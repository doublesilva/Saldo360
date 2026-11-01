using System.Text.Json;

namespace Saldo360.Tests.Infrastructure;

/// <summary>
/// O FULL da CI e fatiado (scripts/ci/test-shards.json). Garante que o arquivo e o workflow continuam coerentes: a quantidade de fatias do ci.yml bate com o arquivo, a ultima fatia
/// e a que recolhe o que nao esta listado (nenhum teste novo fica sem fatia) e nenhuma unidade esta em duas fatias.
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Infrastructure")]
[Trait("Profile", "FAST")]
public sealed class CiShardContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void ShardsAreDisjointTheLastOneCollectsUnlistedTestsAndTheWorkflowMatrixMatches()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "scripts", "ci", "test-shards.json")));
        var shards = document.RootElement.GetProperty("shards").EnumerateArray().ToList();
        Assert.True(shards.Count >= 2);
        Assert.True(shards[^1].GetProperty("rest").GetBoolean());
        Assert.All(shards.Take(shards.Count - 1), shard => Assert.False(shard.GetProperty("rest").GetBoolean()));
        var units = shards.SelectMany(shard => shard.GetProperty("include").EnumerateArray().Select(unit => unit.GetString()!)).ToList();
        Assert.All(shards, shard => Assert.NotEmpty(shard.GetProperty("include").EnumerateArray()));
        Assert.Equal(units.Count, units.Distinct(StringComparer.Ordinal).Count());

        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
        var expectedMatrix = "[" + string.Join(",", Enumerable.Range(1, shards.Count)) + "]";
        Assert.Contains($"'{expectedMatrix}'", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/ci/test-shard-filter.py", workflow, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Saldo360.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
