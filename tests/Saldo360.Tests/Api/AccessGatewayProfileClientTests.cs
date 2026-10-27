using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Saldo360.Api.Authentication;
using Saldo360.Application.Users;
using Saldo360.Infrastructure.Authentication;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Trait("Category", "CORE")]
[Trait("Area", "Security")]
[Trait("Profile", "FAST")]
public sealed class AccessGatewayProfileClientTests
{
    [Fact]
    public async Task FullCpfIsAvailableOnlyInternallyAndNeverSerialized()
    {
        const string cpf = "52998224725";
        var expected = new ExternalIdentity("https://auth.synthetic.invalid", "Subject|Opaque");
        var key = new RsaSecurityKey(RSA.Create(2048));
        var configuration = new OpenIdConnectConfiguration(); configuration.SigningKeys.Add(key);
        var options = new OpenIdConnectOptions { ClientId = "saldo360", Configuration = configuration };
        var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = expected.Issuer, Audience = ProfileAccessTokenValidation.Audience, TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = expected.Subject, ["client_id"] = "saldo360" },
            Expires = DateTime.UtcNow.AddMinutes(5), SigningCredentials = new(key, SecurityAlgorithms.RsaSha256)
        });
        var access = await ProfileAccessTokenValidation.ValidateAsync(jwt, "Bearer", expected, options, default);
        Assert.Equal(UserCpfStatus.Available, access.Status);
        var handler = new FakeProfileGateway { Status = System.Net.HttpStatusCode.OK,
            Body = JsonSerializer.Serialize(new { issuer = expected.Issuer, subject = expected.Subject, cpfRegistered = true, cpf }) };
        using var http = new HttpClient(handler);
        var result = await new AccessGatewayProfileClient(http).GetAsync(access, expected, default);
        Assert.Equal(cpf, result.FullValue); Assert.Equal(UserCpfStatus.Available, result.Status);
        Assert.Equal(UserCpfDiagnosticCode.ProfileAvailable, result.DiagnosticCode);
        Assert.DoesNotContain(cpf, result.ToString()); Assert.DoesNotContain(cpf, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(jwt, access.ToString()); Assert.DoesNotContain(jwt, JsonSerializer.Serialize(access));
    }

    [Theory]
    [InlineData(null, "Bearer", "AccessTokenMissing")]
    [InlineData(" ", "Bearer", "AccessTokenMissing")]
    [InlineData("opaque-sensitive-token", "Bearer", "AccessTokenMalformed")]
    [InlineData("opaque-sensitive-token", null, "BearerTokenTypeInvalid")]
    public async Task AbsentAndMalformedAccessTokensHaveSpecificSafeDiagnostics(string? token, string? type, string code)
    {
        var result = await ProfileAccessTokenValidation.ValidateAsync(token, type, new("https://auth.synthetic.invalid", "subject"), new(), default);
        Assert.Equal(code, result.DiagnosticCode.ToString());
        Assert.Null(result.Token);
        Assert.DoesNotContain("opaque-sensitive-token", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task DiscoveryConfigurationManagerAndPublicJwkValidateTheProfileAccessToken()
    {
        var expected = new ExternalIdentity("https://auth.synthetic.invalid", "opaque-subject");
        var privateKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "synthetic-rs256" };
        // Production uses public discovery/JWKS, not the private RSA object used by simpler fixtures.
        var publicJwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(privateKey.Rsa.ExportParameters(false)) { KeyId = privateKey.KeyId });
        var configuration = new OpenIdConnectConfiguration { Issuer = expected.Issuer };
        configuration.SigningKeys.Add(publicJwk);
        var options = new OpenIdConnectOptions { ClientId = "saldo360", ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(configuration) };
        var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = expected.Issuer, Audience = ProfileAccessTokenValidation.Audience, TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = expected.Subject, ["client_id"] = options.ClientId },
            Expires = DateTime.UtcNow.AddMinutes(5), SigningCredentials = new(privateKey, SecurityAlgorithms.RsaSha256)
        });
        var access = await ProfileAccessTokenValidation.ValidateAsync(jwt, "Bearer", expected, options, default);
        Assert.Equal(UserCpfStatus.Available, access.Status); Assert.Equal(UserCpfDiagnosticCode.AccessTokenValidated, access.DiagnosticCode);
        Assert.Null(options.Configuration);
    }
}
