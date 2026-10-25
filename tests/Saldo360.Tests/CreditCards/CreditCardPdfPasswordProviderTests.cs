using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
public sealed class CreditCardPdfPasswordProviderTests
{
    [Fact]
    public async Task UsesConfiguredIssuerStrategyAndAtMostTwoPrefixes()
    {
        var holder = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CreditCardPdfPasswordMock:Enabled"] = "true",
            [$"CreditCardPdfPasswordMock:Identities:{holder:D}:First5"] = "12345",
            ["CreditCardPdfPasswordMock:IssuerStrategies:Banco Teste"] = "First3",
        }).Build();
        var provider = new LocalCreditCardPdfPasswordProvider(configuration, new TestEnvironment("Testing"));

        var result = await provider.ResolveAsync(holder, "Banco Teste", CancellationToken.None);

        Assert.Equal(["123", "12345"], result.Candidates);
    }

    [Fact]
    public async Task IsUnavailableInProductionEvenWhenConfigured()
    {
        var holder = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CreditCardPdfPasswordMock:Enabled"] = "true",
            [$"CreditCardPdfPasswordMock:Identities:{holder:D}:First5"] = "12345",
        }).Build();
        var provider = new LocalCreditCardPdfPasswordProvider(configuration, new TestEnvironment("Production"));

        var result = await provider.ResolveAsync(holder, "Banco Teste", CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Saldo360.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
