using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Saldo360.Api.Authentication;

/// <summary>
/// Processa <c>X-Forwarded-For/Proto/Host</c> somente de proxies configurados em <see cref="ReverseProxyOptions"/>.
/// Necessário para o callback OIDC usar a origem pública (HTTPS e host externo) atrás do proxy.
/// Não use <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>: ele limpa as listas e confia em qualquer origem.
/// </summary>
public static class ReverseProxySetup
{
    public static IServiceCollection AddSaldo360ForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var proxy = configuration.GetSection(ReverseProxyOptions.SectionName).Get<ReverseProxyOptions>()
            ?? new ReverseProxyOptions();

        // Interpretados já na inicialização: um valor inválido falha no startup.
        var knownProxies = proxy.KnownProxies
            .Select(value => IPAddress.TryParse(value, out var address) ? address : throw Invalid("KnownProxies", value))
            .ToList();
        var knownNetworks = proxy.KnownNetworks
            .Select(value => System.Net.IPNetwork.TryParse(value, out var network) ? network : throw Invalid("KnownNetworks", value))
            .ToList();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            // Acrescenta aos padrões (loopback), sem limpar as listas.
            foreach (var address in knownProxies)
            {
                options.KnownProxies.Add(address);
            }

            foreach (var network in knownNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }

            foreach (var host in proxy.AllowedHosts)
            {
                options.AllowedHosts.Add(host);
            }
        });

        return services;
    }

    private static InvalidOperationException Invalid(string key, string value) =>
        new($"Valor inválido em {ReverseProxyOptions.SectionName}:{key}: '{value}'.");
}
