using System.Net;
using System.Net.Http.Json;
using Saldo360.Api.Health;
using Saldo360.Infrastructure;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Platform")]
[Trait("Dependency", "PostgreSQL")]
public sealed class HealthReadinessPostgresTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    [Trait("Profile", "FAST")]
    public async Task Readiness_IsHealthy_WithMigratedPostgres_AndIntroducesNoFinancialWrites()
    {
        var response = await Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(body);
        Assert.Equal("Healthy", body.Status);
        var check = Assert.Single(body.Checks);
        Assert.Equal(DependencyInjection.DatabaseHealthCheckName, check.Name);
        await using var db = Db();
        Assert.Empty(db.FinancialTransactions);
        Assert.Empty(db.FinancialGoalAllocations);
        Assert.Empty(db.FinancialDebtPayments);
    }
}
