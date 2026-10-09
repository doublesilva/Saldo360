using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>
/// Sobe a API em memória. Sem connection string explícita, troca o PostgreSQL por um banco InMemory.
/// </summary>
public sealed class Saldo360ApiFactory(string? connectionString = null) : WebApplicationFactory<Program>
{
    private const string InMemoryPlaceholder = "Host=in-memory;Database=saldo360_tests";

    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" evita carregar user-secrets do ambiente de desenvolvimento.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Saldo360", connectionString ?? InMemoryPlaceholder);

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        if (connectionString is null)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<Saldo360DbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<Saldo360DbContext>>();
                services.AddDbContext<Saldo360DbContext>(options =>
                    options.UseInMemoryDatabase($"saldo360-{Guid.NewGuid()}"));
            });
        }
    }
}

file static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(descriptor);
        }
    }
}
