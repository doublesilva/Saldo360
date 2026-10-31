using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Dashboard;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Recuperação do limite de cheque especial (serviço + HTTP sobre PostgreSQL). O fixo é a meta registrada; o saldo atual e a projeção vêm do que o
/// Saldo360 já calcula. Fórmulas: utilização = max(0,-saldo); recuperado = utilização de abertura - utilização atual (limitado à de abertura);
/// meta = utilização de abertura - utilização do fechamento planejado.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Accounts")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class OverdraftRecoveryTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Competence(DateOnly month) => month.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";

    private sealed record IdDto(Guid Id);

    private async Task ArrangeAsync(string user)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
    }

    private async Task<Guid> AccountAsync(string name, string openingBalance, string? limit, string scope = "PF", DateOnly? openingDate = null)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session, new
        {
            name, type = "Checking", financialScope = scope, openingBalance, openingBalanceDate = openingDate ?? ThisMonth.AddMonths(-1), overdraftLimit = limit,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<IdDto>(response)).Id;
    }

    /// <summary>Registra a meta como o serviço faria, com a abertura e o fechamento planejado informados (a projeção real exige âncoras bancárias).</summary>
    private async Task SnapshotAsync(Guid account, decimal opening, decimal plannedClosing, DateOnly? month = null, DateTimeOffset? capturedAt = null)
    {
        var competence = month ?? ThisMonth;
        await using var db = Db();
        db.OverdraftRecoverySnapshots.Add(OverdraftRecoverySnapshot.Create(_workspace.Id, account, competence, opening, competence.AddDays(-1), 0m, 0m,
            plannedClosing, "Complete", string.Empty, capturedAt ?? new DateTimeOffset(competence.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)));
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> RecoveryAsync(Guid account, DateOnly? month = null, HttpStatusCode expected = HttpStatusCode.OK, string? session = null, Guid? workspace = null)
    {
        var url = $"/api/financial-workspaces/{workspace ?? _workspace.Id}/financial-dashboard/overdraft-recovery/{account}?competence={Competence(month ?? ThisMonth)}";
        var response = await SendAsync(HttpMethod.Get, url, session ?? _session);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return expected == HttpStatusCode.OK ? JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone() : default;
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    [Fact]
    public async Task AcceptanceExample_Opening15000_Limit20000_PlannedClosing12500_CurrentBalance13000_ProjectedClosing13200()
    {
        await ArrangeAsync("rec-aceite");
        var account = await AccountAsync("Itaú PF", "-13000.00", "20000.00");
        var category = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/planned-transactions", _session,
            new { accountId = account, categoryId = category, type = "Expense", description = "Conta", amount = "200.00", dueDate = Today })).StatusCode);
        await SnapshotAsync(account, -15000m, -12500m);

        var recovery = await RecoveryAsync(account);

        Assert.Equal(("15000.00", "13000.00", "2500.00", "2000.00"), (Text(recovery, "openingUsed"), Text(recovery, "currentUsed"), Text(recovery, "target"), Text(recovery, "recovered")));
        Assert.Equal(("1800.00", "-700.00"), (Text(recovery, "projectedRecovery"), Text(recovery, "deviation")));
        Assert.Equal(("20000.00", "7000.00", "PartialRecovery", "80.0"), (Text(recovery, "contractedLimit"), Text(recovery, "available"), Text(recovery, "state"), Text(recovery, "attainmentPercent")));
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("positiveBalance").ValueKind);

        // A meta é fixa: uma nova previsão muda a projeção, nunca a meta registrada.
        await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/planned-transactions", _session,
            new { accountId = account, categoryId = category, type = "Expense", description = "Outra", amount = "300.00", dueDate = Today });
        var after = await RecoveryAsync(account);
        Assert.Equal("2500.00", Text(after, "target"));
        Assert.Equal("1500.00", Text(after, "projectedRecovery"));
    }

    [Fact]
    public async Task RealizingThePendingForecast_ReplacesIt_WithoutCountingTwice()
    {
        await ArrangeAsync("rec-realizada");
        var account = await AccountAsync("Itaú PF", "-13000.00", "20000.00");
        var category = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
        var planned = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/planned-transactions", _session,
            new { accountId = account, categoryId = category, type = "Expense", description = "Conta", amount = "200.00", dueDate = Today }))).Id;
        await SnapshotAsync(account, -15000m, -12500m);
        Assert.Equal("1800.00", Text(await RecoveryAsync(account), "projectedRecovery"));

        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/planned-transactions/{planned}/realize", _session,
            new { amount = "200.00", transactionDate = Today })).StatusCode);

        var recovery = await RecoveryAsync(account);
        Assert.Equal(("13200.00", "1800.00", "1800.00"), (Text(recovery, "currentUsed"), Text(recovery, "recovered"), Text(recovery, "projectedRecovery")));
    }

    [Theory]
    [InlineData("-12500", "2500.00", "80.0")]   // meta positiva
    [InlineData("-15000", "0.00", null)]        // meta zero: sem percentual
    [InlineData("-15300", "-300.00", null)]     // meta negativa (piora prevista): sem percentual
    public async Task Target_Positive_Zero_AndNegative_AreStoredAsPlanned_WithAttainmentOnlyWhenPositive(string plannedClosing, string target, string? attainment)
    {
        await ArrangeAsync($"rec-meta-{plannedClosing}");
        var account = await AccountAsync("Itaú PF", "-13000.00", "20000.00");
        await SnapshotAsync(account, -15000m, decimal.Parse(plannedClosing, System.Globalization.CultureInfo.InvariantCulture));

        var recovery = await RecoveryAsync(account);

        Assert.Equal(target, Text(recovery, "target"));
        Assert.Equal(attainment, recovery.GetProperty("attainmentPercent").ValueKind == JsonValueKind.Null ? null : Text(recovery, "attainmentPercent"));
    }

    [Fact]
    public async Task PositiveBalance_EndsTheRecovery_AtTheInitialUsage_AndIsShownAsPositiveBalance()
    {
        await ArrangeAsync("rec-positivo");
        var account = await AccountAsync("Itaú PF", "500.00", "20000.00");
        await SnapshotAsync(account, -15000m, -12500m);

        var recovery = await RecoveryAsync(account);

        Assert.Equal(("FullyRecovered", "0.00", "15000.00", "500.00"), (Text(recovery, "state"), Text(recovery, "currentUsed"), Text(recovery, "recovered"), Text(recovery, "positiveBalance")));
        Assert.Equal("20000.00", Text(recovery, "available")); // o card segue visível após a recuperação total
    }

    [Fact]
    public async Task UsageAboveTheLimit_IsReported_WithoutNegativeAvailability()
    {
        await ArrangeAsync("rec-acima");
        var account = await AccountAsync("Itaú PF", "-25000.00", "20000.00");
        await SnapshotAsync(account, -15000m, -12500m);

        var recovery = await RecoveryAsync(account);

        Assert.Equal(("AboveLimit", "5000.00", "0.00"), (Text(recovery, "state"), Text(recovery, "aboveLimit"), Text(recovery, "available")));
    }

    [Fact]
    public async Task UnknownLimit_IsNotZero_AndNoSnapshotOrBalanceReturnsExplicitErrors()
    {
        await ArrangeAsync("rec-erros");
        var noLimit = await AccountAsync("Sem limite", "-100.00", null);
        await SnapshotAsync(noLimit, -100m, -50m);
        var recovery = await RecoveryAsync(noLimit);
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("contractedLimit").ValueKind);
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("available").ValueKind); // desconhecido não vira zero
        Assert.Equal(JsonValueKind.Null, recovery.GetProperty("aboveLimit").ValueKind);

        var unregistered = await AccountAsync("Sem meta", "-100.00", "1000.00");
        var missing = await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/overdraft-recovery/{unregistered}?competence={Competence(ThisMonth)}", _session);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("snapshot", await missing.Content.ReadAsStringAsync());

        // Saldo indisponível: competência anterior à data de abertura da conta (nunca um zero fictício).
        var future = await AccountAsync("Aberta agora", "-100.00", "1000.00", openingDate: ThisMonth);
        var earlier = ThisMonth.AddMonths(-3);
        await SnapshotAsync(future, -100m, -50m, earlier);
        var unavailable = await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/overdraft-recovery/{future}?competence={Competence(earlier)}", _session);
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
        Assert.Contains("balance", await unavailable.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LateReference_ClosedMonth_AndMonthEndBalance_AreIdentified()
    {
        await ArrangeAsync("rec-tardia");
        var account = await AccountAsync("Itaú PF", "-13000.00", "20000.00", openingDate: ThisMonth.AddMonths(-2));
        await SnapshotAsync(account, -15000m, -12500m, capturedAt: new DateTimeOffset(ThisMonth.AddDays(10).ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero));
        Assert.True((await RecoveryAsync(account)).GetProperty("lateReference").GetBoolean());

        var onTime = await AccountAsync("Banco B", "-13000.00", "20000.00", openingDate: ThisMonth.AddMonths(-2));
        await SnapshotAsync(onTime, -15000m, -12500m);
        Assert.False((await RecoveryAsync(onTime)).GetProperty("lateReference").GetBoolean());

        var previous = ThisMonth.AddMonths(-1);
        await SnapshotAsync(account, -15000m, -12500m, previous, new DateTimeOffset(previous.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
        var closed = await RecoveryAsync(account, previous);
        Assert.True(closed.GetProperty("isFinalResult").GetBoolean());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("projectedRecovery").ValueKind); // mês encerrado: resultado final, não projeção
        Assert.Equal(previous.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"), Text(closed, "balanceAsOf"));
    }

    [Fact]
    public async Task TransferFromPjToPf_ReducesTheUsageOfThePf_WithoutInflatingIncome()
    {
        await ArrangeAsync("rec-transferencia");
        var pf = await AccountAsync("Itaú PF", "-13000.00", "20000.00");
        var pj = await AccountAsync("Itaú PJ", "5000.00", null, "PJ");
        await SnapshotAsync(pf, -15000m, -12500m);

        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transfers", _session,
            new { sourceAccountId = pj, destinationAccountId = pf, amount = "2000.00", transferDate = Today, description = "Pró-labore" })).StatusCode);

        var recovery = await RecoveryAsync(pf);
        Assert.Equal(("11000.00", "4000.00"), (Text(recovery, "currentUsed"), Text(recovery, "recovered")));
        var dashboard = await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/plan-actual?scope=all", _session);
        using var json = JsonDocument.Parse(await dashboard.Content.ReadAsStringAsync());
        Assert.Equal("0.00", json.RootElement.GetProperty("income").GetProperty("realized").GetString()); // transferência não é receita
    }

    [Fact]
    public async Task AnotherWorkspace_SeesNotFound_AndConcurrentRegistrationsKeepASingleSnapshot()
    {
        await ArrangeAsync("rec-isolamento");
        var account = await AccountAsync("Itaú PF", "-13000.00", "20000.00");
        var stranger = await SignInAsync("rec-estranho");
        var other = await CreateWorkspaceAsync(stranger);
        await RecoveryAsync(account, expected: HttpStatusCode.NotFound, session: stranger, workspace: other.Id);
        await RecoveryAsync(account, expected: HttpStatusCode.NotFound, session: stranger, workspace: _workspace.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{other.Id}/financial-dashboard/overdraft-recovery/{account}", stranger,
            new { competence = Competence(ThisMonth) })).StatusCode);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var snapshot = OverdraftRecoverySnapshot.Create(_workspace.Id, account, ThisMonth, -15000m, ThisMonth.AddDays(-1), 0m, 0m, -12500m, "Complete", string.Empty, DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IOverdraftRecoveryRepository>().AddIfAbsentAsync(snapshot, CancellationToken.None);
        }));

        await using var db = Db();
        Assert.Equal(1, await db.OverdraftRecoverySnapshots.CountAsync(s => s.AccountId == account));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Created, null)]
    [InlineData("0.00", HttpStatusCode.Created, "0.00")]
    [InlineData("20000.00", HttpStatusCode.Created, "20000.00")]
    [InlineData("-1.00", HttpStatusCode.BadRequest, null)]
    [InlineData("1.005", HttpStatusCode.BadRequest, null)]
    public async Task AccountLimit_NullZeroPositiveAndInvalid(string? limit, HttpStatusCode expected, string? stored)
    {
        await ArrangeAsync($"rec-limite-{limit}");
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session, new
        {
            name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "0.00", openingBalanceDate = ThisMonth.AddMonths(-1), overdraftLimit = limit,
        });
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) return;
        await using var db = Db();
        Assert.Equal(stored is null ? (decimal?)null : decimal.Parse(stored, System.Globalization.CultureInfo.InvariantCulture), (await db.FinancialAccounts.AsNoTracking().SingleAsync()).OverdraftLimit);
    }

    [Fact]
    public async Task AccountBalance_ExposesLimitUsedAvailableAndExcess_OnlyWhenTheLimitIsKnown()
    {
        await ArrangeAsync("rec-conta");
        var known = await AccountAsync("Com limite", "-25000.00", "20000.00");
        var unknown = await AccountAsync("Sem limite", "-100.00", null);

        var withLimit = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/accounts/{known}/balance?asOf={Today:yyyy-MM-dd}", _session)).Content.ReadAsStringAsync()).RootElement;
        var withoutLimit = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/accounts/{unknown}/balance?asOf={Today:yyyy-MM-dd}", _session)).Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(("20000.00", "25000.00", "0.00", "5000.00"), (Text(withLimit, "overdraftLimit"), Text(withLimit, "overdraftUsed"), Text(withLimit, "overdraftAvailable"), Text(withLimit, "overdraftExcess")));
        Assert.Equal(JsonValueKind.Null, withoutLimit.GetProperty("overdraftLimit").ValueKind);
        Assert.Equal(JsonValueKind.Null, withoutLimit.GetProperty("overdraftAvailable").ValueKind);
    }
}
