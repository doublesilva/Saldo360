using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Recuperação do cheque especial dentro do fluxo de caixa da Visão Geral (meta de 80% fixada no dia 10). O relógio do serviço é injetado (dias 9, 10 e 20 do mês corrente), então
/// nenhum teste depende do dia real; os testes HTTP só usam movimentos datados em "hoje" e afirmam o estado esperado para o dia real.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Accounts")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class OverdraftRecoveryOverviewTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month(DateOnly d) => d.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _income, _expense;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    /// <summary>12:00 em São Paulo (15:00 UTC) do dia informado do mês corrente.</summary>
    private static FixedClock Day(int day) => new(new DateTimeOffset(Today.Year, Today.Month, day, 15, 0, 0, TimeSpan.Zero));

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        _income = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Receitas", type = "Income" }))).Id;
        _expense = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
    }

    private async Task<Guid> AccountAsync(string name, string opening, DateOnly? openingDate = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name, type = "Checking", financialScope = "PF", openingBalance = opening, openingBalanceDate = openingDate ?? ThisMonth.AddMonths(-1), overdraftLimit = "20000.00" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    private async Task PlanAsync(Guid account, string type, string amount, DateOnly due) =>
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/planned-transactions", _session,
            new { accountId = account, categoryId = type == "Income" ? _income : _expense, type, description = "Previsto", amount, dueDate = due })).StatusCode);

    private async Task TransactionAsync(Guid account, string type, string amount, DateOnly date) =>
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions", _session,
            new { accountId = account, categoryId = type == "Income" ? _income : _expense, type, description = "Efetivo", amount, transactionDate = date })).StatusCode);

    private async Task<JsonElement> FlowAsync(Guid? account, DateOnly month, string scope = "account")
    {
        var url = $"{Root}/financial-dashboard/overview?competence={Month(month)}&scope={scope}" + (account is null ? "" : $"&accountId={account}");
        var response = await SendAsync(HttpMethod.Get, url, _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("cashFlow").Clone();
    }

    private async Task<OverviewRecoveryDto?> ServiceAsync(Guid account, DateOnly month, TimeProvider time)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var service = ActivatorUtilities.CreateInstance<DashboardOverviewService>(scope.ServiceProvider, time);
        var result = await service.GetAsync(_workspace.Id, _workspace.OwnerUserId, Month(month), PlanActualScopes.Account, account, CancellationToken.None);
        return result.Value!.CashFlow!.Recovery;
    }

    private async Task<List<OverdraftRecoverySnapshot>> SnapshotsAsync(Guid account)
    {
        await using var db = Db();
        return await db.OverdraftRecoverySnapshots.AsNoTracking().Where(s => s.AccountId == account).ToListAsync();
    }

    private static string Text(JsonElement e, params string[] path) { foreach (var p in path) e = e.GetProperty(p); return e.GetString()!; }

    [Fact]
    public async Task OpeningCardShowsTheOpeningBalanceAndTheEightyPercentTargetAndTheBridgeStillCloses()
    {
        await ArrangeAsync("rov-abertura");
        var account = await AccountAsync("Itaú PF", "-13000.00");
        await PlanAsync(account, "Income", "3000.00", Today); // projetado: -13.000 - 100 + 3.000 = -10.100 => capacidade 2.900 => meta 2.320 (80%)
        await TransactionAsync(account, "Expense", "100.00", Today);

        var flow = await FlowAsync(account, ThisMonth);
        var recovery = flow.GetProperty("recovery");

        Assert.Equal("-13000.00", Text(flow, "opening"));
        Assert.Equal("13000.00", Text(recovery, "openingUsed"));
        Assert.Equal("2320.00", Text(recovery, "target"));
        Assert.Equal(("0.80", Today.Day >= 10 ? "Fixed" : "Provisional"), (Text(recovery, "targetFactor"), Text(recovery, "targetState")));
        // a ponte de caixa não foi afetada
        Assert.Equal(decimal.Parse(Text(flow, "opening")) + decimal.Parse(Text(flow, "inflows", "total")) - decimal.Parse(Text(flow, "outflows", "total")), decimal.Parse(Text(flow, "closing")));
    }

    [Fact]
    public async Task AccountThatDoesNotStartTheMonthNegativeHasNoRecoveryComponent()
    {
        await ArrangeAsync("rov-positivo");
        var positive = await AccountAsync("Itaú PF", "500.00");
        var zero = await AccountAsync("Reserva", "0.00");
        Assert.Equal(JsonValueKind.Null, (await FlowAsync(positive, ThisMonth)).GetProperty("recovery").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await FlowAsync(zero, ThisMonth)).GetProperty("recovery").ValueKind);
        Assert.Empty(await SnapshotsAsync(positive));
    }

    [Fact]
    public async Task TheTargetIsProvisionalOnDayNineFixedOnDayTenAndNewExpensesChangeTheProjectionButNotTheFixedTarget()
    {
        await ArrangeAsync("rov-dia10");
        var account = await AccountAsync("Itaú PF", "-13000.00");
        await PlanAsync(account, "Income", "3000.00", Today);

        var day9 = await ServiceAsync(account, ThisMonth, Day(9));
        Assert.Equal(("Provisional", "2400.00", "0.80", "3000.00"), (day9!.TargetState, day9.Target, day9.TargetFactor, day9.Capacity));
        Assert.Null(day9.TargetFixedAt);
        Assert.Empty(await SnapshotsAsync(account)); // nada gravado antes do dia 10

        var tenth = Day(10);
        var day10 = await ServiceAsync(account, ThisMonth, tenth);
        Assert.Equal(("Fixed", "2400.00", "0.80", false), (day10!.TargetState, day10.Target, day10.TargetFactor, day10.LegacyTarget));
        Assert.Equal(tenth.GetUtcNow(), day10.TargetFixedAt);
        Assert.Equal("3000.00", day10.ProjectedRecovery);
        var row = Assert.Single(await SnapshotsAsync(account));
        Assert.Equal((2400m, 0.80m, 3000m, tenth.GetUtcNow()), (row.RecoveryTarget, row.TargetFactor, row.TargetCapacity, row.FixedAt));

        await PlanAsync(account, "Expense", "1000.00", Today); // novas despesas previstas depois da fixação
        var day20 = await ServiceAsync(account, ThisMonth, Day(20));
        Assert.Equal(("Fixed", "2400.00", "2000.00"), (day20!.TargetState, day20.Target, day20.ProjectedRecovery)); // projeção caiu; meta preservada
        Assert.Equal("83.3", day20.ProjectedAchievementPercent);
        Assert.Equal("Met", day20.Band);
        Assert.Equal(row.Id, Assert.Single(await SnapshotsAsync(account)).Id); // idempotente: nada recriado nem recalculado
    }

    [Fact]
    public async Task ConcurrentReadsOnDayTenFixOnlyOneTarget()
    {
        await ArrangeAsync("rov-concorrente");
        var account = await AccountAsync("Itaú PF", "-13000.00");
        await PlanAsync(account, "Income", "3000.00", Today);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ServiceAsync(account, ThisMonth, Day(10))));

        Assert.All(results, r => Assert.Equal(("Fixed", "2400.00"), (r!.TargetState, r.Target)));
        Assert.Single(await SnapshotsAsync(account));
    }

    [Fact]
    public async Task HistoricalTargetsAreNotRewrittenAndKeepTheirOwnValueWithoutTheNewFactor()
    {
        await ArrangeAsync("rov-historica");
        var account = await AccountAsync("Itaú PF", "-13000.00");
        await PlanAsync(account, "Income", "3000.00", Today);
        // meta histórica (85% da capacidade 3.000, gravada antes da regra): 2.550, sem fator
        await using (var db = Db())
        {
            db.OverdraftRecoverySnapshots.Add(OverdraftRecoverySnapshot.Create(_workspace.Id, account, ThisMonth, -13000m, ThisMonth, 0m, 0m, -10450m, "Complete", string.Empty,
                new DateTimeOffset(Today.Year, Today.Month, 1, 15, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
        }

        var recovery = await ServiceAsync(account, ThisMonth, Day(20));

        Assert.Equal(("Fixed", "2550.00", true, null), (recovery!.TargetState, recovery.Target, recovery.LegacyTarget, recovery.TargetFactor));
        var row = Assert.Single(await SnapshotsAsync(account));
        Assert.Equal((2550m, null, null), (row.RecoveryTarget, row.TargetFactor, row.FixedAt));
    }

    [Fact]
    public async Task ClosedMonthShowsTheEffectiveClosingAtTheLastDayAgainstTheFixedTarget()
    {
        await ArrangeAsync("rov-encerrado");
        var last = ThisMonth.AddMonths(-1);
        var account = await AccountAsync("Itaú PF", "-13000.00", last.AddMonths(-1));
        await TransactionAsync(account, "Income", "4000.00", last.AddDays(14));
        await TransactionAsync(account, "Income", "1000.00", Today); // depois do corte da competência: não conta no fechamento histórico
        await using (var db = Db())
        {
            db.OverdraftRecoverySnapshots.Add(OverdraftRecoverySnapshot.CreateFixed(_workspace.Id, account, last, -13000m, last, 0m, 0m, -9000m, 4000m, 0.80m, 3200m, "Complete", string.Empty,
                new DateTimeOffset(last.Year, last.Month, 10, 15, 0, 0, TimeSpan.Zero)));
            await db.SaveChangesAsync();
        }

        var flow = await FlowAsync(account, last);
        var recovery = flow.GetProperty("recovery");

        Assert.Equal("-9000.00", Text(flow, "closing"));
        Assert.Equal(("-9000.00", "4000.00", "3200.00", "125.0", "Exceeded"), (Text(recovery, "currentBalance"), Text(recovery, "finalRecovery"), Text(recovery, "target"), Text(recovery, "finalAchievementPercent"), Text(recovery, "band")));
        Assert.Equal(last.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"), Text(recovery, "currentAsOf"));
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("projectedRecovery").ValueKind);
    }

    [Fact]
    public async Task TransferBetweenOwnAccountsMovesTheBalanceOfBothButIsNeutralInTheCombinedSetAndNeverDuplicated()
    {
        await ArrangeAsync("rov-transferencia");
        var negative = await AccountAsync("Itaú PF", "-13000.00");
        var reserve = await AccountAsync("Reserva", "5000.00");
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transfers", _session,
            new { sourceAccountId = reserve, destinationAccountId = negative, amount = "1000.00", transferDate = Today, description = "Cobrir limite" })).StatusCode);

        var destination = await FlowAsync(negative, ThisMonth);
        var origin = await FlowAsync(reserve, ThisMonth);
        var combined = await FlowAsync(null, ThisMonth, "all");

        Assert.Equal(("1000.00", "1000.00"), (Text(destination, "inflows", "transfersReceived"), Text(destination, "recovery", "recoveryCurrent")));
        Assert.Equal("1000.00", Text(origin, "outflows", "transfersSent"));
        Assert.Equal(JsonValueKind.Null, origin.GetProperty("recovery").ValueKind); // origem com saldo positivo
        Assert.Equal(("0.00", "0.00", "1000.00"), (Text(combined, "inflows", "transfersReceived"), Text(combined, "outflows", "transfersSent"), Text(combined, "internalMoved")));
        Assert.Equal(JsonValueKind.Null, combined.GetProperty("recovery").ValueKind); // um conjunto não compensa saldos de contas diferentes
        Assert.Equal("-8000.00", Text(combined, "closing")); // -13.000 + 5.000, a transferência não cria nem destrói dinheiro
    }

    [Fact]
    public async Task InProgressMonthShowsCurrentAndProjectedRecoveryAgainstTheCurrentTarget()
    {
        await ArrangeAsync("rov-parcial");
        var account = await AccountAsync("Itaú PF", "-13000.00");
        await TransactionAsync(account, "Income", "500.00", Today);
        await PlanAsync(account, "Income", "2500.00", Today);

        var recovery = (await FlowAsync(account, ThisMonth)).GetProperty("recovery");

        Assert.Equal(("-12500.00", "500.00", "-10000.00", "3000.00"), (Text(recovery, "currentBalance"), Text(recovery, "recoveryCurrent"), Text(recovery, "projectedClosing"), Text(recovery, "projectedRecovery")));
        Assert.Equal(("2400.00", "125.0", "Exceeded"), (Text(recovery, "target"), Text(recovery, "projectedAchievementPercent"), Text(recovery, "band")));
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("finalRecovery").ValueKind);
    }
}
