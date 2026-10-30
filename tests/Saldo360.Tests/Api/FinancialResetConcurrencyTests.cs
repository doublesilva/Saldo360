using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Recurrences;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Workspaces")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialResetConcurrencyTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private async Task<(string Session, WorkspaceDto Workspace)> Arrange()
    {
        var session = await SignInAsync("reset-owner"); var w = await CreateWorkspaceAsync(session);
        await using var db = Db(); await FinancialResetFixture.SeedAsync(db, w.Id, w.OwnerUserId);
        return (session, w);
    }
    private async Task AssertEmpty(Guid w)
    {
        await using var db = Db();
        var preview = await new FinancialWorkspaceResetService(db).PreviewAsync(w, (await db.FinancialWorkspaces.SingleAsync(x => x.Id == w)).OwnerUserId, default);
        Assert.All(preview.Counts, item => Assert.Equal(0, item.Value));
    }
    [Fact]
    public async Task TenConcurrentResets_OnlyOneDeletes_AndAllOtherExecutionsAreEmpty()
    {
        var (session, w) = await Arrange();
        var results = await Task.WhenAll(Enumerable.Range(0,10).Select(_ => SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/financial-reset", session, new { confirmation = "APAGAR ORÇAMENTO" })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(results.Select(ReadAsync<JsonElement>));
        Assert.Single(bodies, b => b.GetProperty("status").GetString() == "ResetCompleted");
        Assert.Equal(9, bodies.Count(b => b.GetProperty("status").GetString() == "AlreadyEmpty"));
        await AssertEmpty(w.Id);
    }
    [Fact]
    [Trait("Profile", "FAST")]
    public async Task FailureAfterDeletes_RollsBackAllContentAuditAndResetVersion()
    {
        var (session, w) = await Arrange();
        string before;
        await using (var snapshot = Db()) before = await FinancialResetFixture.FingerprintAsync(snapshot, w.Id);
        await using (var setup = Db())
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION synthetic_reset_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'SyntheticResetFailure'; END $$;
                CREATE TRIGGER synthetic_reset_failure AFTER DELETE ON financial_accounts FOR EACH STATEMENT EXECUTE FUNCTION synthetic_reset_failure();
                """);
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/financial-reset", session, new { confirmation = "APAGAR ORÇAMENTO" });
        Assert.False(response.IsSuccessStatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("SyntheticResetFailure", await response.Content.ReadAsStringAsync());
        await using var db = Db();
        Assert.Equal(before, await FinancialResetFixture.FingerprintAsync(db, w.Id));
        var preview = await new FinancialWorkspaceResetService(db).PreviewAsync(w.Id, w.OwnerUserId, default);
        Assert.All(preview.Counts, item => Assert.True(item.Value > 0, item.Key));
        Assert.Empty(await db.Set<FinancialResetAudit>().ToListAsync());
        Assert.Null((await db.FinancialWorkspaces.SingleAsync()).LastFinancialResetAt);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriterStartedBeforeReset_CannotLeaveTransactionOrMaterializationBehind(bool materialize)
    {
        var (_, w) = await Arrange();
        using var operation = FinancialWriteScope.Enter(w.Id);
        await using var writer = Db();
        // This DB read captures the original epoch before the reset, as in a long-running import.
        var account = await writer.FinancialAccounts.AsNoTracking().FirstAsync();
        var category = await writer.FinancialCategories.AsNoTracking().SingleAsync();
        var recurrence = await writer.FinancialRecurrences.AsNoTracking().SingleAsync();
        // Independent request/execution context, not the suspended writer context.
        using (FinancialWriteScope.Enter(w.Id))
        {
            await using var resetDb = Db();
            await new FinancialWorkspaceResetService(resetDb).ResetAsync(w.Id, w.OwnerUserId, "APAGAR ORÇAMENTO", "synthetic", default);
        }
        var date = new DateOnly(2026,10,1);
        if (materialize)
        {
            var planned = FinancialPlannedTransaction.Create(w.Id, PlannedTransactionDetails.TryCreate(account.Id,category.Id,"Expense","Pending writer",1m,date,out _)!,w.OwnerUserId,DateTimeOffset.UtcNow);
            writer.Add(planned);
            writer.Add(FinancialRecurrenceMaterialization.Create(w.Id,recurrence.Id,2,date.AddMonths(1),planned.Id,DateTimeOffset.UtcNow));
        }
        else writer.Add(FinancialTransaction.Create(w.Id,TransactionDetails.TryCreate(account.Id,category.Id,"Expense","Pending writer",1m,date,date,out _)!,w.OwnerUserId,DateTimeOffset.UtcNow));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => writer.SaveChangesAsync());
        Assert.Equal("FINANCIAL_WORKSPACE_RESET_CONFLICT", Assert.IsType<Npgsql.PostgresException>(error.InnerException).MessageText);
        await AssertEmpty(w.Id);
    }
    [Theory]
    [InlineData("transactions","transactionDate")]
    [InlineData("planned-transactions","dueDate")]
    public async Task HttpWriterAndReset_SerializeInPostgreSql_WithoutResidualRows(string resource,string dateField)
    {
        var (session,w)=await Arrange();
        var resetSession=await SignInAsync("reset-owner");
        await using var blocker=Db(); var account=await blocker.FinancialAccounts.FirstAsync(); var category=await blocker.FinancialCategories.SingleAsync();
        await using var hold=await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id={w.Id} FOR UPDATE");
        var body=new Dictionary<string,object> { ["accountId"]=account.Id,["categoryId"]=category.Id,["type"]="Expense",["description"]="Concurrent",["amount"]="1.00",[dateField]="2026-10-01" };
        var writer=SendWithCsrfAsync(HttpMethod.Post,$"/api/financial-workspaces/{w.Id}/{resource}",session,body);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task WaitForLocks(int count)
        {
            while(true)
            {
                await using var observer=Db();
                var waiting=await observer.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock'").SingleAsync(timeout.Token);
                if(waiting>=count) return;
                await Task.Delay(25,timeout.Token);
            }
        }
        await WaitForLocks(1);
        var reset=SendWithCsrfAsync(HttpMethod.Post,$"/api/financial-workspaces/{w.Id}/financial-reset",resetSession,new { confirmation="APAGAR ORÇAMENTO" });
        await WaitForLocks(2); await hold.CommitAsync();
        var resetResponse=await reset; var writerResponse=await writer;
        Assert.Equal(HttpStatusCode.OK,resetResponse.StatusCode);
        Assert.Contains(writerResponse.StatusCode,new[] { HttpStatusCode.Created,HttpStatusCode.Conflict,HttpStatusCode.BadRequest,HttpStatusCode.NotFound });
        await AssertEmpty(w.Id);
    }
}
