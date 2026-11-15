using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Accounts;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// A listagem completa de contas pode ser compartilhada dentro de uma leitura pública do dashboard, mas os caminhos
/// original e compartilhado devem produzir exatamente os mesmos DTOs, acessos, saldos, transferências e coberturas.
/// PostgreSQL real; os dois caminhos leem o mesmo banco.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class AccountReadsSharingEquivalenceTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month => ThisMonth.ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private WorkspaceDto _otherWorkspace = null!;
    private readonly List<Guid> _accounts = [];
    private Guid _inactive;
    private Guid _expense;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);
    private sealed record Run(string Summary, string All, string Primary, string Inactive, string Missing, string OtherWorkspace, (int Database, int Reused) Reads);

    private async Task ArrangeAsync()
    {
        _session = await SignInAsync("account-sharing-owner");
        _workspace = await CreateWorkspaceAsync(_session);
        var outsider = await SignInAsync("account-sharing-outsider");
        _otherWorkspace = await CreateWorkspaceAsync(outsider);
        _expense = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Despesas", type = "Expense" }))).Id;
        for (var i = 0; i < 3; i++)
        {
            var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session, new
            {
                name = $"PF {i + 1}", type = "Checking", financialScope = "PF", openingBalance = "5000.00",
                openingBalanceDate = ThisMonth.AddMonths(-1), overdraftLimit = "1000.00", isPrimary = i == 0,
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            _accounts.Add((await ReadAsync<IdDto>(response)).Id);
        }
        _inactive = _accounts[2];
        await ExpenseAsync(_inactive, "321.00", "Histórico da inativa");
        await ExpenseAsync(_accounts[0], "100.00", "Efetivo");
        var transfer = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transfers", _session, new
        {
            sourceAccountId = _accounts[0], destinationAccountId = _accounts[1], amount = "50.00", transferDate = Today, description = "Transferência interna",
        });
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        var deactivated = await SendWithCsrfAsync(HttpMethod.Put, $"{Root}/accounts/{_inactive}", _session, new
        {
            name = "PF 3", type = "Checking", financialScope = "PF", openingBalance = "5000.00",
            openingBalanceDate = ThisMonth.AddMonths(-1), isActive = false,
        });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        await using var db = Db();
        db.FinancialMonthlyAggregateTargets.Add(FinancialMonthlyAggregateTarget.Create(_workspace.Id, ThisMonth, MonthlyAggregateTargetType.TotalExpenses, 20000m, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task ExpenseAsync(Guid account, string amount, string description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/transactions", _session,
            new { accountId = account, categoryId = _expense, type = "Expense", description, amount, transactionDate = Today });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<Run> ReadAsync(bool shared)
    {
        using var scope = Factory.Services.CreateScope();
        var sharing = scope.ServiceProvider.GetRequiredService<IAccountListSharing>();
        sharing.Enabled = shared;
        scope.ServiceProvider.GetRequiredService<ICardFactsSharing>().Enabled = true;
        var service = scope.ServiceProvider.GetRequiredService<DashboardOverviewService>();
        var missing = Guid.CreateVersion7();
        var summary = await service.GetBudgetSummaryAsync(_workspace.Id, _workspace.OwnerUserId, Month, CancellationToken.None);
        var all = await service.GetAsync(_workspace.Id, _workspace.OwnerUserId, Month, "all", null, CancellationToken.None);
        var primary = await service.GetAsync(_workspace.Id, _workspace.OwnerUserId, Month, "primary", null, CancellationToken.None);
        var inactive = await service.GetAsync(_workspace.Id, _workspace.OwnerUserId, Month, "account", _inactive, CancellationToken.None);
        var absent = await service.GetAsync(_workspace.Id, _workspace.OwnerUserId, Month, "account", missing, CancellationToken.None);
        var foreign = await service.GetAsync(_otherWorkspace.Id, _workspace.OwnerUserId, Month, "all", null, CancellationToken.None);
        return new(Serialize(summary), Serialize(all), Serialize(primary), Serialize(inactive), Serialize(absent), Serialize(foreign), sharing.Reads);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task SharedAndOriginalPathsKeepAllDashboardResultsAndAccessDecisionsEqual()
    {
        await ArrangeAsync();

        var original = await ReadAsync(false);
        var shared = await ReadAsync(true);

        Assert.Equal((0, 0), original.Reads);
        Assert.True(shared.Reads.Database >= 4, $"leituras no banco: {shared.Reads.Database}");
        Assert.True(shared.Reads.Reused > shared.Reads.Database, $"banco={shared.Reads.Database}; reutilizadas={shared.Reads.Reused}");
        Assert.Equal(original.Summary, shared.Summary);
        Assert.Equal(original.All, shared.All); // inclui saldos e transferências internas do consolidado
        Assert.Equal(original.Primary, shared.Primary);
        Assert.Equal(original.Inactive, shared.Inactive); // seleção explícita e histórico da conta inativa
        Assert.Equal(original.Missing, shared.Missing);
        Assert.Equal(original.OtherWorkspace, shared.OtherWorkspace);
    }

    [Fact]
    public async Task ANewPublicReadLoadsAccountsAgainAndWorkspacesDoNotShareEntries()
    {
        await ArrangeAsync();
        using var scope = Factory.Services.CreateScope();
        var sharing = scope.ServiceProvider.GetRequiredService<IAccountListSharing>();
        var service = scope.ServiceProvider.GetRequiredService<DashboardOverviewService>();

        await service.GetBudgetSummaryAsync(_workspace.Id, _workspace.OwnerUserId, Month, CancellationToken.None);
        var first = sharing.Reads;
        await service.GetBudgetSummaryAsync(_workspace.Id, _workspace.OwnerUserId, Month, CancellationToken.None);
        var second = sharing.Reads;

        Assert.Equal(first.Database + 1, second.Database); // a janela anterior foi descartada
        Assert.True(second.Reused > first.Reused);

        var repository = scope.ServiceProvider.GetRequiredService<IFinancialAccountRepository>();
        using (sharing.Begin())
        {
            await repository.ListAsync(_workspace.Id, true, CancellationToken.None);
            await repository.ListAsync(_otherWorkspace.Id, true, CancellationToken.None);
            await repository.ListAsync(_workspace.Id, false, CancellationToken.None);
        }
        Assert.Equal(second.Database + 2, sharing.Reads.Database); // uma entrada distinta por workspace
        Assert.True(sharing.Reads.Reused > second.Reused);
    }
}
