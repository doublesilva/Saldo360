using Saldo360.Application.Categories;
using Saldo360.Application.Classification;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T13.3: uma compra parcelada recebe UMA classificação. A categoria já conhecida da série (manual primeiro) é herdada pelas demais parcelas; sem ela, só um representante vai ao classificador (IA inclusive)
/// e o resultado vale para toda a série. Séries ambíguas e compras paralelas de mesmo nome não se misturam. Série identificada por qualquer parcela observada (compra anterior ao Saldo360).
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggySeriesCategorizationTests
{
    private static readonly Guid Card = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Shopping = Guid.NewGuid();
    private static readonly Guid Home = Guid.NewGuid();

    private sealed class Repo(IReadOnlyList<PluggyCategorizationCandidate> candidates, IReadOnlyList<MappableCategory> categories) : IPluggyCategorizationRepository
    {
        public List<PluggyCategoryDecision> Applied { get; } = [];
        public Task<IReadOnlyList<PluggyCategorizationCandidate>> ListCandidatesAsync(Guid workspaceId, CancellationToken ct) => Task.FromResult(candidates);
        public Task<IReadOnlyList<MappableCategory>> ListCategoriesAsync(Guid workspaceId, CancellationToken ct) => Task.FromResult(categories);
        public Task<PluggyCategorizationApplied> ApplyAsync(Guid workspaceId, Guid userId, IReadOnlyList<PluggyCategoryDecision> decisions, DateTimeOffset now, CancellationToken ct)
        { Applied.AddRange(decisions); return Task.FromResult(new PluggyCategorizationApplied(decisions.Count, 0, 0, 0)); }
        public Task<PluggyCategorizationCounts> CountAsync(Guid workspaceId, CancellationToken ct) => Task.FromResult(new PluggyCategorizationCounts(0, 0, 0, 0, 0, 0, 0));
    }

    private sealed class Classifier(Guid? aiCategory) : IFinancialClassificationService
    {
        public int AiInputs { get; private set; }
        public int AiCalls { get; private set; }
        // Sem regra nem catálogo: tudo precisa de IA.
        public IReadOnlyList<FinancialClassificationOutcome> ClassifyLocal(IReadOnlyList<FinancialClassificationInput> inputs, IReadOnlyCollection<MappableCategory> categories) =>
            inputs.Select(i => new FinancialClassificationOutcome(i.Key, null, null, null, null, false, null, "local")).ToList();
        public Task<FinancialAiClassification> ClassifyWithAiAsync(IReadOnlyList<FinancialClassificationInput> inputs, IReadOnlyCollection<MappableCategory> categories, CancellationToken ct)
        {
            AiCalls++; AiInputs += inputs.Count;
            var outcomes = inputs.Select(i => new FinancialClassificationOutcome(i.Key, aiCategory, aiCategory is null ? null : FinancialClassificationSource.AI, null, null, aiCategory is null, aiCategory is null ? "baixa confiança" : null, "ai:test")).ToList();
            return Task.FromResult(new FinancialAiClassification(outcomes, new FinancialClassificationUsage(1, inputs.Count, 10, 10, 0m, null, null, "test")));
        }
    }

    private sealed class Model : IFinancialClassificationModel
    {
        public bool IsAvailable => true;
        public string ModelName => "test";
        public Task<FinancialModelResponse> ClassifyAsync(FinancialModelRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private static PluggyCategorizationCandidate Installment(string description, int number, decimal amount = 100m, int total = 10, Guid? card = null, Guid? current = null, Guid? posting = null,
        PluggyCategorySource source = PluggyCategorySource.None, int day = 5) =>
        new()
        {
            PostingId = Guid.NewGuid(), Kind = PluggyPostingKind.CardPurchase, Description = $"{description} {number:00}/{total:00}", Amount = amount, Date = new DateOnly(2026, 5, 1).AddMonths(number).AddDays(day),
            CardId = card ?? Card, TotalInstallments = total, CurrentCategoryId = current, PostingCategoryId = posting, Source = source, RecordId = Guid.NewGuid(),
        };

    private static IReadOnlyList<MappableCategory> Categories => [];

    private static async Task<(PluggyCategorizationResult Result, Repo Repo, Classifier Ai)> RunAsync(IReadOnlyList<PluggyCategorizationCandidate> candidates, Guid? aiCategory = null)
    {
        var repo = new Repo(candidates, Categories); var ai = new Classifier(aiCategory);
        var outcome = await new PluggyCategorizationService(repo, ai, new Model()).RunAsync(Guid.NewGuid(), Guid.NewGuid(), new(true, true, false), DateTimeOffset.UtcNow, CancellationToken.None);
        return (outcome.Result!, repo, ai);
    }

    [Fact]
    public async Task ASeriesWithoutACategorySendsOnlyOneRepresentativeToTheAiAndEveryInstallmentGetsTheResult()
    {
        // Compra anterior ao Saldo360: a primeira parcela disponível é a 4/10; as seguintes também estão sem categoria.
        var candidates = Enumerable.Range(4, 3).Select(n => Installment("Geladeira Brastemp", n)).ToList();

        var (result, repo, ai) = await RunAsync(candidates, aiCategory: Home);

        Assert.Equal((1, 1), (ai.AiCalls, ai.AiInputs));                      // UMA solicitação para a série, não três
        Assert.Equal(3, repo.Applied.Count);
        Assert.All(repo.Applied, d => Assert.Equal(Home, d.CategoryId));
        Assert.Equal(candidates.Select(c => c.PostingId).Order(), repo.Applied.Select(d => d.PostingId).Order());
    }

    [Fact]
    public async Task ACategoryAlreadyKnownInOneInstallmentIsInheritedByTheOthersWithoutCallingTheAi()
    {
        var known = Installment("Geladeira Brastemp", 4, current: Shopping, posting: Shopping, source: PluggyCategorySource.AI);
        var later = new[] { Installment("Geladeira Brastemp", 5), Installment("Geladeira Brastemp", 6) };

        var (result, repo, ai) = await RunAsync([known, .. later]);

        Assert.Equal(0, ai.AiCalls);
        Assert.Equal(2, result.Inherited);
        Assert.Equal(later.Select(c => c.PostingId).Order(), repo.Applied.Where(d => d.CategoryId == Shopping).Select(d => d.PostingId).Order());
        Assert.DoesNotContain(repo.Applied, d => d.PostingId == known.PostingId);   // a parcela já classificada não é tocada
    }

    [Fact]
    public async Task AManualCategoryOnOneInstallmentWinsOverAnAutomaticOneAndIsNeverOverwritten()
    {
        var manual = Installment("Sofá Tok", 3, current: Home, posting: null, source: PluggyCategorySource.User);
        var automatic = Installment("Sofá Tok", 4, current: Shopping, posting: Shopping, source: PluggyCategorySource.AI);
        var open = Installment("Sofá Tok", 5);

        var (_, repo, ai) = await RunAsync([manual, automatic, open]);

        Assert.Equal(0, ai.AiCalls);
        Assert.Equal(Home, Assert.Single(repo.Applied, d => d.PostingId == open.PostingId).CategoryId);   // herda a manual
        Assert.DoesNotContain(repo.Applied, d => d.PostingId == manual.PostingId);
    }

    [Fact]
    public async Task ParallelPurchasesWithTheSameNameStayIndependentAndASeriesWithConflictingCategoriesIsNotForced()
    {
        // Mesmo nome, valores diferentes = compras paralelas: cada uma é uma série.
        var a1 = Installment("Mercadolivre", 1, amount: 36.82m, total: 4); var a2 = Installment("Mercadolivre", 2, amount: 36.82m, total: 4);
        var b1 = Installment("Mercadolivre", 1, amount: 157.15m, total: 4, current: Shopping, posting: Shopping, source: PluggyCategorySource.AI); var b2 = Installment("Mercadolivre", 2, amount: 157.15m, total: 4);
        // Série ambígua: duas parcelas com categorias automáticas diferentes.
        var c1 = Installment("Loja Z", 1, total: 3, current: Shopping, posting: Shopping, source: PluggyCategorySource.AI); var c2 = Installment("Loja Z", 2, total: 3, current: Home, posting: Home, source: PluggyCategorySource.AI);
        var c3 = Installment("Loja Z", 3, total: 3);

        var (_, repo, ai) = await RunAsync([a1, a2, b1, b2, c1, c2, c3], aiCategory: Home);

        Assert.Equal(Shopping, repo.Applied.Single(d => d.PostingId == b2.PostingId).CategoryId);      // herdou só da própria série (157,15)
        Assert.Equal((Home, Home), (repo.Applied.Single(d => d.PostingId == a1.PostingId).CategoryId, repo.Applied.Single(d => d.PostingId == a2.PostingId).CategoryId));   // série de 36,82 foi à IA, à parte
        Assert.Equal(Home, repo.Applied.Single(d => d.PostingId == c3.PostingId).CategoryId);          // ambígua: parcela solta segue o fluxo normal (IA), sem herdar
    }

    [Fact]
    public async Task AnUnavailableOrInsecureAiLeavesTheSeriesForReprocessingWithoutInventingACategory()
    {
        var candidates = Enumerable.Range(2, 3).Select(n => Installment("Compra X", n)).ToList();

        var (result, repo, _) = await RunAsync(candidates, aiCategory: null);

        Assert.All(repo.Applied, d => Assert.Null(d.CategoryId));
        Assert.All(repo.Applied, d => Assert.True(d.NeedsReview));
        Assert.Equal(3, result.Unresolved);
    }
}
