namespace Saldo360.Tests.Infrastructure;

[Trait("Category", "INFRA")]
[Trait("Area", "Infrastructure")]
public sealed class ProductionDeploymentContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DocumentaryApiProxyAllowsPreparationAndNeverReplaysAPaidRequest()
    {
        var nginx = File.ReadAllText(Path.Combine(RepositoryRoot, "deploy", "nginx.conf"));
        var api = nginx[nginx.IndexOf("location ^~ /api/", StringComparison.Ordinal)..];
        api = api[..api.IndexOf('}')];
        Assert.Contains("proxy_read_timeout 240s;", api);
        Assert.Contains("proxy_send_timeout 240s;", api);
        Assert.Contains("proxy_next_upstream off;", api);
    }

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
        var collectors = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.collectors.yml"));
        var deploy = File.ReadAllText(Path.Combine(RepositoryRoot, "deploy", "server", "deploy-saldo360"));
        Assert.DoesNotContain("FinancialEmailImport__Sources__", compose, StringComparison.Ordinal);
        Assert.Equal(6, Count(collectors, "FINANCIAL_WORKSPACE_ID:?"));
        Assert.Equal(7, Count(collectors, "__Enabled: \"false\"")); // six profiles + scheduler
        Assert.DoesNotContain("__Enabled: \"true\"", collectors, StringComparison.Ordinal);
        Assert.DoesNotContain("__FamilyId:", collectors, StringComparison.Ordinal);
        Assert.DoesNotContain("FINANCIAL_WORKSPACE_ID:-", collectors, StringComparison.Ordinal);
        Assert.DoesNotContain("__FamilyId:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("FINANCIAL_WORKSPACE_ID:-", compose, StringComparison.Ordinal);
        Assert.Contains("if [ -n \"$workspace_id\" ]; then", deploy, StringComparison.Ordinal);
        Assert.Contains("docker-compose.collectors.yml", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void CutoverPlanIsOptionalForCleanInstallation_NotAnUnconditionalDeployGate()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.production.yml"));
        var deploy = File.ReadAllText(Path.Combine(RepositoryRoot, "deploy", "server", "deploy-saldo360"));
        Assert.DoesNotContain("FINANCIAL_WORKSPACE_CUTOVER_PLAN:?", compose, StringComparison.Ordinal);
        Assert.Contains("FINANCIAL_WORKSPACE_CUTOVER_PLAN: ${FINANCIAL_WORKSPACE_CUTOVER_PLAN:-}", compose, StringComparison.Ordinal);
        Assert.Contains("if [ -n \"$${FINANCIAL_WORKSPACE_CUTOVER_PLAN:-}\" ]; then", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("FINANCIAL_WORKSPACE_CUTOVER_PLAN ausente", deploy, StringComparison.Ordinal);
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
