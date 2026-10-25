using System.Net;
using System.Net.Http.Json;
using Saldo360.Api.Health;
using Saldo360.Infrastructure;
using Saldo360.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Saldo360.Tests.Api;

[Trait("Category", "CORE")]
public sealed class HealthEndpointTests
{
    [Fact]
    public async Task Health_ReturnsHealthy_WhenDatabaseIsReachable()
    {
        await using var factory = new Saldo360ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(body);
        Assert.Equal("Healthy", body.Status);
        var legacyCheck = Assert.Single(body.Checks);
        Assert.Equal(DependencyInjection.DatabaseHealthCheckName, legacyCheck.Name);
        Assert.Equal("Healthy", legacyCheck.Status);

        var live = await client.GetFromJsonAsync<HealthResponse>("/health/live");
        Assert.NotNull(live);
        Assert.Equal("Healthy", live.Status);
        Assert.Equal("self", Assert.Single(live.Checks).Name);

        var ready = await client.GetFromJsonAsync<HealthResponse>("/health/ready");
        Assert.NotNull(ready);
        Assert.Equal("Healthy", ready.Status);
        Assert.Equal(DependencyInjection.DatabaseHealthCheckName, Assert.Single(ready.Checks).Name);
    }

    [Fact]
    public async Task Health_ReturnsUnhealthy_WithoutLeakingCredentials_WhenDatabaseIsUnreachable()
    {
        const string password = "senha-que-nao-pode-vazar";
        // Porta 1 em loopback: conexão recusada rapidamente, sem depender de um PostgreSQL real.
        var connectionString = $"Host=127.0.0.1;Port=1;Database=saldo360;Username=saldo360;Password={password};Timeout=3";

        await using var factory = new Saldo360ApiFactory(connectionString);
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live?token=segredo-na-query");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var response = await client.GetAsync("/health/ready");
        var rawBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"Unhealthy\"", rawBody);
        Assert.DoesNotContain(password, rawBody);
        Assert.DoesNotContain("Password", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(password));
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains("segredo-na-query"));
        var dependency = Assert.Single(factory.Logs.StructuredEntries, entry => entry.Category.EndsWith("DatabaseReadinessHealthCheck"));
        Assert.Equal(LogLevel.Warning, dependency.Level);
        Assert.Equal("PostgreSQL", dependency.Properties["Dependency"]);
        Assert.Equal("Unhealthy", dependency.Properties["DependencyResult"]);
    }

    [Fact]
    public void Startup_FailsWithClearMessage_WhenConnectionStringIsMissing()
    {
        using var factory = new Saldo360ApiFactory(connectionString: "");

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var inner = exception as InvalidOperationException ?? exception.InnerException;
        Assert.IsType<InvalidOperationException>(inner);
        Assert.Contains("ConnectionStrings:Saldo360", inner.Message);
    }
}
