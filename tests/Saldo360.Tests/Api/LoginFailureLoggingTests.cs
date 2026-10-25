using System.Net;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Falhas de login com dados sensíveis fictícios: o log deve conter só o motivo controlado e o id de
/// correlação, nunca o code, tokens, segredos ou o texto devolvido pelo provedor.
/// </summary>
[Trait("Category", "CORE")]
public sealed class LoginFailureLoggingTests : ApiTestBase
{
    private const string FakeCode = "CODIGO-AUTORIZACAO-FICTICIO-9f8e7d";
    private const string FakeToken = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmaWN0aWNpbyJ9.assinatura-ficticia";
    private const string FakeDescription = "falha com senha=segredo-ficticio-123 e token " + FakeToken;

    [Fact]
    public async Task ProviderErrorAtCallback_LogsOnlyControlledReason()
    {
        var start = await StartLoginAsync();

        var callback = await CallbackAsync(
            start,
            $"error=invalid_request&error_description={Uri.EscapeDataString(FakeDescription)}&code={FakeCode}");

        var correlationId = AssertRedirectedWithReference(callback);
        AssertLoggedOnly("provedor:invalid_request", correlationId);
    }

    [Fact]
    public async Task TokenEndpointError_LogsOnlyControlledReason()
    {
        Factory.OidcProvider.TokenError = new Dictionary<string, object>
        {
            ["error"] = "invalid_grant",
            ["error_description"] = FakeDescription,
            ["id_token"] = FakeToken,
        };
        var start = await StartLoginAsync();

        var callback = await CallbackAsync(start, $"code={FakeCode}");

        var correlationId = AssertRedirectedWithReference(callback);
        AssertLoggedOnly("provedor:invalid_grant", correlationId);
    }

    [Fact]
    public async Task NonStandardProviderErrorCode_IsNotLoggedVerbatim()
    {
        var start = await StartLoginAsync();

        var callback = await CallbackAsync(start, $"error={Uri.EscapeDataString("erro_" + FakeToken)}&code={FakeCode}");

        var correlationId = AssertRedirectedWithReference(callback);
        AssertLoggedOnly("provedor:erro_nao_padronizado", correlationId);
    }

    private static string AssertRedirectedWithReference(HttpResponseMessage callback)
    {
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var location = callback.Headers.Location!.ToString();
        Assert.StartsWith("/?login=failed&ref=", location);
        Assert.False(HasCookie(callback, SessionCookieName));
        return Uri.UnescapeDataString(location["/?login=failed&ref=".Length..]);
    }

    private void AssertLoggedOnly(string expectedReason, string correlationId)
    {
        var entries = Factory.Logs.Entries.ToList();

        var failure = Assert.Single(entries, e => e.Contains("Login OIDC não concluído"));
        Assert.Contains($"Motivo: {expectedReason}.", failure);
        Assert.Contains($"Correlação: {correlationId}.", failure);

        foreach (var sensitive in new[]
        {
            FakeCode, FakeToken, "segredo-ficticio-123", FakeDescription,
            Saldo360ApiFactory.TestClientSecret, "access-token-de-teste", "error_description",
        })
        {
            Assert.DoesNotContain(entries, e => e.Contains(sensitive, StringComparison.OrdinalIgnoreCase));
        }
    }
}
