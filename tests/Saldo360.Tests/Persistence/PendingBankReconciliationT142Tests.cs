using Microsoft.EntityFrameworkCore;
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
/// T14.2: o valor bancário agendado substitui a estimativa SÓ daquela competência quando há identificação segura. Cobre a exceção de competência sem ocorrência (DMAE), a identidade estável
/// de despesa estimada (CEEE) com suas salvaguardas, previsões avulsas, rejeições diagnosticáveis sem repetição, pagamento de fatura e Pix agendado (nunca viram despesa nem transferência),
/// idempotência e a transição PENDING → POSTED, com "hoje" fixo (01/11/2026).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PendingBankReconciliationT142Tests(PostgresFixture postgres) : IAsyncLifetime
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

    // ---- A: exceção de competência sem ocorrência da recorrência --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACompetenceExceptionBeforeTheRecurrenceStarts_UsesTheBankAmount_WithoutARetroactiveOccurrenceOrAChangedStart()
    {
        var recurrence = await AddRecurrenceAsync("DMAE", 150m, new DateOnly(2026, 12, 11), estimated: true);
        var planned = await AddExceptionAsync(recurrence.Id, Nov, "DMAE — novembro/2026", 138.60m, new DateOnly(2026, 11, 11));
        var debit = await AddBankAsync("dmae-nov", "Débito automático DA DMAE-P AL", 138.60m, new DateOnly(2026, 11, 11), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(1, report.Confirmed);
        await using var db = Db();
        var kept = await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned);
        Assert.Equal((138.60m, AmountConfirmationSource.PluggyAuto, debit), (kept.ConfirmedAmount, kept.ConfirmationSource, kept.AmountSourceTransactionId));
        Assert.Equal(1, await db.FinancialPlannedTransactions.CountAsync());                               // nenhuma ocorrência retroativa criada
        var stored = await db.FinancialRecurrences.AsNoTracking().SingleAsync(r => r.Id == recurrence.Id);
        Assert.Equal((new DateOnly(2026, 12, 11), 150m), (stored.StartDate, stored.Amount));               // início e valor-base intactos
        Assert.Equal(0, await db.FinancialAmountConfirmationEvents.CountAsync(e => e.Kind == AmountConfirmationEventKind.Rejected));
        Assert.Equal(0, (await ProcessAsync()).Total);                                                     // idempotente
    }

    // ---- B: identidade estável de despesa estimada ----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEstimatedExpenseWithAStableIdentifierInTheHistory_IsAssociatedDespiteTheDistantValueAndSixDays_OnlyForThatCompetence()
    {
        var recurrence = await AddRecurrenceAsync("CEEE / energia", 200m, new DateOnly(2026, 11, 10), estimated: true);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "Saída Ceee 000004105761", 144.63m, new DateOnly(2026, 10, 16));
        var debit = await AddBankAsync("ceee-nov", "Saída CEEE 000004105761", 138.56m, new DateOnly(2026, 11, 16), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(1, report.Confirmed);
        await using var db = Db();
        var november = await Obligation(db, recurrence.Id, Nov); var december = await Obligation(db, recurrence.Id, Dec);
        Assert.Equal((138.56m, "PluggyAuto", debit, ObligationSituation.Planned), (november.PlannedAmount, november.ConfirmationSource, november.SourceTransactionId, november.Situation));
        Assert.Equal((200m, ObligationAmountSource.Base), (december.PlannedAmount, december.AmountSource));  // o valor-base das próximas competências não muda
        Assert.Equal(200m, (await db.FinancialRecurrences.AsNoTracking().SingleAsync(r => r.Id == recurrence.Id)).Amount);
        Assert.Equal(0, (await ProcessAsync()).Total);
    }

    [Fact]
    public async Task WithoutTheStableIdentifier_TheDistantAmountIsNeverAssociated_AndTheDiagnosisSaysTheAmountIsTheProblem()
    {
        var recurrence = await AddRecurrenceAsync("CEEE / energia", 200m, new DateOnly(2026, 11, 10), estimated: true);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "Saída Ceee 000004105761", 144.63m, new DateOnly(2026, 10, 16));
        var debit = await AddBankAsync("ceee-sem-id", "Saída CEEE", 138.56m, new DateOnly(2026, 11, 12), PluggyTransactionStatus.Pending);

        var first = await ProcessAsync(); var second = await ProcessAsync();

        Assert.Equal((0, 1, 0), (first.Confirmed, first.Ambiguous, second.Ambiguous));
        await using var db = Db();
        Assert.Equal((200m, ObligationAmountSource.Base), ((await Obligation(db, recurrence.Id, Nov)).PlannedAmount, (await Obligation(db, recurrence.Id, Nov)).AmountSource));
        var diagnosis = await db.FinancialAmountConfirmationEvents.AsNoTracking().SingleAsync();
        Assert.Equal((AmountConfirmationEventKind.Ambiguous, debit, "AmountIncompatible"), (diagnosis.Kind, diagnosis.SourceTransactionId, diagnosis.Reason));
    }

    [Fact]
    public async Task TwoDebitsWithTheSameIdentifier_AreNeverAssociated_ToTheEstimatedExpense()
    {
        var recurrence = await AddRecurrenceAsync("CEEE / energia", 200m, new DateOnly(2026, 11, 10), estimated: true);
        await AddPaidHistoryAsync(recurrence.Id, new DateOnly(2026, 10, 1), "Saída Ceee 000004105761", 144.63m, new DateOnly(2026, 10, 16));
        await AddBankAsync("ceee-a", "Saída CEEE 000004105761", 138.56m, new DateOnly(2026, 11, 16), PluggyTransactionStatus.Pending);
        await AddBankAsync("ceee-b", "Saída CEEE 000004105761", 141.00m, new DateOnly(2026, 11, 17), PluggyTransactionStatus.Pending);

        Assert.Equal(0, (await ProcessAsync()).Confirmed);

        await using var db = Db();
        Assert.Equal(ObligationAmountSource.Base, (await Obligation(db, recurrence.Id, Nov)).AmountSource);
    }

    // ---- D: previsões avulsas -------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStandaloneForecastWithACompatibleDescriptionAndTheSameAmountWithinCents_UsesTheBankAmount_ConsistentlyAcrossAgendaBudgetAndBalance_WithoutDoubleCounting()
    {
        var planned = await AddStandaloneAsync("Seguro do carro", 320.00m, new DateOnly(2026, 11, 8));
        var debit = await AddBankAsync("seguro", "SEGURO DO CARRO", 319.95m, new DateOnly(2026, 11, 9), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(1, report.Confirmed);
        await using var db = Db();
        var stored = await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned);
        Assert.Equal((320.00m, 319.95m, AmountConfirmationSource.PluggyAuto, debit), (stored.Amount, stored.ConfirmedAmount, stored.ConfirmationSource, stored.AmountSourceTransactionId));
        var audit = await db.FinancialAmountConfirmationEvents.AsNoTracking().SingleAsync();
        Assert.Equal((AmountConfirmationEventKind.Confirmed, planned, (Guid?)null), (audit.Kind, audit.PlannedTransactionId, audit.RecurrenceId));

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, new(Nov, new DateOnly(2026, 11, 30), null, null, Today), default);
        var account = await new FinancialAccountRepository(db).SumPendingPlannedAsync(_ws.Id, _account.Id, Today, new DateOnly(2026, 11, 30), default);
        var budget = await BudgetFactReader.ReadAsync(db, _ws.Id, Nov, Dec, default, Today);
        var balance = (await new FinancialCalendarBalanceRepository(db).ReadAsync(_ws.Id, null, new DateOnly(2026, 11, 30), Today, default))!;
        Assert.Equal(319.95m, snapshot.Summary.PendingExpense);
        Assert.Equal(319.95m, account.PlannedExpense);
        Assert.Equal(319.95m, budget.Where(f => f.State == BudgetFactState.Pending).Sum(f => f.Amount));
        Assert.Equal(319.95m, balance.Pending.Sum(p => p.Expense));
        Assert.Equal(PendingBankCoverageState.Covered, Assert.Single(balance.PendingBank!).State);
        Assert.Equal(0, (await ProcessAsync()).Total);
    }

    [Fact]
    public async Task AFiveCentDifferenceAloneNeverAssociatesAStandaloneForecast_WhenTheDescriptionHasNoEvidence()
    {
        var planned = await AddStandaloneAsync("IPTU Casa Salso — parcela 10/10", 319.35m, new DateOnly(2026, 11, 8));
        await AddBankAsync("iptu", "Débito automático DA PM P AL", 319.30m, new DateOnly(2026, 11, 9), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal((0, 1), (report.Confirmed, report.Ambiguous));
        await using var db = Db();
        Assert.Null((await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned)).ConfirmedAmount);
    }

    [Fact]
    public async Task AManualConfirmationOfAStandaloneForecast_IsProtectedFromTheAutomaticMatch()
    {
        var planned = await AddStandaloneAsync("Seguro do carro", 320.00m, new DateOnly(2026, 11, 8));
        await using (var db = Db())
        {
            (await db.FinancialPlannedTransactions.SingleAsync(p => p.Id == planned)).ConfirmCompetenceAmount(321m, _user, Now);
            await db.SaveChangesAsync();
        }
        await AddBankAsync("seguro", "SEGURO DO CARRO", 319.95m, new DateOnly(2026, 11, 9), PluggyTransactionStatus.Pending);

        Assert.Equal(0, (await ProcessAsync()).Confirmed);

        await using var verify = Db();
        var stored = await verify.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned);
        Assert.Equal((321m, true), (stored.ConfirmedAmount, stored.IsManuallyConfirmed));
    }

    [Fact]
    public async Task TheStandaloneConfirmationSurvivesPendingToPosted_WithoutDuplicatingTheExpense()
    {
        var planned = await AddStandaloneAsync("Seguro do carro", 320.00m, new DateOnly(2026, 11, 8));
        var debit = await AddBankAsync("seguro", "SEGURO DO CARRO", 319.95m, new DateOnly(2026, 11, 9), PluggyTransactionStatus.Pending);
        await ProcessAsync();

        await PostAsync(debit, 319.95m, new DateOnly(2026, 11, 9), "SEGURO DO CARRO");
        var after = await ProcessAsync();

        Assert.Equal(0, after.Total);                                                                     // Pending → Posted: o valor confirmado fica; nada é revertido nem recriado
        await using var db = Db();
        Assert.Equal(319.95m, (await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned)).ConfirmedAmount);
        Assert.Equal(1, await db.FinancialPlannedTransactions.CountAsync());
    }

    // ---- E: rejeições diagnosticáveis -----------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARejectedSafeAssociation_IsRecordedOnceWithAStructuredReason_AndIsNeverAnIntegrationFailure()
    {
        var recurrence = await AddRecurrenceAsync("Conta de luz", 200m, new DateOnly(2026, 10, 10), estimated: true);
        var planned = await AddExceptionAsync(recurrence.Id, Nov, "Conta de luz — novembro/2026", 200m, new DateOnly(2026, 11, 10));
        await using (var db = Db()) { (await db.FinancialRecurrences.SingleAsync(r => r.Id == recurrence.Id)).Pause(Now); await db.SaveChangesAsync(); }
        var debit = await AddBankAsync("luz-nov", "CONTA DE LUZ", 188.40m, new DateOnly(2026, 11, 10), PluggyTransactionStatus.Pending);

        var first = await ProcessAsync(); var second = await ProcessAsync(); var third = await ProcessAsync();

        Assert.Equal(0, first.Total + second.Total + third.Total);
        await using var verify = Db();
        var rejected = await verify.FinancialAmountConfirmationEvents.AsNoTracking().SingleAsync(e => e.Kind == AmountConfirmationEventKind.Rejected); // um só, apesar de três sincronizações
        Assert.Equal(("RecurrenceInactive", debit, planned), (rejected.Reason, rejected.SourceTransactionId, rejected.PlannedTransactionId));
        Assert.Contains("188.4", rejected.Details);
        Assert.Null((await verify.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned)).ConfirmedAmount);
    }

    // ---- Proteções: fatura de cartão e Pix agendado -----------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACardInvoiceAutoDebit_IsNeverAssociatedToAnExpenseForecast_NorCountedAsANewExpense()
    {
        var planned = await AddStandaloneAsync("Fatura Itaú Visa", 6463.76m, new DateOnly(2026, 11, 16));
        await AddBankAsync("fatura", "Débito automático FATURA PER", 6463.76m, new DateOnly(2026, 11, 16), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(0, report.Total);                                                                    // nem associação nem diagnóstico de ambiguidade
        await using var db = Db();
        Assert.Null((await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned)).ConfirmedAmount);
        Assert.Equal(0, await db.FinancialAmountConfirmationEvents.CountAsync());
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());                                     // o pendente não cria despesa
    }

    [Fact]
    public async Task AScheduledPix_IsNotInferredAsAnInternalTransfer_NorAssociatedByAmountAlone()
    {
        var planned = await AddStandaloneAsync("Reserva anual", 4200m, new DateOnly(2026, 12, 14));
        await AddBankAsync("pix", "Pix agendado DIEGO SILVA DA", 4200m, new DateOnly(2026, 12, 14), PluggyTransactionStatus.Pending);

        var report = await ProcessAsync();

        Assert.Equal(0, report.Confirmed);                                                                // valor igual sozinho nunca associa
        await using var db = Db();
        Assert.Null((await db.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == planned)).ConfirmedAmount);
        Assert.Equal(0, await db.FinancialTransfers.CountAsync());                                        // sem evidência da conta destinatária, nenhuma transferência
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
    }

    // ---- apoio -----------------------------------------------------------------------------------------------------------------------------------------------------------------------------

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

    private async Task PostAsync(Guid id, decimal amount, DateOnly date, string description)
    {
        await using var db = Db();
        var tx = await db.PluggyImportedTransactions.SingleAsync(t => t.Id == id);
        tx.Observe(new PluggyTransactionSnapshot(tx.PluggyTransactionId, tx.PluggyAccountId, tx.AccountType, tx.AccountLabel, tx.Date, -amount, tx.Currency, tx.Description, tx.DescriptionRaw, tx.Type, PluggyTransactionStatus.Posted,
            tx.OperationType, tx.Category, null, null, null, null, null, null), _run.Id, Now);
        var movement = FinancialTransaction.CreateFromPluggy(_ws.Id, _account.Id, _category.Id, CategoryType.Expense, description, amount, date, _user, Now);
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

    private async Task<Guid> AddStandaloneAsync(string description, decimal amount, DateOnly due)
    {
        var details = PlannedTransactionDetails.TryCreate(_account.Id, _category.Id, nameof(CategoryType.Expense), description, amount, due, out _, nameof(ExpenseNature.Fixed))!;
        var planned = FinancialPlannedTransaction.Create(_ws.Id, details, _user, Now);
        await using var db = Db(); db.FinancialPlannedTransactions.Add(planned); await db.SaveChangesAsync();
        return planned.Id;
    }

    private async Task<Guid> AddExceptionAsync(Guid recurrenceId, DateOnly competence, string description, decimal amount, DateOnly due)
    {
        var details = PlannedTransactionDetails.TryCreate(_account.Id, _category.Id, nameof(CategoryType.Expense), description, amount, due, out _, nameof(ExpenseNature.Fixed))!;
        var planned = FinancialPlannedTransaction.Create(_ws.Id, details, _user, Now);
        planned.MarkAsCompetenceException(recurrenceId, competence);
        await using var db = Db(); db.FinancialPlannedTransactions.Add(planned); await db.SaveChangesAsync();
        return planned.Id;
    }

    private async Task<FinancialRecurrence> AddRecurrenceAsync(string description, decimal amount, DateOnly start, bool estimated = false)
    {
        var recurrence = FinancialRecurrence.Create(_ws.Id, new RecurrenceDetails(_account.Id, _category.Id, CategoryType.Expense, description, amount,
            estimated, RecurrenceFrequency.Monthly, 1, start, null, null, start.Day, null, ExpenseNature.Fixed), Now);
        await using var db = Db(); db.FinancialRecurrences.Add(recurrence); await db.SaveChangesAsync();
        return recurrence;
    }

    private async Task<FixedObligation> Obligation(Saldo360DbContext db, Guid recurrenceId, DateOnly competence) =>
        (await FixedObligationReader.LoadAsync(db, _ws.Id, new DateOnly(2026, 9, 1), new DateOnly(2027, 2, 28), Today, default)).Single(o => o.RecurrenceId == recurrenceId && o.Competence == competence);

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_cs);
}
