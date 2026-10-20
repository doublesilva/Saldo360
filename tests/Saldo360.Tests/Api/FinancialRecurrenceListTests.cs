using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Families;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Families;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialRecurrenceListTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyFamilyOwnerReceivesEmptyArray(bool includeArchived)
    {
        var owner = await SignInAsync("empty-recurrence-owner");
        var family = await CreateWorkspaceAsync(owner, "Empty recurrence family");
        Assert.Equal("Personal", family.Type);

        var response = await SendAsync(HttpMethod.Get,
            $"/api/financial-workspaces/{family.Id}/recurrences?includeArchived={includeArchived.ToString().ToLowerInvariant()}", owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
        await using var db = Db();
        Assert.False(await db.FinancialRecurrences.AnyAsync());
        Assert.False(await db.FinancialPlannedTransactions.AnyAsync());
        Assert.False(await db.FinancialTransactions.AnyAsync());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RepositoryListsEmptyActiveAndArchivedRows(bool active, bool archived, bool includeArchived)
    {
        var owner = await SignInAsync("repository-recurrence-owner");
        var family = await CreateWorkspaceAsync(owner, "Repository recurrence family");
        var expected = new List<FinancialRecurrence>();
        if (active || archived)
        {
            var accountResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/accounts", owner,
                new { name = "Synthetic account", type = "Checking", financialScope = "PF" });
            Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
            var accountId = (await ReadAsync<IdDto>(accountResponse)).Id;
            var categoryResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/categories", owner,
                new { name = "Synthetic expense", type = "Expense" });
            Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
            var categoryId = (await ReadAsync<IdDto>(categoryResponse)).Id;
            await using var db = Db();
            if (active) expected.Add(Create(new DateOnly(2027, 1, 10), false));
            if (archived) expected.Add(Create(new DateOnly(2026, 11, 10), true));
            db.FinancialRecurrences.AddRange(expected);
            await db.SaveChangesAsync();

            FinancialRecurrence Create(DateOnly start, bool archive)
            {
                var row = FinancialRecurrence.Create(family.Id,
                    new(accountId, categoryId, CategoryType.Expense, "Synthetic recurring expense", 19.83m, true,
                        RecurrenceFrequency.Monthly, 1, start, null, null, 10), DateTimeOffset.Parse("2026-10-19T12:00:00Z"));
                if (archive) row.Archive(DateTimeOffset.Parse("2026-10-19T13:00:00Z"));
                return row;
            }
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Saldo360DbContext>();
        var userId = await dbContext.FinancialWorkspaces.Where(m => m.Id == family.Id).Select(w => w.OwnerUserId).SingleAsync();
        Assert.Equal(FinancialWorkspaceRole.Owner, await scope.ServiceProvider.GetRequiredService<IFinancialWorkspaceRepository>()
            .GetOwnerRoleAsync(family.Id, userId, CancellationToken.None));
        // Isola participação e consulta de entidades antes de executar projeção/ordenação do repositório.
        Assert.Equal(expected.Count, await dbContext.FinancialRecurrences.Where(r => r.WorkspaceId == family.Id).CountAsync());
        var rows = await scope.ServiceProvider.GetRequiredService<IFinancialRecurrenceRepository>()
            .ListAsync(family.Id, includeArchived, CancellationToken.None);
        var visible = expected.Where(r => includeArchived || r.Status != RecurrenceStatus.Archived)
            .OrderBy(r => r.StartDate).ThenBy(r => r.Id).ToList();
        Assert.Equal(visible.Select(r => r.Id), rows.Select(r => r.Id));
        Assert.All(rows, row => Assert.Equal(19.83m, row.Amount));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IFinancialRecurrenceRepository>()
            .ListAsync(Guid.NewGuid(), true, CancellationToken.None));
    }

    private sealed record IdDto(Guid Id);
}
