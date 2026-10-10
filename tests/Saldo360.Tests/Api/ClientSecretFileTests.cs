using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>Segredo do client lido de arquivo (Authentication:Oidc:ClientSecretFile), como em /run/secrets.</summary>
public sealed class ClientSecretFileTests : IDisposable
{
    private const string SecretFromFile = "segredo-vindo-do-arquivo";

    private readonly string _secretPath = Path.Combine(Path.GetTempPath(), $"saldo360-secret-{Guid.NewGuid()}");

    [Fact]
    public async Task SecretFile_TakesPrecedence_AndIsSentToTokenEndpoint()
    {
        // Quebra de linha final, como editores costumam gravar.
        await File.WriteAllTextAsync(_secretPath, SecretFromFile + "\n");
        var test = new SecretFileLogin(_secretPath);
        await test.InitializeAsync();

        try
        {
            var token = await test.LoginAndGetTokenRequestAsync();

            Assert.Equal(SecretFromFile, token.Form["client_secret"]);
            Assert.DoesNotContain(test.Logs, entry => entry.Contains(SecretFromFile));
        }
        finally
        {
            await test.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEmptySecretFile_FailsAtStartup_WithoutLeakingContent(bool createEmptyFile)
    {
        if (createEmptyFile)
        {
            File.WriteAllText(_secretPath, "\n");
        }

        using var factory = new Saldo360ApiFactory(settings: new Dictionary<string, string?>
        {
            ["Authentication:Oidc:ClientSecretFile"] = _secretPath,
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var inner = exception as InvalidOperationException ?? exception.InnerException;
        Assert.Contains("ClientSecretFile", inner!.Message);
    }

    public void Dispose() => File.Delete(_secretPath);

    private sealed class SecretFileLogin(string secretPath) : ApiTestBase(new Dictionary<string, string?>
    {
        ["Authentication:Oidc:ClientSecretFile"] = secretPath,
    })
    {
        public IEnumerable<string> Logs => Factory.Logs.Entries;

        public async Task<ReceivedRequest> LoginAndGetTokenRequestAsync()
        {
            Factory.OidcProvider.IdTokenClaims["sub"] = "usuario-arquivo";
            Factory.OidcProvider.UserInfo["sub"] = "usuario-arquivo";

            var callback = await CallbackAsync(await StartLoginAsync(), "code=code-de-teste");
            Assert.Equal("/", callback.Headers.Location!.ToString());

            return Assert.Single(Factory.OidcProvider.Requests, r => r.Path == "/token");
        }
    }
}
