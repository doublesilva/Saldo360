using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Saldo360.Tests.Support;

/// <summary>
/// Provedor OIDC em memória: atende o token endpoint e o userinfo pelo backchannel da API e emite
/// ID tokens assinados com uma chave de teste. Permite exercitar o callback real (/signin-oidc),
/// incluindo validação do token e as ClaimActions do handler.
/// </summary>
public sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string Issuer = "https://auth.test.invalid";
    public const string AuthorizationEndpoint = Issuer + "/authorize";
    public const string ClientId = "saldo360-tests";

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-signing-key" };

    /// <summary>Claims do ID token além de iss/aud/nonce/exp (ex.: sub). Ajuste antes do callback.</summary>
    public Dictionary<string, object> IdTokenClaims { get; } = [];

    /// <summary>Corpo JSON devolvido pelo userinfo.</summary>
    public Dictionary<string, object> UserInfo { get; } = [];

    /// <summary>Nonce enviado no pedido de autorização; precisa ir no ID token.</summary>
    public string? Nonce { get; set; }

    public OpenIdConnectConfiguration Configuration
    {
        get
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = Issuer,
                AuthorizationEndpoint = AuthorizationEndpoint,
                TokenEndpoint = Issuer + "/token",
                UserInfoEndpoint = Issuer + "/userinfo",
            };
            configuration.SigningKeys.Add(_signingKey);
            return configuration;
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json(new Dictionary<string, object>
            {
                ["access_token"] = "access-token-de-teste",
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
                ["id_token"] = CreateIdToken(),
            }),
            "/userinfo" => Json(UserInfo),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    private string CreateIdToken()
    {
        var claims = new Dictionary<string, object>(IdTokenClaims);
        if (Nonce is not null)
        {
            claims["nonce"] = Nonce;
        }

        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}
