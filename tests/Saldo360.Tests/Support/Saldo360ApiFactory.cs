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
using Saldo360.Infrastructure.Authentication;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>
/// Sobe a API em memória. Sem connection string explícita, troca o PostgreSQL por um banco InMemory.
/// O provedor OIDC é o <see cref="FakeOidcProvider"/>; <see cref="SignInPath"/> cria sessões diretamente nos testes.
/// </summary>
public sealed class Saldo360ApiFactory(string? connectionString = null) : WebApplicationFactory<Program>
{
    public const string TestClientSecret = "segredo-do-cliente-que-nao-pode-vazar";
    public const string TestAuthority = FakeOidcProvider.Issuer;
    public const string TestAuthorizationEndpoint = FakeOidcProvider.AuthorizationEndpoint;

    /// <summary>
    /// Endpoint exclusivo dos testes: cria uma sessão real (cookie + ticket store) com as claims
    /// <c>iss</c> e <c>sub</c> informadas na query (padrão: <see cref="TestAuthority"/> e "user-123").
    /// Não passa pelo fluxo OIDC; para isso, use o callback com <see cref="OidcProvider"/>.
    /// </summary>
    public const string SignInPath = "/test/sign-in";

    private const string InMemoryPlaceholder = "Host=in-memory;Database=saldo360_tests";

    private readonly string _inMemoryDatabaseName = $"saldo360-{Guid.NewGuid()}";

    public CapturingLoggerProvider Logs { get; } = new();

    public FakeOidcProvider OidcProvider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" evita carregar user-secrets do ambiente de desenvolvimento e
        // aplica a política de cookies de produção (Secure, __Host-).
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Saldo360", connectionString ?? InMemoryPlaceholder);
        builder.UseSetting("Authentication:Oidc:Authority", TestAuthority);
        builder.UseSetting("Authentication:Oidc:ClientId", FakeOidcProvider.ClientId);
        builder.UseSetting("Authentication:Oidc:ClientSecret", TestClientSecret);

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<OpenIdConnectOptions>(AuthenticationSetup.OidcScheme, options =>
            {
                var configuration = OidcProvider.Configuration;
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.Backchannel = new HttpClient(OidcProvider);
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
                    new Claim(ExternalIdentity.IssuerClaimType, context.Request.Query["iss"].FirstOrDefault() ?? TestAuthority),
                    new Claim(ExternalIdentity.SubjectClaimType, context.Request.Query["sub"].FirstOrDefault() ?? "user-123"),
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
