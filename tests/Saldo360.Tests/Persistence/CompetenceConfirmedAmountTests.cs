using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Saldo360.Application.Budgets;
using Saldo360.Application.Calendar;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
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
/// PROJ-04: valor real confirmado por competência de despesa fixa, no PostgreSQL real e com "hoje" fixo (01/11/2026).
/// Real confirmado &gt; ajuste da competência &gt; valor-base, valendo só naquele mês, sem somar e sem confundir com pagamento.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Recurrences")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CompetenceConfirmedAmountTests(PostgresFixture postgres) : IAsyncLifetime
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

    public async Task InitializeAsync()
    {
        _cs = await postgres.CreateDatabaseAsync();
        await using var db = Db();
        _user = await new UserDirectory(db, TimeProvider.System).ResolveAsync("https://auth.test.invalid", "a", "A", CancellationToken.None);
        _ws = FinancialWorkspace.CreatePersonal(_user, Now);
        await new FinancialWorkspaceRepository(db).AddAsync(_ws, default);
        _account = FinancialAccount.Create(_ws.Id,
            AccountDetails.TryCreate("Itaú", null, nameof(AccountType.Checking), nameof(FinancialScope.PF), null, 1000m, new DateOnly(2026, 9, 1), out _)!, _user, Now);
        _category = FinancialCategory.Create(_ws.Id, "Moradia", CategoryType.Expense, _user, Now);
        db.AddRange(_account, _category);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Confirmation_ReplacesOnlyThatCompetence_AndKeepsTheBaseAndFutureMonths()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);

        var outcome = await ConfirmAsync(recurrence.Id, Nov, 748.31m);

        Assert.Equal(CompetenceAmountStatus.Ok, outcome.Status);
        Assert.Equal((750m, 750m, 748.31m, true), (outcome.Result!.BaseAmount, outcome.Result.AdjustedAmount, outcome.Result.ConfirmedAmount, outcome.Result.Created));
        await using var db = Db();
        var november = await Obligation(db, recurrence.Id, Nov);
        var december = await Obligation(db, recurrence.Id, Dec);
        var january = await Obligation(db, recurrence.Id, new DateOnly(2027, 1, 1));
        Assert.Equal((748.31m, ObligationAmountSource.Confirmed, 750m), (november.PlannedAmount, november.AmountSource, november.BaseAmount));
        Assert.Equal((750m, ObligationAmountSource.Base, true), (december.PlannedAmount, december.AmountSource, december.IsVirtual));
        Assert.Equal((750m, ObligationAmountSource.Base), (january.PlannedAmount, january.AmountSource));
        Assert.Equal(750m, (await db.FinancialRecurrences.AsNoTracking().SingleAsync(r => r.Id == recurrence.Id)).Amount);
    }

    [Fact]
    public async Task Confirmation_IsNotPayment_AndPaymentThenReplacesNothingTwice()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        var confirmation = (await ConfirmAsync(recurrence.Id, Nov, 748.31m)).Result!;
        await using (var db = Db())
        {
            var pending = await Obligation(db, recurrence.Id, Nov);
            Assert.Equal((ObligationSituation.Planned, true), (pending.Situation, pending.IsPendingCashFlow));
        }

        var payment = FinancialTransaction.CreateFromPluggy(_ws.Id, _account.Id, _category.Id, CategoryType.Expense, "SAIDA CREDIARIO", 748.31m, new DateOnly(2026, 11, 6), _user, Now);
        await using (var seed = Db())
        {
            seed.FinancialTransactions.Add(payment);
            seed.FinancialObligationSettlements.Add(FinancialObligationSettlement.Create(_ws.Id, recurrence.Id, Nov, confirmation.PlannedTransactionId, payment.Id, SettlementOrigin.Manual, Now));
            await seed.SaveChangesAsync();
        }

        await using var db2 = Db();
        var paid = await Obligation(db2, recurrence.Id, Nov);
        var snapshot = await new FinancialCalendarRepository(db2).ListAsync(_ws.Id, new(Nov, new DateOnly(2026, 11, 30), null, null, Today), default);
        Assert.Equal((ObligationSituation.Realized, 748.31m), (paid.Situation, paid.ActualAmount));
        Assert.Equal((748.31m, 0m), (snapshot.Summary.RealizedExpense, snapshot.Summary.PendingExpense));
    }

    [Fact]
    public async Task Correction_UpdatesTheSameCompetenceOnly_WithoutCreatingASecondRow()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await ConfirmAsync(recurrence.Id, Nov, 748.31m);

        var corrected = await ConfirmAsync(recurrence.Id, Nov, 749.10m);

        Assert.Equal((CompetenceAmountStatus.Ok, false, 749.10m), (corrected.Status, corrected.Result!.Created, corrected.Result.ConfirmedAmount));
        await using var db = Db();
        Assert.Equal(1, await db.FinancialPlannedTransactions.CountAsync(p => p.ExceptionRecurrenceId == recurrence.Id));
        Assert.Equal(749.10m, (await Obligation(db, recurrence.Id, Nov)).PlannedAmount);
        Assert.Equal(750m, (await Obligation(db, recurrence.Id, Dec)).PlannedAmount);
    }

    [Fact]
    public async Task Priority_ConfirmedBeatsAdjustment_AndAnExistingExceptionStaysAnAdjustmentUntilConfirmed()
    {
        var recurrence = await AddRecurrenceAsync("Plano", 100m);
        var exception = FinancialPlannedTransaction.Create(_ws.Id, Details(150m, new DateOnly(2026, 11, 12), "Plano"), _user, Now);
        exception.MarkAsCompetenceException(recurrence.Id, Nov);
        await using (var seed = Db()) { seed.FinancialPlannedTransactions.Add(exception); await seed.SaveChangesAsync(); }
        await using (var db = Db())
        {
            var adjusted = await Obligation(db, recurrence.Id, Nov);
            Assert.Equal((150m, ObligationAmountSource.Adjustment, 100m), (adjusted.PlannedAmount, adjusted.AmountSource, adjusted.BaseAmount));
        }

        var outcome = await ConfirmAsync(recurrence.Id, Nov, 120m);

        Assert.Equal((CompetenceAmountStatus.Ok, false, 150m, 120m), (outcome.Status, outcome.Result!.Created, outcome.Result.AdjustedAmount, outcome.Result.ConfirmedAmount));
        await using var db2 = Db();
        var confirmed = await Obligation(db2, recurrence.Id, Nov);
        Assert.Equal((120m, ObligationAmountSource.Confirmed, 150m), (confirmed.PlannedAmount, confirmed.AmountSource, confirmed.AdjustedAmount));
    }

    [Fact]
    public async Task NoDuplicate_AgendaAccountAndBudgetCountTheConfirmedValueOnce()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await ConfirmAsync(recurrence.Id, Nov, 748.31m);
        await using var db = Db();

        var snapshot = await new FinancialCalendarRepository(db).ListAsync(_ws.Id, new(Nov, new DateOnly(2026, 11, 30), null, null, Today), default);
        var account = await new FinancialAccountRepository(db).SumPendingPlannedAsync(_ws.Id, _account.Id, Today, new DateOnly(2026, 11, 30), default);
        var budget = await BudgetFactReader.ReadAsync(db, _ws.Id, Nov, Dec, default, Today);
        var item = Assert.Single(snapshot.Items);

        Assert.Equal(748.31m, snapshot.Summary.PendingExpense);
        Assert.Equal(748.31m, account.PlannedExpense);
        Assert.Equal(748.31m, budget.Where(f => f.State == BudgetFactState.Pending).Sum(f => f.Amount));
        Assert.Equal((748.31m, 750m, "Confirmed", recurrence.Id), (item.Amount, item.BaseAmount, item.AmountSource, item.RecurrenceId));
    }

    [Fact]
    public async Task Confirmation_IsRejectedForClosedCompetences_AndPaidOnes()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);

        var closed = await ConfirmAsync(recurrence.Id, new DateOnly(2026, 10, 1), 700m);
        Assert.Equal(CompetenceAmountStatus.InvalidCompetence, closed.Status);

        var payment = FinancialTransaction.CreateFromPluggy(_ws.Id, _account.Id, _category.Id, CategoryType.Expense, "SAIDA CREDIARIO", 750m, new DateOnly(2026, 11, 6), _user, Now);
        await using (var seed = Db())
        {
            seed.FinancialTransactions.Add(payment);
            seed.FinancialObligationSettlements.Add(FinancialObligationSettlement.Create(_ws.Id, recurrence.Id, Nov, null, payment.Id, SettlementOrigin.Manual, Now));
            await seed.SaveChangesAsync();
        }
        var paid = await ConfirmAsync(recurrence.Id, Nov, 748.31m);
        Assert.Equal(CompetenceAmountStatus.Conflict, paid.Status);
        await using var db = Db();
        Assert.False(await db.FinancialPlannedTransactions.AnyAsync(p => p.ExceptionRecurrenceId == recurrence.Id));
    }

    [Fact]
    public async Task ProjectedBalance_UsesTheConfirmedValueOnlyInThatMonth_WithoutCountingTheRecurrenceAgain()
    {
        var recurrence = await AddRecurrenceAsync("Crediário Solar", 750m);
        await ConfirmAsync(recurrence.Id, Nov, 748.31m);
        await using var db = Db();
        var repository = new FinancialCalendarBalanceRepository(db);

        var november = (await repository.ReadAsync(_ws.Id, null, new DateOnly(2026, 11, 30), Today, default))!.Pending.Sum(p => p.Expense);
        var throughDecember = (await repository.ReadAsync(_ws.Id, null, new DateOnly(2026, 12, 31), Today, default))!.Pending.Sum(p => p.Expense);
        var throughJanuary = (await repository.ReadAsync(_ws.Id, null, new DateOnly(2027, 1, 31), Today, default))!.Pending.Sum(p => p.Expense);

        Assert.Equal(748.31m, november);
        Assert.Equal(748.31m + 750m, throughDecember);
        Assert.Equal(748.31m + 750m + 750m, throughJanuary);
    }

    private const string Previous = "20261102171709_AddIncomeForecast";
    private const string Current = "20261104111511_AddCompetenceConfirmedAmount";

    [Fact]
    public async Task Migration_IsAdditive_AndNeverMarksExistingExceptionsAsConfirmed()
    {
        var recurrence = await AddRecurrenceAsync("Plano", 100m);
        var exception = FinancialPlannedTransaction.Create(_ws.Id, Details(150m, new DateOnly(2026, 11, 12), "Plano"), _user, Now);
        exception.MarkAsCompetenceException(recurrence.Id, Nov);
        await using (var db = Db())
        {
            db.FinancialPlannedTransactions.Add(exception); await db.SaveChangesAsync();
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync(Previous); // desfaz só esta migration
            Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'financial_planned_transactions' AND column_name = 'confirmed_amount'").SingleAsync());
            await migrator.MigrateAsync(Current); // reaplica
        }

        await using var verify = Db();
        var kept = await verify.FinancialPlannedTransactions.AsNoTracking().SingleAsync(p => p.Id == exception.Id);
        Assert.Equal((150m, recurrence.Id, Nov, null, null, null), (kept.Amount, kept.ExceptionRecurrenceId, kept.ExceptionCompetence, kept.ConfirmedAmount, kept.AmountConfirmedAt, kept.AmountConfirmedByUserId));
        Assert.Equal(ObligationAmountSource.Adjustment, (await Obligation(verify, recurrence.Id, Nov)).AmountSource);
    }

    private async Task<CompetenceAmountOutcome> ConfirmAsync(Guid recurrenceId, DateOnly competence, decimal amount)
    {
        await using var db = Db();
        var recurrence = await db.FinancialRecurrences.SingleAsync(r => r.Id == recurrenceId);
        return await new FinancialRecurrenceMaterializationRepository(db).ConfirmCompetenceAmountAsync(recurrence, competence, amount, _user, Now, default);
    }

    private async Task<FixedObligation> Obligation(Saldo360DbContext db, Guid recurrenceId, DateOnly competence) =>
        (await FixedObligationReader.LoadAsync(db, _ws.Id, new DateOnly(2026, 9, 1), new DateOnly(2027, 2, 28), Today, default)).Single(o => o.RecurrenceId == recurrenceId && o.Competence == competence);

    private Saldo360DbContext Db() => PostgresFixture.CreateDbContext(_cs);

    private PlannedTransactionDetails Details(decimal amount, DateOnly due, string description) =>
        PlannedTransactionDetails.TryCreate(_account.Id, _category.Id, "Expense", description, amount, due, out _)!;

    private async Task<FinancialRecurrence> AddRecurrenceAsync(string description, decimal amount)
    {
        var start = new DateOnly(2026, 10, 6);
        var recurrence = FinancialRecurrence.Create(_ws.Id, new RecurrenceDetails(_account.Id, _category.Id, CategoryType.Expense, description, amount,
            false, RecurrenceFrequency.Monthly, 1, start, null, null, start.Day, null, ExpenseNature.Fixed), Now);
        await using var db = Db(); db.FinancialRecurrences.Add(recurrence); await db.SaveChangesAsync();
        return recurrence;
    }
}
