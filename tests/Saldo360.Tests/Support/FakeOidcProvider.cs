using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Saldo360.Tests.Support;

/// <summary>
/// Provedor OIDC em memória: atende PAR (opcional), token endpoint e userinfo pelo backchannel da API
/// e emite ID tokens assinados com uma chave de teste. Permite exercitar o callback real (/signin-oidc),
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

    /// <summary>Quando definido, o token endpoint responde 400 com este corpo (erro OAuth).</summary>
    public Dictionary<string, object>? TokenError { get; set; }

    /// <summary>Anuncia e atende o endpoint de PAR (como o Authelia do servidor).</summary>
    public bool EnablePushedAuthorization { get; set; }
    public string? ProfileTokenScenario { get; set; }
    public string? LastAccessToken { get; private set; }

    /// <summary>Requisições recebidas, por endpoint ("/par", "/token", "/userinfo").</summary>
    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

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
            if (EnablePushedAuthorization)
            {
                configuration.PushedAuthorizationRequestEndpoint = Issuer + "/par";
            }

            configuration.SigningKeys.Add(_signingKey);
            return configuration;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new ReceivedRequest(path, QueryHelpers.ParseQuery(body), request.Headers.Authorization is not null));

        return path switch
        {
            "/par" when EnablePushedAuthorization => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new Dictionary<string, object>
                {
                    ["request_uri"] = "urn:ietf:params:oauth:request_uri:teste",
                    ["expires_in"] = 60,
                }),
            },
            "/token" when TokenError is not null => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(TokenError),
            },
            "/token" => Json(new Dictionary<string, object>
            {
                ["access_token"] = LastAccessToken = ProfileTokenScenario is null ? "access-token-de-teste" : CreateProfileToken(),
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
                ["id_token"] = CreateIdToken(),
            }),
            "/userinfo" => Json(UserInfo),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
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

    private string CreateProfileToken()
    {
        var now = DateTime.UtcNow;
        var key = ProfileTokenScenario == "signature" ? new RsaSecurityKey(RSA.Create(2048)) { KeyId = "wrong-key" } : _signingKey;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = ProfileTokenScenario == "subject" ? "other" : IdTokenClaims["sub"],
            ["client_id"] = ProfileTokenScenario == "client" ? "other-client" : ClientId
        };
        if (ProfileTokenScenario == "cpf-claim") claims["cpf"] = "52998224725";
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ProfileTokenScenario == "issuer" ? "https://other.invalid" : Issuer,
            Audience = ProfileTokenScenario == "audience" ? ClientId : "accessgateway-profile",
            TokenType = ProfileTokenScenario == "id-token" ? "JWT" : "at+jwt", Claims = claims,
            IssuedAt = now.AddMinutes(-10), NotBefore = now.AddMinutes(-10),
            Expires = ProfileTokenScenario == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}

/// <param name="Path">Caminho do endpoint chamado.</param>
/// <param name="Form">Corpo form-urlencoded recebido.</param>
/// <param name="HasAuthorizationHeader">Se veio header Authorization (ex.: client_secret_basic).</param>
public sealed record ReceivedRequest(string Path, Dictionary<string, StringValues> Form, bool HasAuthorizationHeader);
