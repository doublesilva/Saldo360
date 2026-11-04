using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Saldo360.Application.Budgets;
using Saldo360.Application.Calendar;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Accounts;
using Saldo360.Infrastructure.Budgets;
using Saldo360.Infrastructure.Calendar;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Recurrences;
using Saldo360.Infrastructure.Users;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

/// <summary>
/// PROJ-05: o valor informado pelo banco (Pluggy) vira o valor confirmado da competência de uma despesa fixa SEM ação do usuário quando a associação é segura, com "hoje" fixo (01/11/2026).
/// Cobre confirmação automática por competência, valor-base preservado, pendente → efetivado, cancelamento definitivo × ausência temporária, troca do identificador da transação,
/// correção de valor, proteção da confirmação manual, idempotência/concorrência e consistência entre Agenda, orçamento, conta e saldo projetado.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PendingBankConfirmationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 11, 1);
    private static readonly DateOnly Nov = new(2026, 11, 1);
    private static readonly DateOnly Dec = new(2026, 12, 1);
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private string _cs = null!;
    private Guid _user;
    private FinancialWorkspace _ws = null!;
    private FinancialAccount _account = null!;
    private FinancialCategory _category = null!;
    private PluggyItemLink _item = null!;
    private PluggySyncRun _run = null!;
    private PluggyAccountLink _link = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        _user = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        _ws = FinancialWorkspace.CreatePersonal(_user, Now);
        await new FinancialWorkspaceRepository(db).AddAsync(_ws, default);
        _account = FinancialAccount.Create(_ws.Id, AccountDetails.TryCreate("Itaú", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, 1000m, new DateOnly(2026, 9, 1), out _)!, _user, Now);
        _category = FinancialCategory.Create(_ws.Id, "Moradia", CategoryType.Expense, _user, Now);
        db.AddRange(_account, _category);
        await db.SaveChangesAsync();
        _item = PluggyItemLink.Create(_ws.Id, _user, Guid.NewGuid().ToString("D"), "Synthetic", Now);
        _run = PluggySyncRun.Start(_ws.Id, _item.Id, _user, Now);
        _link = PluggyAccountLink.Create(_ws.Id, _item.Id, "acc-1", PluggyLinkedAccountKind.Bank, _account.Id, null, "Itaú", null, "BANK", "CHECKING_ACCOUNT", null, _user, Now);
        db.AddRange(_item, _run, _link);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SafeMatch_ConfirmsOnlyThatCompetenceAutomatically_AndKeepsTheBase()
    {
        var recurrence = await AddRecurrenceAsync("Conta de luz", 200m, estimated: true);
        var debit = await AddBankAsync("luz-nov", "CONTA DE LUZ", 188.40m, new DateOnly(2026, 11, 10), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(1, report.Confirmed);
        await using var db = Db();
        var november = await Obligation(db, recurrence.Id, Nov);
        Assert.Equal((188.40m, ObligationAmountSource.Confirmed, "PluggyAuto", debit, ObligationSituation.Planned), (november.PlannedAmount, november.AmountSource, november.ConfirmationSource, november.SourceTransactionId, november.Situation));
        Assert.Equal((200m, ObligationAmountSource.Base), ((await Obligation(db, recurrence.Id, Dec)).PlannedAmount, (await Obligation(db, recurrence.Id, Dec)).AmountSource));
        Assert.Equal(200m, (await db.FinancialRecurrences.AsNoTracking().SingleAsync(r => r.Id == recurrence.Id)).Amount);
        var audit = await db.FinancialAmountConfirmationEvents.AsNoTracking().SingleAsync();
        Assert.Equal((AmountConfirmationEventKind.Confirmed, AmountConfirmationSource.PluggyAuto, 188.40m, debit), (audit.Kind, audit.Source, audit.NewAmount, audit.SourceTransactionId));
    }

    [Fact]
    public async Task RecurrenceHistory_ConfirmsTheCrediarioWithoutRelaxingTheMatcher_AndTheNextMonthKeepsTheBase()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        var debit = await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(1, report.Confirmed);
        await using var db = Db();
        var november = await Obligation(db, recurrence.Id, Nov); var december = await Obligation(db, recurrence.Id, Dec);
        Assert.Equal((748.31m, "PluggyAuto", debit), (november.PlannedAmount, november.ConfirmationSource, november.SourceTransactionId));
        Assert.Equal((750m, ObligationAmountSource.Base, true), (december.PlannedAmount, december.AmountSource, december.IsVirtual));
        Assert.Equal("Crediário Solar", (await db.FinancialRecurrences.AsNoTracking().SingleAsync(r => r.Id == recurrence.Id)).Description); // nome da recorrência intacto
    }

    [Fact]
    public async Task WithoutHistoryEvidence_TheGenericBoletoIsNotAssociated_AndTheDiagnosisIsRecordedOnce()
    {
        var recurrence = await AddRecurrenceAsync("ASTTI", 140m);
        var debit = await AddBankAsync("dda-nov", "Pagamento de boleto DDA PAG TIT", 140m, new DateOnly(2026, 11, 10), PluggyTransactionStatus.Pending);

        var first = await ProcessAsync();
        var second = await ProcessAsync();

        Assert.Equal((0, 1, 0), (first.Confirmed, first.Ambiguous, second.Ambiguous)); // diagnóstico registrado uma vez; reprocessar não duplica
        await using var db = Db();
        Assert.Equal((140m, ObligationAmountSource.Base), ((await Obligation(db, recurrence.Id, Nov)).PlannedAmount, (await Obligation(db, recurrence.Id, Nov)).AmountSource));
        var diagnosis = await db.FinancialAmountConfirmationEvents.AsNoTracking().SingleAsync();
        Assert.Equal((AmountConfirmationEventKind.Ambiguous, debit, "NoHistoryEvidence"), (diagnosis.Kind, diagnosis.SourceTransactionId, diagnosis.Reason));
        Assert.Contains("\"impactAmount\":140", diagnosis.Details);
        Assert.Contains("ASTTI", diagnosis.Details);
    }

    [Fact]
    public async Task AsttiWithHistory_IsConfirmedByTheDdaBoleto_EvenWithAnotherBoletoOfSimilarValueOnTheSameDay()
    {
        var recurrence = await AddRecurrenceAsync("ASTTI", 140m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "Pagamento de boleto DDA PAG TIT", 140m, new DateOnly(2026, 10, 10));
        var dda = await AddBankAsync("dda-nov", "Pagamento de boleto DDA PAG TIT", 140m, new DateOnly(2026, 11, 10), PluggyTransactionStatus.Pending);
        await AddBankAsync("itau-nov", "Pagamento de boleto ITAU UNIBANCO HOLDING S.A.", 135.58m, new DateOnly(2026, 11, 10), PluggyTransactionStatus.Pending);

        await ProcessAsync();

        await using var db = Db();
        var november = await Obligation(db, recurrence.Id, Nov);
        Assert.Equal((140m, "PluggyAuto", dda), (november.PlannedAmount, november.ConfirmationSource, november.SourceTransactionId));
    }

    [Fact]
    public async Task UnreconciledPriorMonth_StillTeachesTheBankDescription_SoTheCrediarioIsConfirmedAndThenSettledByLink()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddUnlinkedPostedAsync("Saída CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 10, 6)); // outubro: o matcher nunca a associou (nome 50%, valor ≠ 750,00)
        var debit = await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(1, report.Confirmed);
        await using (var db = Db())
        {
            var november = await Obligation(db, recurrence.Id, Nov);
            Assert.Equal((748.31m, "PluggyAuto", debit), (november.PlannedAmount, november.ConfirmationSource, november.SourceTransactionId));
            Assert.Equal(750m, (await Obligation(db, recurrence.Id, Dec)).PlannedAmount);
        }
        await PostAsync(debit, 748.31m, new DateOnly(2026, 11, 6));
        await using (var db = Db()) await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default);
        await using var verify = Db();
        var paid = await Obligation(verify, recurrence.Id, Nov);
        var snapshot = await new FinancialCalendarRepository(verify).ListAsync(_ws.Id, new(Nov, new DateOnly(2026, 11, 30), null, null, Today), default);
        Assert.Equal((ObligationSituation.Realized, 748.31m), (paid.Situation, paid.ActualAmount));
        Assert.Equal((748.31m, 0m), (snapshot.Summary.RealizedExpense, snapshot.Summary.PendingExpense)); // sem a duplicidade de 750,00 pendente + 748,31 pago
    }

    [Fact]
    public async Task UnreconciledPriorMonth_WithAnAmbiguousOrDistantMovement_TeachesNothing()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddUnlinkedPostedAsync("Saída CREDIARIO AUTOM", 600m, new DateOnly(2026, 10, 6)); // valor fora da tolerância
        await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal((0, 1), (report.Confirmed, report.Ambiguous));
        await using var db = Db();
        Assert.Equal(750m, (await Obligation(db, recurrence.Id, Nov)).PlannedAmount);
    }

    private async Task AddUnlinkedPostedAsync(string description, decimal amount, DateOnly date)
    {
        await using var db = Db();
        db.FinancialTransactions.Add(FinancialTransaction.CreateFromPluggy(_ws.Id, _account.Id, _category.Id, CategoryType.Expense, description, amount, date, _user, Now));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task PendingToPosted_KeepsTheConfirmedValue_AndPaymentSettlesWithoutANewExpense()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        var debit = await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);
        await ProcessAsync();

        await PostAsync(debit, 748.31m, new DateOnly(2026, 11, 6));
        var report = await ProcessAsync();
        await using (var db = Db()) await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default);

        Assert.Equal(0, report.Total); // efetivar não muda o valor confirmado
        await using var verify = Db();
        var november = await Obligation(verify, recurrence.Id, Nov);
        var snapshot = await new FinancialCalendarRepository(verify).ListAsync(_ws.Id, new(Nov, new DateOnly(2026, 11, 30), null, null, Today), default);
        Assert.Equal((ObligationSituation.Realized, 748.31m), (november.Situation, november.ActualAmount));
        Assert.Equal((748.31m, 0m), (snapshot.Summary.RealizedExpense, snapshot.Summary.PendingExpense)); // paga uma vez, nada pendente
        Assert.Equal(748.31m, (await verify.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.ExceptionRecurrenceId == recurrence.Id)).ConfirmedAmount);
    }

    [Fact]
    public async Task TemporaryAbsence_DoesNotRevertTheAutomaticConfirmation()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        var debit = await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);
        await ProcessAsync();

        await MissAsync(debit, Now.AddHours(-1)); // uma ausência isolada: sem evidência de cancelamento
        var report = await ProcessAsync();

        Assert.Equal(0, report.Reverted);
        await using var db = Db();
        Assert.Equal((748.31m, "PluggyAuto"), ((await Obligation(db, recurrence.Id, Nov)).PlannedAmount, (await Obligation(db, recurrence.Id, Nov)).ConfirmationSource));
    }

    [Fact]
    public async Task DefinitiveCancellation_RevertsTheAutomaticConfirmationToTheProjection()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        var debit = await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);
        await ProcessAsync();

        await MissAsync(debit, Now.AddHours(-72)); await MissAsync(debit, Now.AddHours(-2)); await MissAsync(debit, Now.AddHours(-1)); // ausente em 3 coletas por mais de 48h
        var report = await ProcessAsync();

        Assert.Equal(1, report.Reverted);
        await using var verify = Db();
        var november = await Obligation(verify, recurrence.Id, Nov);
        Assert.Equal((750m, null, null), (november.PlannedAmount, november.SourceTransactionId, november.ConfirmationSource));
        Assert.Contains(await verify.FinancialAmountConfirmationEvents.AsNoTracking().ToListAsync(), e => e.Kind == AmountConfirmationEventKind.Reverted && e.SourceTransactionId == debit);
        Assert.Equal(750m, (await Obligation(verify, recurrence.Id, Dec)).PlannedAmount);
    }


    [Fact]
    public async Task ReplacedTransactionIdentifier_KeepsTheObligationAndUpdatesTheBankReference()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        var original = await AddBankAsync("crediario-pending", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);
        await ProcessAsync();
        await MissAsync(original, Now.AddHours(-72)); await MissAsync(original, Now.AddHours(-2)); await MissAsync(original, Now.AddHours(-1));
        var replacement = await AddBankAsync("crediario-posted", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Posted);

        var report = await ProcessAsync();

        Assert.Equal((1, 0), (report.SourceReplaced, report.Reverted));
        await using var db = Db();
        var november = await Obligation(db, recurrence.Id, Nov);
        Assert.Equal((748.31m, replacement, "PluggyAuto"), (november.PlannedAmount, november.SourceTransactionId, november.ConfirmationSource));
        Assert.Contains(await db.FinancialAmountConfirmationEvents.AsNoTracking().ToListAsync(), e => e.Kind == AmountConfirmationEventKind.SourceReplaced && e.SourceTransactionId == replacement);
    }

    [Fact]
    public async Task ChangedPendingValue_UpdatesTheAutomaticConfirmation_AndRecordsTheChange()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        var debit = await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);
        await ProcessAsync();

        await ChangeAmountAsync(debit, 749.00m);
        var report = await ProcessAsync();

        Assert.Equal(1, report.Updated);
        await using var db = Db();
        Assert.Equal(749.00m, (await Obligation(db, recurrence.Id, Nov)).PlannedAmount);
        var change = await db.FinancialAmountConfirmationEvents.AsNoTracking().SingleAsync(e => e.Kind == AmountConfirmationEventKind.Updated);
        Assert.Equal((748.31m, 749.00m), (change.PreviousAmount, change.NewAmount));
    }

    [Fact]
    public async Task ManualConfirmationAndManualLink_AreNeverOverwrittenNorRevertedBySync()
    {
        var manual = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(manual.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        await ConfirmManuallyAsync(manual.Id, Nov, 740m);
        var linked = await AddRecurrenceAsync("Conta de água", 100m, estimated: true);
        var linkedDebit = await AddBankAsync("agua-nov", "SANEAMENTO MUNICIPAL", 97m, new DateOnly(2026, 11, 12), PluggyTransactionStatus.Pending);
        await using (var db = Db()) Assert.Equal(CompetenceAmountStatus.Ok, (await new FinancialRecurrenceMaterializationRepository(db).LinkBankTransactionAsync(await db.FinancialRecurrences.SingleAsync(r => r.Id == linked.Id), Nov, linkedDebit, _user, Now, default)).Status);
        await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);

        await ProcessAsync();
        await MissAsync(linkedDebit, Now.AddHours(-72)); await MissAsync(linkedDebit, Now.AddHours(-2)); await MissAsync(linkedDebit, Now.AddHours(-1));
        var report = await ProcessAsync();

        Assert.Equal(0, report.Reverted);
        await using var verify = Db();
        var crediario = await Obligation(verify, manual.Id, Nov); var water = await Obligation(verify, linked.Id, Nov);
        Assert.Equal((740m, "Manual"), (crediario.PlannedAmount, crediario.ConfirmationSource));
        Assert.Equal((97m, "ManualLink", linkedDebit), (water.PlannedAmount, water.ConfirmationSource, water.SourceTransactionId));
    }

    [Fact]
    public async Task ManualLink_AppliesTheBankValueWithoutASecondConfirmation_AndTheDebitIsNeverCountedAgain()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        var debit = await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);
        await ProcessAsync(); // sem histórico: ambíguo, nada associado
        await using (var db = Db())
        {
            Assert.Equal(750m, (await Obligation(db, recurrence.Id, Nov)).PlannedAmount);
            var candidates = await new FinancialRecurrenceMaterializationRepository(db).BankCandidatesAsync(await db.FinancialRecurrences.SingleAsync(r => r.Id == recurrence.Id), Nov, default);
            Assert.Equal(debit, Assert.Single(candidates).TransactionId);
        }

        CompetenceAmountOutcome outcome;
        await using (var db = Db()) outcome = await new FinancialRecurrenceMaterializationRepository(db).LinkBankTransactionAsync(await db.FinancialRecurrences.SingleAsync(r => r.Id == recurrence.Id), Nov, debit, _user, Now, default);

        Assert.Equal((CompetenceAmountStatus.Ok, "ManualLink", 748.31m), (outcome.Status, outcome.Result!.Source, outcome.Result.ConfirmedAmount));
        await using var verify = Db();
        var balance = (await new FinancialCalendarBalanceRepository(verify).ReadAsync(_ws.Id, null, new DateOnly(2026, 11, 30), Today, default))!;
        Assert.Equal(748.31m, balance.Pending.Sum(p => p.Expense)); // 748,31 uma vez: o débito vinculado não soma de novo
        Assert.Equal(PendingBankCoverageState.Covered, Assert.Single(balance.PendingBank!).State);
        var again = await ProcessAsync();
        Assert.Equal(0, again.Total); // o vínculo manual não é refeito nem disputado
    }

    [Fact]
    public async Task Idempotent_RepeatedAndConcurrentPasses_ChangeNothingAndNeverDuplicate()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);

        var reports = await Task.WhenAll(ProcessAsync(), ProcessAsync(), ProcessAsync());
        var repeated = await ProcessAsync();

        Assert.Equal(1, reports.Sum(r => r.Confirmed));
        Assert.Equal(0, repeated.Total);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialPlannedTransactions.CountAsync(p => p.ExceptionRecurrenceId == recurrence.Id));
        Assert.Equal(1, await db.FinancialAmountConfirmationEvents.CountAsync(e => e.Kind == AmountConfirmationEventKind.Confirmed));
    }

    [Fact]
    public async Task AgendaBudgetAccountAndProjectedBalance_AgreeOnTheAutomaticValue_WithoutCountingTheDebitAgain()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "SAIDA CREDIARIO AUTOM", 750m, new DateOnly(2026, 10, 6));
        await AddBankAsync("crediario-nov", "SAIDA CREDIARIO AUTOM", 748.31m, new DateOnly(2026, 11, 6), PluggyTransactionStatus.Pending);
        await ProcessAsync();
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, new(Nov, new DateOnly(2026, 11, 30), null, null, Today), default);
        var account = await new FinancialAccountRepository(db).SumPendingPlannedAsync(_ws.Id, _account.Id, Today, new DateOnly(2026, 11, 30), default);
        var budget = await BudgetFactReader.ReadAsync(db, _ws.Id, Nov, Dec, default, Today);
        var balance = (await new FinancialCalendarBalanceRepository(db).ReadAsync(_ws.Id, null, new DateOnly(2026, 11, 30), Today, default))!;
        var item = Assert.Single(snapshot.Items, i => i.RecurrenceId == recurrence.Id);

        Assert.Equal(748.31m, snapshot.Summary.PendingExpense);
        Assert.Equal(748.31m, account.PlannedExpense);
        Assert.Equal(748.31m, budget.Where(f => f.State == BudgetFactState.Pending).Sum(f => f.Amount));
        Assert.Equal(748.31m, balance.Pending.Sum(p => p.Expense));
        Assert.Equal(PendingBankCoverageState.Covered, Assert.Single(balance.PendingBank!).State);
        Assert.Equal((748.31m, 750m, "Confirmed", "PluggyAuto"), (item.Amount, item.BaseAmount, item.AmountSource, item.ConfirmationSource));
    }

    [Fact]
    public async Task Migration_IsAdditive_AndKeepsExistingConfirmationsAsManual()
    {
        var recurrence = await AddRecurrenceAsync("Plano", 100m);
        await ConfirmManuallyAsync(recurrence.Id, Nov, 90m);
        await using (var db = Db())
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261104111511_AddCompetenceConfirmedAmount"); // desfaz só a PROJ-05
            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'financial_planned_transactions' AND column_name = 'amount_source_transaction_id'").SingleAsync());
            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'financial_amount_confirmation_events'").SingleAsync());
            await migrator.MigrateAsync(); // reaplica até a última
        }

        await using var verify = Db();
        var kept = await verify.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.ExceptionRecurrenceId == recurrence.Id);
        Assert.Equal((90m, null, null), (kept.ConfirmedAmount, kept.AmountSourceTransactionId, kept.ConfirmationSource)); // origem nula = manual (PROJ-04), nada reescrito
        Assert.True(kept.IsManuallyConfirmed);
    }

    // ---- apoio -----------------------------------------------------------------------------------------------------------------------------------------------------------------

    private async Task<PendingBankConfirmationReport> ProcessAsync()
    {
        await using var db = Db();
        return await PendingBankConfirmationProcessor.ProcessAsync(db, _ws.Id, Today, Now, default);
    }

    private async Task<Guid> AddBankAsync(string externalId, string description, decimal amount, DateOnly date, PluggyTransactionStatus status)
    {
        await using var db = Db();
        var snapshot = new PluggyTransactionSnapshot(externalId, "acc-1", "BANK", "Itaú", new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), -amount, "BRL", description, description, "DEBIT", status,
            "PIX", null, null, null, null, null, null);
        var tx = PluggyImportedTransaction.Create(_ws.Id, _item.Id, snapshot, _run.Id, Now);
        db.Add(tx); db.Add(PluggyPosting.Create(_ws.Id, tx.Id, _link.Id, Now));
        await db.SaveChangesAsync();
        return tx.Id;
    }

    private async Task MissAsync(Guid id, DateTimeOffset at)
    {
        await using var db = Db();
        (await db.PluggyImportedTransactions.SingleAsync(t => t.Id == id)).MarkMissing(at);
        await db.SaveChangesAsync();
    }

    private async Task ChangeAmountAsync(Guid id, decimal amount)
    {
        await using var db = Db();
        var tx = await db.PluggyImportedTransactions.SingleAsync(t => t.Id == id);
        tx.Observe(new PluggyTransactionSnapshot(tx.PluggyTransactionId, tx.PluggyAccountId, tx.AccountType, tx.AccountLabel, tx.Date, -amount, tx.Currency, tx.Description, tx.DescriptionRaw, tx.Type, tx.Status, tx.OperationType,
            tx.Category, null, null, null, null, null, null), _run.Id, Now);
        await db.SaveChangesAsync();
    }

    /// <summary>Efetiva: a transação importada passa a Posted e a postagem gera o lançamento realizado (mesmo caminho do motor), sem nova despesa.</summary>
    private async Task PostAsync(Guid id, decimal amount, DateOnly date)
    {
        await using var db = Db();
        var tx = await db.PluggyImportedTransactions.SingleAsync(t => t.Id == id);
        tx.Observe(new PluggyTransactionSnapshot(tx.PluggyTransactionId, tx.PluggyAccountId, tx.AccountType, tx.AccountLabel, tx.Date, -amount, tx.Currency, tx.Description, tx.DescriptionRaw, tx.Type, PluggyTransactionStatus.Posted,
            tx.OperationType, tx.Category, null, null, null, null, null, null), _run.Id, Now);
        var movement = FinancialTransaction.CreateFromPluggy(_ws.Id, _account.Id, _category.Id, CategoryType.Expense, "SAIDA CREDIARIO AUTOM", amount, date, _user, Now);
        db.FinancialTransactions.Add(movement);
        (await db.PluggyPostings.SingleAsync(p => p.ImportedTransactionId == id)).ApplyBankMovement(movement.Id, amount, date, CategoryType.Expense, Now);
        await db.SaveChangesAsync();
    }

    private async Task AddPaidHistoryAsync(Guid recurrenceId, DateOnly competence, string description, decimal amount, DateOnly date)
    {
        await using var db = Db();
        var tx = FinancialTransaction.CreateFromPluggy(_ws.Id, _account.Id, _category.Id, CategoryType.Expense, description, amount, date, _user, Now);
        db.FinancialTransactions.Add(tx);
        db.FinancialObligationSettlements.Add(FinancialObligationSettlement.Create(_ws.Id, recurrenceId, competence, null, tx.Id, SettlementOrigin.Manual, Now));
        await db.SaveChangesAsync();
    }

    private async Task ConfirmManuallyAsync(Guid recurrenceId, DateOnly competence, decimal amount)
    {
        await using var db = Db();
        var outcome = await new FinancialRecurrenceMaterializationRepository(db).ConfirmCompetenceAmountAsync(await db.FinancialRecurrences.SingleAsync(r => r.Id == recurrenceId), competence, amount, _user, Now, default);
        Assert.Equal(CompetenceAmountStatus.Ok, outcome.Status);
    }

    private async Task<FixedObligation> Obligation(Saldo360DbContext db, Guid recurrenceId, DateOnly competence) =>
        (await FixedObligationReader.LoadAsync(db, _ws.Id, new DateOnly(2026, 9, 1), new DateOnly(2027, 2, 28), Today, default)).Single(o => o.RecurrenceId == recurrenceId && o.Competence == competence);

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_cs);

    private async Task<FinancialRecurrence> AddRecurrenceAsync(string description, decimal amount, bool estimated = false)
    {
        var start = new DateOnly(2026, 10, description == "ASTTI" ? 10 : description == "Conta de luz" ? 10 : description == "Conta de água" ? 12 : 6);
        var recurrence = FinancialRecurrence.Create(_ws.Id, new RecurrenceDetails(_account.Id, _category.Id, CategoryType.Expense, description, amount,
            estimated, RecurrenceFrequency.Monthly, 1, start, null, null, start.Day, null, ExpenseNature.Fixed), Now);
        await using var db = Db(); db.FinancialRecurrences.Add(recurrence); await db.SaveChangesAsync();
        return recurrence;
    }
}
