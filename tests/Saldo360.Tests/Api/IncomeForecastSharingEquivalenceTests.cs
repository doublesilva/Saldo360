using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Calendar;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.MonthlyPlan;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class IncomeForecastSharingEquivalenceTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly Month => new(Today.Year, Today.Month, 1);
    private static DateOnly MonthEnd => Month.AddMonths(1).AddDays(-1);
    private sealed record IdDto(Guid Id);
    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Pf, Guid? Pj, Guid Income);

    private async Task<Setup> ArrangeAsync(string name, bool withPj)
    {
        var session = await SignInAsync(name);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body)
        {
            var response = await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await ReadAsync<IdDto>(response)).Id;
        }
        var opening = Month.AddMonths(-8);
        var pf = await Create("accounts", new { name = "PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "1000.00", openingBalanceDate = opening });
        var pj = withPj ? await Create("accounts", new { name = "PJ", type = "Checking", financialScope = "PJ", openingBalance = "1000.00", openingBalanceDate = opening }) : (Guid?)null;
        var income = await Create("categories", new { name = "Receita", type = "Income" });
        return new(session, workspace, pf, pj, income);
    }

    private async Task AddInferenceHistoryAsync(Setup setup)
    {
        Assert.NotNull(setup.Pj);
        await using var db = Db();
        for (var i = 1; i <= 6; i++)
        {
            var date = Month.AddMonths(-i).AddDays(9);
            db.FinancialTransactions.Add(FinancialTransaction.CreateFromPluggy(setup.Workspace.Id, setup.Pj!.Value, setup.Income,
                CategoryType.Income, "PIX CLIENTE ACME", 10000m + i * 100m, date, setup.Workspace.OwnerUserId, DateTimeOffset.UtcNow));
        }
        await db.SaveChangesAsync();
    }

    private async Task AddExplicitForecastAsync(Setup setup, decimal rate = 100m)
    {
        Assert.NotNull(setup.Pj);
        await using var db = Db();
        db.FinancialIncomeForecasts.Add(FinancialIncomeForecast.Create(setup.Workspace.Id, setup.Pj!.Value, rate, 8m,
            Math.Min(28, Today.Day + 1), 0, 10m, 0m, true, setup.Workspace.OwnerUserId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("inferred")]
    [InlineData("explicit")]
    public async Task SameKeySharesExplicitInferredAndNoneWithoutChangingTheForecast(string kind)
    {
        var setup = await ArrangeAsync("forecast-sharing-" + kind, kind != "none");
        if (kind == "inferred") await AddInferenceHistoryAsync(setup);
        if (kind == "explicit") await AddExplicitForecastAsync(setup);

        using var scope = Factory.Services.CreateScope();
        var sharing = scope.ServiceProvider.GetRequiredService<IIncomeForecastSharing>();
        var db = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
        sharing.Enabled = false;
        var original = await IncomeForecastLoader.LoadAsync(db, setup.Workspace.Id, Month, MonthEnd, Today, default);
        sharing.Enabled = true;
        IncomeForecastView first;
        IncomeForecastView second;
        using (sharing.Begin())
        {
            first = await IncomeForecastLoader.LoadAsync(db, setup.Workspace.Id, Month, MonthEnd, Today, default);
            second = await IncomeForecastLoader.LoadAsync(db, setup.Workspace.Id, Month, MonthEnd, Today, default);
        }

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(first));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal((1, 1), sharing.Reads);
        Assert.Equal(kind == "none", first == IncomeForecastView.None);
        Assert.Equal(kind == "inferred", first.Inferred);
        Assert.Equal(kind == "explicit", first.Settings is not null && !first.Inferred);
    }

    [Fact]
    public async Task DifferentHorizonsWorkspacesAndRequestsHaveIndependentReads()
    {
        var first = await ArrangeAsync("forecast-sharing-keys-a", false);
        var second = await ArrangeAsync("forecast-sharing-keys-b", false);
        using (var scope = Factory.Services.CreateScope())
        {
            var sharing = scope.ServiceProvider.GetRequiredService<IIncomeForecastSharing>();
            var db = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
            using (sharing.Begin())
            {
                await IncomeForecastLoader.LoadAsync(db, first.Workspace.Id, Month, MonthEnd, Today, default);
                await IncomeForecastLoader.LoadAsync(db, first.Workspace.Id, Month, MonthEnd.AddMonths(1), Today, default);
                await IncomeForecastLoader.LoadAsync(db, second.Workspace.Id, Month, MonthEnd, Today, default);
                sharing.Invalidate(); // nova tentativa de consistência: o mesmo recorte deve voltar ao banco
                await IncomeForecastLoader.LoadAsync(db, first.Workspace.Id, Month, MonthEnd, Today, default);
            }
            Assert.Equal((4, 0), sharing.Reads);
        }
        using (var scope = Factory.Services.CreateScope())
        {
            var sharing = scope.ServiceProvider.GetRequiredService<IIncomeForecastSharing>();
            var db = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
            using (sharing.Begin()) await IncomeForecastLoader.LoadAsync(db, first.Workspace.Id, Month, MonthEnd, Today, default);
            Assert.Equal((1, 0), sharing.Reads);
        }
    }

    [Fact]
    public async Task AForecastChangedBetweenTransactionsIsObservedByTheNextAgendaSnapshot()
    {
        var setup = await ArrangeAsync("forecast-sharing-transactions", true);
        using var scope = Factory.Services.CreateScope();
        var sharing = scope.ServiceProvider.GetRequiredService<IIncomeForecastSharing>();
        var repository = scope.ServiceProvider.GetRequiredService<IFinancialCalendarBalanceRepository>();
        using var window = sharing.Begin();

        var before = await repository.ReadAsync(setup.Workspace.Id, setup.Pj, MonthEnd, Today, default);
        await AddExplicitForecastAsync(setup, 250m);
        var after = await repository.ReadAsync(setup.Workspace.Id, setup.Pj, MonthEnd, Today, default);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Empty(before.InferredPending ?? []);
        Assert.NotEmpty(after.InferredPending ?? []);
        Assert.True(sharing.Reads.Database >= 2);
    }

    [Fact]
    public async Task DashboardPathsAreEquivalentWithSharingEnabledAndDisabled()
    {
        var setup = await ArrangeAsync("forecast-sharing-dashboard", true);
        await AddExplicitForecastAsync(setup);

        async Task<(string Summary, string Overview)> ReadAsync(bool enabled)
        {
            using var scope = Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IIncomeForecastSharing>().Enabled = enabled;
            var service = scope.ServiceProvider.GetRequiredService<DashboardOverviewService>();
            var month = Month.ToString("yyyy-MM");
            var summary = await service.GetBudgetSummaryAsync(setup.Workspace.Id, setup.Workspace.OwnerUserId, month, default);
            var overview = await service.GetAsync(setup.Workspace.Id, setup.Workspace.OwnerUserId, month, "all", null, default);
            return (JsonSerializer.Serialize(summary), JsonSerializer.Serialize(overview));
        }

        Assert.Equal(await ReadAsync(false), await ReadAsync(true));
    }
}
