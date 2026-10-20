namespace Saldo360.Tests.Infrastructure;

public sealed class ProductionDeploymentContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProductionComposeRequiresImmutableImageTag()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.production.yml"));

        Assert.DoesNotContain("SALDO360_IMAGE_TAG:-local", compose, StringComparison.Ordinal);
        Assert.Equal(3, Count(compose, "SALDO360_IMAGE_TAG:?"));
    }

    [Fact]
    public void CollectorDestinationRequiresExplicitWorkspaceWithoutFamilyFallback()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.production.yml"));
        Assert.Equal(6, Count(compose, "FINANCIAL_WORKSPACE_ID:?"));
        Assert.DoesNotContain("__FamilyId:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("FINANCIAL_WORKSPACE_ID:-", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateNeverReceivesActiveApiAlias()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "deploy", "server", "deploy-saldo360"));

        Assert.DoesNotContain("--use-aliases", script, StringComparison.Ordinal);
        Assert.Contains("grep -Fxq api", script, StringComparison.Ordinal);
        Assert.Contains("alias produtivo api", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceBillAutomationRemainsDisabledByDefault()
    {
        var environment = File.ReadAllText(Path.Combine(RepositoryRoot, ".env.production.example"));

        Assert.Contains("FINANCIAL_DOCUMENT_CLASSIFICATION_ENABLED=false", environment, StringComparison.Ordinal);
        Assert.Contains("SERVICE_BILL_EXTRACTION_ENABLED=false", environment, StringComparison.Ordinal);
        Assert.Contains("SERVICE_BILL_RECONCILIATION_ENABLED=false", environment, StringComparison.Ordinal);
        Assert.Contains("SERVICE_BILL_CONFIRMATION_ENABLED=false", environment, StringComparison.Ordinal);
        Assert.Contains("SERVICE_BILL_BINDING_ENABLED=false", environment, StringComparison.Ordinal);
    }

    private static int Count(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Saldo360.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
