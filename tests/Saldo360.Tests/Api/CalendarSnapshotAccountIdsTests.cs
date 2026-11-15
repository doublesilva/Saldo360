using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Calendar;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transfers;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// O snapshot da Agenda deriva os IDs das contas selecionadas da própria lista já materializada na transação. Os recortes (conta específica, consolidado, conta inativa
/// com histórico, conta de outro workspace e conjunto vazio) mantêm contas, saldos de abertura, realizados e transferências previstas. PostgreSQL real.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CalendarSnapshotAccountIdsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static DateOnly Through => Today.AddDays(10);
    private sealed record IdDto(Guid Id);

    private async Task<Guid> AccountAsync(string session, WorkspaceDto workspace, string name, string opening, DateOnly openingDate, bool active = true)
    {
        var root = $"/api/financial-workspaces/{workspace.Id}";
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{root}/accounts", session,
            new { name, type = "Checking", financialScope = "PF", openingBalance = opening, openingBalanceDate = openingDate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await ReadAsync<IdDto>(response)).Id;
        if (!active)
            Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{root}/accounts/{id}", session,
                new { name, type = "Checking", financialScope = "PF", openingBalance = opening, openingBalanceDate = openingDate, isActive = false })).StatusCode);
        return id;
    }

    private async Task<(FinancialCalendarBalanceSnapshot? Snapshot, FinancialCalendarBalanceSnapshot? Monthly)> ReadAsync(Guid workspaceId, Guid? accountId)
    {
        using var scope = Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IFinancialCalendarBalanceRepository>();
        return (await repository.ReadAsync(workspaceId, accountId, Through, Today, CancellationToken.None),
            await repository.ReadMonthlyAsync(workspaceId, accountId, ThisMonth, Through, Today, CancellationToken.None));
    }

    [Fact]
    public async Task EverySliceKeepsAccountsOpeningBalancesAndPlannedTransfers()
    {
        var session = await SignInAsync("agenda-ids");
        var workspace = await CreateWorkspaceAsync(session);
        var other = await CreateWorkspaceAsync(await SignInAsync("agenda-ids-outro"));
        var a = await AccountAsync(session, workspace, "A", "1000.00", ThisMonth.AddMonths(-2));
        var b = await AccountAsync(session, workspace, "B", "2000.00", ThisMonth.AddMonths(-1));
        var c = await AccountAsync(session, workspace, "C inativa", "500.00", ThisMonth.AddMonths(-1), active: false);
        var due = Today.AddDays(3);
        await using (var db = Db())
        {
            var details = TransferDetails.TryCreate(a, b, 100m, due, "Previsto A para B", Today, out _, allowFutureDate: true)!;
            db.FinancialPlannedTransfers.Add(FinancialPlannedTransfer.Create(workspace.Id, details, workspace.OwnerUserId, DateTimeOffset.UtcNow));
            var withInactive = TransferDetails.TryCreate(b, c, 30m, due, "Previsto B para inativa", Today, out _, allowFutureDate: true)!;
            db.FinancialPlannedTransfers.Add(FinancialPlannedTransfer.Create(workspace.Id, withInactive, workspace.OwnerUserId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        foreach (var snapshot in await Both(workspace.Id, null))
        {   // consolidado: as três contas (a inativa com histórico inclusive); transferências internas se anulam
            Assert.Equal(new[] { a, b, c }.Order(), snapshot!.Accounts.Select(x => x.Id).Order());
            Assert.DoesNotContain(snapshot.Pending, row => row.TransferIn != 0m || row.TransferOut != 0m);
        }
        foreach (var snapshot in await Both(workspace.Id, a))
        {
            Assert.Equal([a], snapshot!.Accounts.Select(x => x.Id));
            Assert.Equal((1000m, ThisMonth.AddMonths(-2)), (snapshot.Accounts[0].OpeningBalance, snapshot.Accounts[0].OpeningBalanceDate));
            Assert.Equal((due, 0m, 100m), SingleTransfer(snapshot));
        }
        foreach (var snapshot in await Both(workspace.Id, b))
        {
            Assert.Equal([b], snapshot!.Accounts.Select(x => x.Id));
            Assert.Equal((due, 100m, 30m), SingleTransfer(snapshot)); // recebe 100 de A e envia 30 à inativa
        }
        foreach (var snapshot in await Both(workspace.Id, c))
        {   // conta inativa com histórico continua selecionável
            Assert.Equal([c], snapshot!.Accounts.Select(x => x.Id));
            Assert.Equal((due, 30m, 0m), SingleTransfer(snapshot));
        }
        // conta de outro workspace: não pertence à família
        var foreign = await AccountAsync(await SignInAsync("agenda-ids-outro"), other, "Alheia", "10.00", ThisMonth);
        foreach (var snapshot in await Both(workspace.Id, foreign)) Assert.Null(snapshot);
        // conjunto vazio: workspace sem contas, consolidado devolve snapshot sem contas e sem previsões
        var emptyWorkspace = await CreateWorkspaceAsync(await SignInAsync("agenda-ids-vazio"));
        foreach (var snapshot in await Both(emptyWorkspace.Id, null))
        {
            Assert.Empty(snapshot!.Accounts);
            Assert.Empty(snapshot.Pending);
        }
    }

    private async Task<FinancialCalendarBalanceSnapshot?[]> Both(Guid workspaceId, Guid? accountId)
    {
        var (snapshot, monthly) = await ReadAsync(workspaceId, accountId);
        return [snapshot, monthly];
    }

    private static (DateOnly Date, decimal In, decimal Out) SingleTransfer(FinancialCalendarBalanceSnapshot snapshot)
    {
        var row = Assert.Single(snapshot.Pending, r => r.TransferIn != 0m || r.TransferOut != 0m);
        return (row.Date, row.TransferIn, row.TransferOut);
    }
}
