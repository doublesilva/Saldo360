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
        Assert.DoesNotContain(cpf, result.ToString()); Assert.DoesNotContain(cpf, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(jwt, access.ToString()); Assert.DoesNotContain(jwt, JsonSerializer.Serialize(access));
    }
}
