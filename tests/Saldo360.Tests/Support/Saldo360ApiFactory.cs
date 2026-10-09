using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Saldo360.Api.Authentication;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>
/// Sobe a API em memória. Sem connection string explícita, troca o PostgreSQL por um banco InMemory.
/// O provedor OIDC é simulado por uma discovery estática; <see cref="SignInPath"/> cria sessões nos testes.
/// </summary>
public sealed class Saldo360ApiFactory(string? connectionString = null) : WebApplicationFactory<Program>
{
    public const string TestClientSecret = "segredo-do-cliente-que-nao-pode-vazar";
    public const string TestAuthority = "https://auth.test.invalid";
    public const string TestAuthorizationEndpoint = TestAuthority + "/authorize";

    /// <summary>Endpoint exclusivo dos testes: cria uma sessão real (cookie + ticket store) para o usuário informado.</summary>
    public const string SignInPath = "/test/sign-in";

    private const string InMemoryPlaceholder = "Host=in-memory;Database=saldo360_tests";

    private readonly string _inMemoryDatabaseName = $"saldo360-{Guid.NewGuid()}";

    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" evita carregar user-secrets do ambiente de desenvolvimento e
        // aplica a política de cookies de produção (Secure, __Host-).
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Saldo360", connectionString ?? InMemoryPlaceholder);
        builder.UseSetting("Authentication:Oidc:Authority", TestAuthority);
        builder.UseSetting("Authentication:Oidc:ClientId", "saldo360-tests");
        builder.UseSetting("Authentication:Oidc:ClientSecret", TestClientSecret);

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<OpenIdConnectOptions>(AuthenticationSetup.OidcScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = TestAuthority,
                    AuthorizationEndpoint = TestAuthorizationEndpoint,
                    TokenEndpoint = TestAuthority + "/token",
                };
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });

            services.AddSingleton<IStartupFilter, TestSignInStartupFilter>();

            if (connectionString is null)
            {
                services.RemoveAll<DbContextOptions<Saldo360DbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<Saldo360DbContext>>();
                services.AddDbContext<Saldo360DbContext>(options => options.UseInMemoryDatabase(_inMemoryDatabaseName));
            }
        });
    }

    private sealed class TestSignInStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(SignInPath, signIn => signIn.Run(async context =>
            {
                var claims = new[]
                {
                    new Claim("sub", context.Request.Query["sub"].FirstOrDefault() ?? "user-123"),
                    new Claim("name", "Maria Silva"),
                    new Claim("preferred_username", "maria"),
                    new Claim("email", "maria@example.com"),
                };
                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", "role"));
                await context.SignInAsync(AuthenticationSetup.CookieScheme, principal);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }));

            next(app);
        };
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
