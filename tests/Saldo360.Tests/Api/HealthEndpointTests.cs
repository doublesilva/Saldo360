using System.Net;
using System.Net.Http.Json;
using Saldo360.Api.Health;
using Saldo360.Infrastructure;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

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
        var check = Assert.Single(body.Checks);
        Assert.Equal(DependencyInjection.DatabaseHealthCheckName, check.Name);
        Assert.Equal("Healthy", check.Status);
    }

    [Fact]
    public async Task Health_ReturnsUnhealthy_WithoutLeakingCredentials_WhenDatabaseIsUnreachable()
    {
        const string password = "senha-que-nao-pode-vazar";
        // Porta 1 em loopback: conexão recusada rapidamente, sem depender de um PostgreSQL real.
        var connectionString = $"Host=127.0.0.1;Port=1;Database=saldo360;Username=saldo360;Password={password};Timeout=3";

        await using var factory = new Saldo360ApiFactory(connectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var rawBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"Unhealthy\"", rawBody);
        Assert.DoesNotContain(password, rawBody);
        Assert.DoesNotContain("Password", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(factory.Logs.Entries);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Contains(password));
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
