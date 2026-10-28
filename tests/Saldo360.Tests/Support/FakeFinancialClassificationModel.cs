using Saldo360.Application.Classification;

namespace Saldo360.Tests.Support;

/// <summary>
/// Substituto do fallback de IA nos testes: nenhuma chamada de rede. Registra cada requisição (para provar o que seria enviado) e responde com um
/// delegate configurável. Por padrão não está "disponível"; testes que exercitam a IA ligam <see cref="Available"/>.
/// </summary>
public sealed class FakeFinancialClassificationModel : IFinancialClassificationModel
{
    private readonly List<FinancialModelRequest> requests = [];

    public bool Available { get; set; }
    public string Name { get; set; } = "fake-model";
    public Func<FinancialModelRequest, FinancialModelResponse>? Responder { get; set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public IReadOnlyList<FinancialModelRequest> Requests { get { lock (requests) return [.. requests]; } }

    public bool IsAvailable => Available;
    public string ModelName => Name;

    public Task<FinancialModelResponse> ClassifyAsync(FinancialModelRequest request, CancellationToken cancellationToken)
    {
        lock (requests) { requests.Add(request); Calls++; }
        if (Failure is not null) throw Failure;
        return Task.FromResult(Responder?.Invoke(request) ?? new FinancialModelResponse([], 0, 0));
    }

    /// <summary>Responde a todos os itens com o código da primeira categoria do tipo do item cujo nome contém <paramref name="nameContains"/>.</summary>
    public static Func<FinancialModelRequest, FinancialModelResponse> Always(string nameContains, double confidence = 0.95, bool needsReview = false) => request =>
    {
        var decisions = request.Items.Select(item =>
        {
            var category = request.Categories.FirstOrDefault(c => c.Type.ToString() == item.Kind.ToString() && c.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
            return new FinancialModelDecision(item.Index, category?.Code, confidence, needsReview, "Teste");
        }).ToList();
        return new FinancialModelResponse(decisions, 100 * request.Items.Count, 10 * request.Items.Count);
    };
}
