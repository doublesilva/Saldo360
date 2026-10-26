using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// A API recebe HTTP interno do proxy; o callback OIDC precisa usar a origem pública (HTTPS + host externo),
/// mas só quando os headers encaminhados vêm de um proxy ou rede explicitamente confiável.
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Security")]
public sealed class ForwardedHeadersTests() : ApiTestBase(
    new Dictionary<string, string?>
    {
        ["ReverseProxy:KnownProxies:0"] = TrustedProxy,
        ["ReverseProxy:KnownNetworks:0"] = "172.30.0.0/16",
        ["ReverseProxy:AllowedHosts:0"] = PublicHost,
    },
    baseAddress: "http://saldo360-api:8080")
{
    private const string TrustedProxy = "10.0.0.10";
    private const string PublicHost = "saldo360.exemplo.test";
    private const string InternalCallback = "http://saldo360-api:8080/signin-oidc";
    private const string PublicCallback = "https://" + PublicHost + "/signin-oidc";

    [Theory]
    [InlineData(TrustedProxy)]
    [InlineData("172.30.5.9")] // rede confiável
    [InlineData("127.0.0.1")] // loopback continua confiável (padrão do ASP.NET Core)
    [Trait("Profile", "FAST")]
    public async Task TrustedProxy_ProducesHttpsCallbackWithPublicHost(string remoteIp)
    {
        var callback = await GetCallbackAsync(remoteIp, proto: "https", host: PublicHost);

        Assert.Equal(PublicCallback, callback);
    }

    [Theory]
    [InlineData("203.0.113.7")] // internet
    [InlineData("172.31.0.1")] // fora da rede confiável
    [InlineData("10.0.0.11")] // vizinho do proxy, mas não ele
    [Trait("Profile", "FAST")]
    public async Task UntrustedOrigin_ForwardedHeadersAreIgnored(string remoteIp)
    {
        var callback = await GetCallbackAsync(remoteIp, proto: "https", host: PublicHost);

        Assert.Equal(InternalCallback, callback);
    }

    [Fact]
    public async Task TrustedProxy_WithHostOutsideAllowList_DoesNotChangeHost()
    {
        var callback = await GetCallbackAsync(TrustedProxy, proto: "https", host: "evil.example");

        Assert.Equal("saldo360-api:8080", new Uri(callback).Authority);
    }

    [Fact]
    public async Task TrustedProxy_WithoutForwardedHeaders_KeepsInternalOrigin()
    {
        var callback = await GetCallbackAsync(TrustedProxy, proto: null, host: null);

        Assert.Equal(InternalCallback, callback);
    }

    [Fact]
    public void InvalidProxyConfiguration_FailsAtStartup()
    {
        using var factory = new Saldo360ApiFactory(settings: new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownProxies:0"] = "nao-e-um-ip",
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var inner = exception as InvalidOperationException ?? exception.InnerException;
        Assert.Contains("ReverseProxy:KnownProxies", inner!.Message);
    }

    /// <summary>Chama /auth/login simulando a origem da conexão e devolve o redirect_uri enviado ao provedor.</summary>
    private async Task<string> GetCallbackAsync(string remoteIp, string? proto, string? host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/login?returnUrl=%2F");
        request.Headers.Add(Saldo360ApiFactory.TestRemoteIpHeader, remoteIp);
        if (proto is not null) request.Headers.Add("X-Forwarded-Proto", proto);
        if (host is not null) request.Headers.Add("X-Forwarded-Host", host);
        request.Headers.Add("X-Forwarded-For", "198.51.100.20");

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"]!;
    }
}
