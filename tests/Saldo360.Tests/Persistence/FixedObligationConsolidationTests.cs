using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Saldo360.Application.Calendar;
using Saldo360.Application.Budgets;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
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
/// Consolidação de despesas fixas no PostgreSQL real, com "hoje" fixo (01/11/2026): previsto x realizado vinculado, exceção de competência,
/// histórico a revisar, ocorrências virtuais, vínculo automático, unicidade e isolamento entre workspaces. Tudo determinístico.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Recurrences")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FixedObligationConsolidationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 11, 1);
    private static readonly DateOnly NovFirst = new(2026, 11, 1);
    private static readonly DateOnly NovLast = new(2026, 11, 30);
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private string _cs = null!;
    private Guid _user;
    private FinancialWorkspace _ws = null!;
    private FinancialWorkspace _other = null!;
    private FinancialAccount _account = null!;
    private FinancialCategory _category = null!;
    private FinancialAccount _otherAccount = null!;
    private FinancialCategory _otherCategory = null!;

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        _user = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        _ws = FinancialWorkspace.CreatePersonal(_user, Now);
        var user2 = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "b", "B", CancellationToken.None);
        _other = FinancialWorkspace.CreatePersonal(user2, Now);
        var repo = new FinancialWorkspaceRepository(db);
        await repo.AddAsync(_ws, default); await repo.AddAsync(_other, default);
        _account = Account(_ws); _otherAccount = Account(_other);
        _category = FinancialCategory.Create(_ws.Id, "Moradia", CategoryType.Expense, _user, Now);
        _otherCategory = FinancialCategory.Create(_other.Id, "Moradia", CategoryType.Expense, user2, Now);
        db.AddRange(_account, _otherAccount, _category, _otherCategory);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FutureRecurrence_AppearsWithoutMaterialization_AndNothingIsWrittenOnRead()
    {
        var recurrence = await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, Filter(), default);

        var item = Assert.Single(snapshot.Items, i => i.Source == FinancialCalendarSource.RecurrenceOccurrence);
        Assert.Equal((200m, FinancialCalendarStatus.Pending, "Planned", recurrence.Id), (item.Amount, item.Status, item.ReconciliationStatus, item.RecurrenceId));
        Assert.Equal(200m, snapshot.Summary.PendingExpense);
        Assert.Equal(0, await db.FinancialRecurrenceMaterializations.CountAsync());
        Assert.Equal(0, await db.FinancialPlannedTransactions.CountAsync());
    }

    [Fact]
    public async Task OctoberOccurrence_IsNeedsReview_NotOverdue_AndNotInProjectedFlow()
    {
        await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        var octoberPlanned = FinancialPlannedTransaction.Create(_ws.Id, Details(300m, new DateOnly(2026, 10, 20), "IPTU"), _user, Now);
        octoberPlanned.MarkHistoricalReview();
        await using (var seed = Db()) { seed.FinancialPlannedTransactions.Add(octoberPlanned); await seed.SaveChangesAsync(); }
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, new FinancialCalendarFilter(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, null, Today), default);
        var review = snapshot.Items.Where(i => i.Status == FinancialCalendarStatus.NeedsReview).ToList();
        var balance = await new FinancialCalendarBalanceRepository(db).ReadAsync(_ws.Id, null, new DateOnly(2026, 12, 31), Today, default);
        var account = await new FinancialAccountRepository(db).SumPendingPlannedAsync(_ws.Id, _account.Id, Today, new DateOnly(2026, 12, 31), default);

        Assert.Equal(2, review.Count);
        Assert.Equal((0m, 0m), (snapshot.Summary.OverdueExpense, snapshot.Summary.PendingExpense));
        Assert.DoesNotContain(balance!.Pending, p => p.Date < NovFirst);
        Assert.Equal(0m, account.OverdueExpense);
    }

    [Fact]
    public async Task PaymentLinked_ConsolidatesOnce_WithoutResidual_AndFlowNoLongerCountsThePlanned()
    {
        var recurrence = await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10), estimated: true);
        var tx = await AddPluggyTransactionAsync("PIX ALUGUEL JOAO", 184.50m, new DateOnly(2026, 11, 10));
        await LinkAsync(recurrence.Id, NovFirst, null, tx.Id);
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, Filter(), default);
        var obligations = await FixedObligationReader.LoadAsync(db, _ws.Id, NovFirst, NovLast, Today, default);

        var paid = Assert.Single(obligations);
        Assert.Equal((ObligationSituation.Realized, 184.50m, 200m), (paid.Situation, paid.Consolidated, paid.PlannedAmount));
        Assert.Equal(184.50m, FixedObligationIndicators.FixasConsolidadas(obligations));
        Assert.Equal(0m, FixedObligationIndicators.FixasPrevistasPendentes(obligations));
        Assert.Equal(0m, snapshot.Summary.PendingExpense); // sem resíduo de 15,50 e sem previsão duplicada
        var only = Assert.Single(snapshot.Items); // a movimentação é o único evento (não cria evento equivalente)
        Assert.Equal((tx.Id, "Reconciled", 184.50m, tx.Id), (only.Id, only.ReconciliationStatus, only.ActualAmount, only.TransactionId));
        Assert.Equal(184.50m, snapshot.Summary.RealizedExpense); // contado uma única vez
    }

    [Fact]
    public async Task UnlinkedPluggyPayment_RemainsAnIndependentRealizedExpense()
    {
        await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        var tx = await AddPluggyTransactionAsync("COMPRA MERCADO", 80m, new DateOnly(2026, 11, 5));
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, Filter(), default);

        Assert.Equal(80m, snapshot.Summary.RealizedExpense);
        Assert.Null(snapshot.Items.Single(i => i.Id == tx.Id).ReconciliationStatus);
        Assert.Equal(200m, snapshot.Summary.PendingExpense);
    }

    [Fact]
    public async Task CompetenceException_ReplacesTheRecurrence_NeverSums()
    {
        var recurrence = await AddRecurrenceAsync("Plano", 100m, new DateOnly(2026, 10, 10));
        var exception = FinancialPlannedTransaction.Create(_ws.Id, Details(150m, new DateOnly(2026, 11, 12), "Plano"), _user, Now);
        exception.MarkAsCompetenceException(recurrence.Id, NovFirst);
        await using (var seed = Db()) { seed.FinancialPlannedTransactions.Add(exception); await seed.SaveChangesAsync(); }
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, Filter(), default);
        var account = await new FinancialAccountRepository(db).SumPendingPlannedAsync(_ws.Id, _account.Id, Today, NovLast, default);
        var budget = await BudgetFactReader.ReadAsync(db, _ws.Id, NovFirst, NovFirst.AddMonths(1), default, Today);

        Assert.Equal(150m, snapshot.Summary.PendingExpense);
        Assert.Equal(150m, account.PlannedExpense);
        Assert.Equal(150m, budget.Where(f => f.State == BudgetFactState.Pending).Sum(f => f.Amount));
        Assert.DoesNotContain(snapshot.Items, i => i.Source == FinancialCalendarSource.RecurrenceOccurrence);
    }

    [Fact]
    public async Task MaterializedOccurrence_SuppressesTheVirtualOne()
    {
        var recurrence = await AddRecurrenceAsync("Plano", 100m, new DateOnly(2026, 11, 10), until: new DateOnly(2026, 11, 30));
        await using (var seed = Db())
        {
            var planned = FinancialPlannedTransaction.Create(_ws.Id, Details(100m, new DateOnly(2026, 11, 10), "Plano"), _user, Now);
            seed.FinancialPlannedTransactions.Add(planned);
            seed.FinancialRecurrenceMaterializations.Add(FinancialRecurrenceMaterialization.Create(_ws.Id, recurrence.Id, 1, new DateOnly(2026, 11, 10), planned.Id, Now));
            await seed.SaveChangesAsync();
        }
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, Filter(), default);

        Assert.Equal(100m, snapshot.Summary.PendingExpense);
        Assert.Single(snapshot.Items);
    }

    [Fact]
    public async Task AllConsumers_AgreeOnTheSamePendingFixedExpense()
    {
        await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        var paid = await AddRecurrenceAsync("Internet", 100m, new DateOnly(2026, 10, 15));
        var tx = await AddPluggyTransactionAsync("INTERNET FIBRA", 100m, new DateOnly(2026, 11, 15));
        await LinkAsync(paid.Id, NovFirst, null, tx.Id);
        await using var db = Db();

        var agenda = (await new FinancialCalendarRepository(db).ListAsync(_ws.Id, Filter(), default)).Summary.PendingExpense;
        var account = (await new FinancialAccountRepository(db).SumPendingPlannedAsync(_ws.Id, _account.Id, Today, NovLast, default)).PlannedExpense;
        var budget = (await BudgetFactReader.ReadAsync(db, _ws.Id, NovFirst, NovFirst.AddMonths(1), default, Today)).Where(f => f.State == BudgetFactState.Pending).Sum(f => f.Amount);
        var projection = (await new FinancialCalendarBalanceRepository(db).ReadAsync(_ws.Id, null, NovLast, Today, default))!.Pending.Sum(p => p.Expense);

        Assert.Equal([200m, 200m, 200m, 200m], new[] { agenda, account, budget, projection });
    }

    [Fact]
    public async Task CalendarRead_EndingBeforeTheCurrentMonth_DoesNotFailOnVirtualRecurrences()
    {
        await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        await using var db = Db();

        // Painel do mês: compromissos em aberto "até o fim do mês anterior" com hoje já em novembro (início da janela virtual > fim).
        var snapshot = await new FinancialCalendarBalanceRepository(db).ReadAsync(_ws.Id, null, NovFirst.AddDays(-1), Today, default);

        Assert.NotNull(snapshot);
        Assert.Empty(await FixedObligationReader.LoadVirtualPendingAsync(db, _ws.Id, NovFirst, NovFirst.AddDays(-1), Today, default));
    }

    [Fact]
    public async Task Reconciler_LinksOnlyUniqueMatch_IsIdempotent_AndNeverMovesAConfirmedLink()
    {
        var recurrence = await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10), estimated: true);
        var first = await AddPluggyTransactionAsync("PIX ALUGUEL JOAO", 184.50m, new DateOnly(2026, 11, 9));
        await using (var db = Db()) Assert.Equal(1, await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default));
        await using (var db = Db()) Assert.Equal(0, await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default)); // reprocessamento idempotente
        var second = await AddPluggyTransactionAsync("PIX ALUGUEL JOAO", 190m, new DateOnly(2026, 11, 11));
        await using (var db = Db()) Assert.Equal(0, await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default)); // não troca o vínculo confirmado

        await using var verify = Db();
        var settlement = Assert.Single(await verify.FinancialObligationSettlements.ToListAsync());
        Assert.Equal((first.Id, recurrence.Id, NovFirst, SettlementOrigin.Auto), (settlement.FinancialTransactionId, settlement.RecurrenceId, settlement.Competence, settlement.Origin));
        Assert.NotEqual(second.Id, settlement.FinancialTransactionId);
    }

    [Fact]
    public async Task Reconciler_CompetenceSuffixDescription_Links_AndLeavesThePluggyTransactionUntouched()
    {
        await AddRecurrenceAsync("DMAE — novembro/2026", 150m, new DateOnly(2026, 10, 10), estimated: true);
        var payment = await AddPluggyTransactionAsync("PAGTO DMAE PORTO ALEGRE", 168.30m, new DateOnly(2026, 11, 9));
        async Task<string> Snapshot() { await using var d = Db(); var t = await d.FinancialTransactions.AsNoTracking().SingleAsync(x => x.Id == payment.Id); return $"{t.Amount}|{t.TransactionDate}|{t.Description}|{t.IsCancelled}|{t.UpdatedAt:O}|{t.PlannedTransactionId}"; }
        var before = await Snapshot();

        await using (var db = Db()) Assert.Equal(1, await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default));

        Assert.Equal(before, await Snapshot());
        await using var verify = Db();
        Assert.Equal(payment.Id, Assert.Single(await verify.FinancialObligationSettlements.ToListAsync()).FinancialTransactionId);
    }

    [Fact]
    public async Task Reconciler_AmbiguousOrPartialOrOtherOrigin_StaysForReview()
    {
        await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        await AddPluggyTransactionAsync("PIX ALUGUEL A", 200m, new DateOnly(2026, 11, 9));
        await AddPluggyTransactionAsync("PIX ALUGUEL B", 200m, new DateOnly(2026, 11, 10)); // ambíguo
        await using var db = Db();

        Assert.Equal(0, await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default));
        Assert.Empty(await db.FinancialObligationSettlements.ToListAsync());
        Assert.Equal(200m, FixedObligationIndicators.FixasPrevistasPendentes(await FixedObligationReader.LoadAsync(db, _ws.Id, NovFirst, NovLast, Today, default)));
    }

    [Fact]
    public async Task Reconciler_PartialPaymentOfFixedAmount_IsNotSettled()
    {
        await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        await AddPluggyTransactionAsync("PIX ALUGUEL JOAO", 100m, new DateOnly(2026, 11, 9));
        await using var db = Db();

        Assert.Equal(0, await FixedObligationReconciler.ReconcileAsync(db, _ws.Id, Today, Now, default));
    }

    [Fact]
    public async Task Database_RejectsTwoPaymentsForOneOccurrence_AndOnePaymentForTwoObligations()
    {
        var a = await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        var b = await AddRecurrenceAsync("Internet", 100m, new DateOnly(2026, 10, 15));
        var t1 = await AddPluggyTransactionAsync("ALUGUEL", 200m, new DateOnly(2026, 11, 9));
        var t2 = await AddPluggyTransactionAsync("ALUGUEL 2", 200m, new DateOnly(2026, 11, 9));
        await LinkAsync(a.Id, NovFirst, null, t1.Id);

        var twoPayments = await Assert.ThrowsAsync<DbUpdateException>(() => LinkAsync(a.Id, NovFirst, null, t2.Id));
        var twoObligations = await Assert.ThrowsAsync<DbUpdateException>(() => LinkAsync(b.Id, NovFirst, null, t1.Id));

        Assert.Equal("ux_obligation_settlement_recurrence_competence", ((PostgresException)twoPayments.InnerException!).ConstraintName);
        Assert.Equal("ux_obligation_settlement_transaction", ((PostgresException)twoObligations.InnerException!).ConstraintName);
    }

    [Fact]
    public async Task Workspaces_AreIsolated()
    {
        await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10));
        await using var db = Db();

        var other = await FixedObligationReader.LoadAsync(db, _other.Id, NovFirst, NovLast, Today, default);
        var mine = await FixedObligationReader.LoadAsync(db, _ws.Id, NovFirst, NovLast, Today, default);

        Assert.Empty(other);
        Assert.Single(mine);
        await using var cross = Db();
        // O vínculo entre workspaces diferentes é impossível: a FK composta exige a mesma família.
        var tx = await AddPluggyTransactionAsync("ALUGUEL", 200m, new DateOnly(2026, 11, 9));
        var foreignRecurrence = await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10), workspace: _other, account: _otherAccount, category: _otherCategory);
        await Assert.ThrowsAsync<DbUpdateException>(() => LinkAsync(foreignRecurrence.Id, NovFirst, null, tx.Id));
    }

    [Fact]
    public async Task LinkingDoesNotChangeTheBankTransactionOrTheRealizedBalance()
    {
        var recurrence = await AddRecurrenceAsync("Aluguel", 200m, new DateOnly(2026, 10, 10), estimated: true);
        var tx = await AddPluggyTransactionAsync("PIX ALUGUEL JOAO", 184.50m, new DateOnly(2026, 11, 9));
        await using var before = Db();
        var realizedBefore = (await new FinancialCalendarRepository(before).ListAsync(_ws.Id, Filter(), default)).Summary.RealizedExpense;
        await LinkAsync(recurrence.Id, NovFirst, null, tx.Id);

        await using var after = Db();
        var persisted = await after.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == tx.Id);
        var realizedAfter = (await new FinancialCalendarRepository(after).ListAsync(_ws.Id, Filter(), default)).Summary.RealizedExpense;

        Assert.Equal((184.50m, new DateOnly(2026, 11, 9), FinancialTransactionOrigin.PluggyImport, false), (persisted.Amount, persisted.TransactionDate, persisted.Origin, persisted.IsCancelled));
        Assert.Equal(realizedBefore, realizedAfter);
    }

    // ----- apoio -----
    private static FinancialCalendarFilter Filter() => new(NovFirst, NovLast, null, null, Today);
    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_cs);

    private FinancialAccount Account(FinancialWorkspace ws) => FinancialAccount.Create(ws.Id,
        AccountDetails.TryCreate("Itaú", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, 1000m, new DateOnly(2026, 9, 1), out _)!, ws.OwnerUserId, Now);

    private PlannedTransactionDetails Details(decimal amount, DateOnly due, string description) =>
        PlannedTransactionDetails.TryCreate(_account.Id, _category.Id, "Expense", description, amount, due, out _)!;

    private async Task<FinancialRecurrence> AddRecurrenceAsync(string description, decimal amount, DateOnly start, bool estimated = false, DateOnly? until = null,
        FinancialWorkspace? workspace = null, FinancialAccount? account = null, FinancialCategory? category = null)
    {
        var recurrence = FinancialRecurrence.Create((workspace ?? _ws).Id, new RecurrenceDetails((account ?? _account).Id, (category ?? _category).Id, CategoryType.Expense, description, amount,
            estimated, RecurrenceFrequency.Monthly, 1, start, until, null, start.Day, null, ExpenseNature.Fixed), Now);
        await using var db = Db(); db.FinancialRecurrences.Add(recurrence); await db.SaveChangesAsync();
        return recurrence;
    }

    private async Task<FinancialTransaction> AddPluggyTransactionAsync(string description, decimal amount, DateOnly date)
    {
        var tx = FinancialTransaction.CreateFromPluggy(_ws.Id, _account.Id, _category.Id, CategoryType.Expense, description, amount, date, _user, Now);
        await using var db = Db(); db.FinancialTransactions.Add(tx); await db.SaveChangesAsync();
        return tx;
    }

    private async Task LinkAsync(Guid recurrenceId, DateOnly competence, Guid? plannedId, Guid transactionId)
    {
        await using var db = Db();
        db.FinancialObligationSettlements.Add(FinancialObligationSettlement.Create(
            db.FinancialRecurrences.AsNoTracking().Where(r => r.Id == recurrenceId).Select(r => r.WorkspaceId).Single(), recurrenceId, competence, plannedId, transactionId, SettlementOrigin.Manual, Now));
        await db.SaveChangesAsync();
    }
}

/// <summary>Migration aditiva AddFixedObligationConsolidation: não perde dados existentes, cria só o que é novo e é reversível.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Recurrences")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FixedObligationMigrationTests(PostgresFixture postgres)
{
    private const string Previous = "20261101180947_AddPluggyDetectedCardRecurrences";
    private const string Current = "20261101200304_AddFixedObligationConsolidation";

    [Fact]
    public async Task Migration_IsAdditive_PreservesExistingPlannedRows_AndRoundTrips()
    {
        var cs = await postgres.CreateDatabaseAsync();
        Guid plannedId;
        await using (var db = PostgresFixture.CreateDbContext(cs))
        {
            var user = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "m", "M", default);
            var ws = FinancialWorkspace.CreatePersonal(user, DateTimeOffset.UtcNow);
            await new FinancialWorkspaceRepository(db).AddAsync(ws, default);
            var account = FinancialAccount.Create(ws.Id, AccountDetails.TryCreate("Itaú", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, 10m, new DateOnly(2026, 9, 1), out _)!, user, DateTimeOffset.UtcNow);
            var category = FinancialCategory.Create(ws.Id, "Moradia", CategoryType.Expense, user, DateTimeOffset.UtcNow);
            db.AddRange(account, category); await db.SaveChangesAsync();
            var planned = FinancialPlannedTransaction.Create(ws.Id, PlannedTransactionDetails.TryCreate(account.Id, category.Id, "Expense", "Aluguel", 200m, new DateOnly(2026, 11, 10), out _)!, user, DateTimeOffset.UtcNow);
            db.FinancialPlannedTransactions.Add(planned); await db.SaveChangesAsync(); plannedId = planned.Id;

            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync(Previous); // desfaz só esta migration
            Assert.False(await ColumnExists(db, "financial_planned_transactions", "exception_recurrence_id"));
            Assert.False(await TableExists(db, "financial_obligation_settlements"));
            Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM financial_planned_transactions WHERE id = {plannedId}").SingleAsync());
            await migrator.MigrateAsync(Current); // reaplica
        }
        await using var verify = PostgresFixture.CreateDbContext(cs);
        var kept = await verify.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == plannedId);
        Assert.Equal((200m, null, null, false), (kept.Amount, kept.ExceptionRecurrenceId, kept.ExceptionCompetence, kept.NeedsHistoricalReview));
        Assert.True(await TableExists(verify, "financial_obligation_settlements"));
        Assert.True(await ColumnExists(verify, "financial_planned_transactions", "needs_historical_review"));
    }

    private static async Task<bool> TableExists(Saldo360DbContext db, string table) =>
        await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = {table}").SingleAsync() == 1;

    private static async Task<bool> ColumnExists(Saldo360DbContext db, string table, string column) =>
        await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = {table} AND column_name = {column}").SingleAsync() == 1;
}
