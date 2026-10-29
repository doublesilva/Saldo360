using Saldo360.Application.Classification;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Classification;

/// <summary>
/// T09.3.1: serviço único de classificação financeira. Puro e sem rede: o fallback de IA é um substituto que registra o que seria enviado.
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class FinancialClassificationTests
{
    private static readonly DateOnly Day = new(2026, 10, 5);
    private static readonly MappableCategory Food = new(Guid.NewGuid(), "Alimentação", CategoryType.Expense, true);
    private static readonly MappableCategory Leisure = new(Guid.NewGuid(), "Lazer", CategoryType.Expense, true);
    private static readonly MappableCategory Salary = new(Guid.NewGuid(), "Salário", CategoryType.Income, true);
    private static readonly MappableCategory OtherIncome = new(Guid.NewGuid(), "Outras receitas", CategoryType.Income, true);

    private static FinancialClassificationInput Input(string key, string description = "MERCADO CENTRAL", FinancialMovementKind kind = FinancialMovementKind.Expense,
        string? external = null, string? operation = "CARTAO", Guid? manual = null, bool card = false) =>
        new(key, description, 10m, Day, kind, external, operation, "Pluggy", manual, card);

    private static (FinancialClassificationService Service, FakeFinancialClassificationModel Model) Create(Action<FinancialClassificationOptions>? configure = null, bool available = true)
    {
        var model = new FakeFinancialClassificationModel { Available = available };
        var options = new FinancialClassificationOptions { AiEnabled = true, RetryDelayMilliseconds = 0 };
        configure?.Invoke(options);
        return (new FinancialClassificationService(model, options), model);
    }

    // ---- 1. Manual e regras ----------------------------------------------------------------------------------------------

    [Fact]
    public void ManualCategoryIsPreservedAndNeverReachesTheModelOrTheCatalog()
    {
        var (service, model) = Create();
        var manual = Guid.NewGuid();

        var outcome = service.ClassifyLocal([Input("a", external: "Groceries", manual: manual)], [Food]).Single();

        Assert.Equal((manual, FinancialClassificationSource.Manual), (outcome.CategoryId, outcome.Source));
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData("Credit card payment", "Pagamento de fatura")]
    [InlineData("Same person transfer", "Transferência entre contas do titular")]
    [InlineData("Transfer - Internal", "Transferência entre contas do titular")]
    public void NonConsumptionMovementsAreDecidedByRulesAndNeverCategorized(string external, string reasonStart)
    {
        var (service, _) = Create();

        var outcome = service.ClassifyLocal([Input("a", external: external)], [Food, Leisure]).Single();

        Assert.Equal(FinancialClassificationSource.Rule, outcome.Source);
        Assert.Null(outcome.CategoryId);
        Assert.Null(outcome.ProposedName);
        Assert.False(outcome.NeedsReview);
        Assert.StartsWith(reasonStart, outcome.Reason);
    }

    [Fact]
    public void ThirdPartyTransfersRefundsAndMislabeledCardPurchasesGoToManualReviewWithoutCategory()
    {
        var (service, _) = Create();

        var outcomes = service.ClassifyLocal([
            Input("pix", external: "Transfer - PIX", operation: "PIX"),
            Input("refund", external: "Shopping", operation: "ESTORNO"),
            Input("card-payment-as-purchase", external: "Credit card payment", card: true),
        ], [Food]);

        Assert.All(outcomes, o => { Assert.Equal(FinancialClassificationSource.Rule, o.Source); Assert.Null(o.CategoryId); Assert.True(o.NeedsReview); });
    }

    // ---- 2. Catálogo Pluggy ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Groceries", "Alimentacao ")]
    [InlineData("groceries", "ALIMENTAÇÃO")]
    [InlineData("Supermarkets", "  alimentação  ")]
    public void PluggyCategoryReusesAnExistingCategoryDespiteCaseAccentsAndSpacing(string external, string existingName)
    {
        var (service, _) = Create();
        var existing = new MappableCategory(Guid.NewGuid(), existingName, CategoryType.Expense, true);

        var outcome = service.ClassifyLocal([Input("a", external: external)], [existing]).Single();

        Assert.Equal((existing.Id, FinancialClassificationSource.Pluggy, (string?)null), (outcome.CategoryId, outcome.Source, outcome.ProposedName));
    }

    [Fact]
    public void MissingCategoriesAreProposedOnlyForUnambiguousMappingsAndNeverDuplicatedByAccentOrInactivity()
    {
        var (service, _) = Create();
        var inactive = new MappableCategory(Guid.NewGuid(), "Alimentacao", CategoryType.Expense, false);

        var proposed = service.ClassifyLocal([Input("a", external: "Pharmacy"), Input("b", external: "Algo Totalmente Desconhecido")], [Food]);
        var withInactive = service.ClassifyLocal([Input("c", external: "Groceries")], [inactive]).Single();

        Assert.Equal(("Farmácia", CategoryType.Expense), (proposed[0].ProposedName, proposed[0].ProposedType));
        Assert.Null(proposed[1].ProposedName); // desconhecida: nada é inventado
        Assert.True(proposed[1].NeedsAi);
        Assert.Null(withInactive.ProposedName); // existe equivalente (inativa): não cria duplicada
        Assert.Null(withInactive.CategoryId); // e também não usa a inativa
    }

    [Fact]
    public void CategoryTypeIsRespected()
    {
        var (service, _) = Create();

        var income = service.ClassifyLocal([Input("a", kind: FinancialMovementKind.Income, external: "Proceeds interests and dividends"),
            Input("b", kind: FinancialMovementKind.Income, external: "Groceries")], [Food, Salary]);

        Assert.Equal(("Rendimentos", CategoryType.Income), (income[0].ProposedName, income[0].ProposedType));
        Assert.True(income[1].NeedsAi); // "Groceries" não existe como receita e a categoria de despesa nunca é usada
        Assert.Null(income[1].CategoryId);
    }

    // ---- 3. Sanitização ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("COMPRA 123.456.789-00 LOJA XYZ", "123.456.789-00")]
    [InlineData("LOJA 12.345.678/0001-90 CENTRO", "12.345.678/0001-90")]
    [InlineData("PAGAMENTO fulano@exemplo.com MERCADO", "fulano@exemplo.com")]
    [InlineData("MERCADO CENTRAL ****1234 SAO PAULO", "1234")]
    [InlineData("FARMACIA 11999998888 CENTRO", "11999998888")]
    [InlineData("LOJA 8f4b7c2e-1a3d-4e5f-9a6b-0c1d2e3f4a5b MATRIZ", "8f4b7c2e")]
    public void SensitiveIdentifiersAreRemovedFromTheTextSentToTheModel(string description, string forbidden)
    {
        var ok = FinancialDescriptionSanitizer.TrySanitize(description, "CARTAO", "Shopping", isCardPurchase: true, out var sanitized);

        Assert.True(ok);
        Assert.DoesNotContain(forbidden, sanitized);
        Assert.False(sanitized.Any(char.IsDigit) && sanitized.Contains("123456"));
    }

    [Theory]
    [InlineData("PIX ENVIADO JOAO DA SILVA", "PIX", "Transfer - PIX")]
    [InlineData("TED MARIA SOUZA", "TED", "Transfer - TED")]
    [InlineData("JOAO CARLOS PEREIRA", "OUTROS", null)]
    [InlineData("12345678", "OUTROS", null)]
    public void DescriptionsThatCannotBeAnonymizedAreNeverSent(string description, string operation, string? external)
    {
        Assert.False(FinancialDescriptionSanitizer.TrySanitize(description, operation, external, isCardPurchase: false, out var sanitized));
        Assert.Empty(sanitized);
    }

    // ---- 4. Fallback OpenAI ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyUnresolvedItemsAreSentAndTheRequestCarriesNoIdsAmountsDatesOrPersonalData()
    {
        var (service, model) = Create();
        model.Responder = FakeFinancialClassificationModel.Always("Lazer");
        var input = Input("secret-internal-key", description: "CINEMA CENTER 123.456.789-00 contato@x.com", card: true);

        var result = await service.ClassifyWithAiAsync([input], [Food, Leisure], default);

        var request = Assert.Single(model.Requests);
        var text = string.Join('|', request.Items.Select(i => i.Text)) + string.Join('|', request.Categories.Select(c => c.Name));
        Assert.DoesNotContain("123.456.789-00", text);
        Assert.DoesNotContain("contato@x.com", text);
        Assert.DoesNotContain("secret-internal-key", text);
        Assert.All(request.Items, i => Assert.Equal(0, i.Index)); // índice temporário, não identificador interno
        Assert.Equal((Leisure.Id, FinancialClassificationSource.AI), (result.Outcomes.Single().CategoryId, result.Outcomes.Single().Source));
        Assert.Equal((1, 1, 100, 10), (result.Usage.Calls, result.Usage.Items, result.Usage.InputTokens, result.Usage.OutputTokens));
        Assert.True(result.Usage.EstimatedCostUsd > 0m);
    }

    [Fact]
    public async Task InvalidModelAnswersAreRejectedAndLeftForReview()
    {
        var (service, model) = Create();
        var inactive = new MappableCategory(Guid.NewGuid(), "Antiga", CategoryType.Expense, false);
        model.Responder = request => new FinancialModelResponse([
            new(0, 999, 0.99, false, "código inexistente"),
            new(1, request.Categories.First(c => c.Type == CategoryType.Income).Code, 0.99, false, "tipo errado"),
            new(2, request.Categories.First(c => c.Name == "Lazer").Code, 0.40, false, "pouca confiança"),
            new(3, request.Categories.First(c => c.Name == "Lazer").Code, 0.99, true, "ambígua"),
            new(4, null, 0.99, false, "sem categoria"),
            // índice 5 sem resposta
        ], 10, 5);
        var inputs = Enumerable.Range(0, 6).Select(i => Input($"k{i}", description: $"LOJA QUALQUER {(char)('A' + i)}")).ToList();

        var result = await service.ClassifyWithAiAsync(inputs, [Leisure, Salary, inactive], default);

        Assert.All(result.Outcomes, o => { Assert.Null(o.CategoryId); Assert.True(o.NeedsReview); Assert.Null(o.Source); });
        Assert.Equal(6, result.Outcomes.Count);
    }

    [Fact]
    public async Task AModelCannotCreateCategoriesOrUseInactiveOnes()
    {
        var (service, model) = Create();
        var inactive = new MappableCategory(Guid.NewGuid(), "Antiga", CategoryType.Expense, false);
        model.Responder = FakeFinancialClassificationModel.Always("Antiga");

        var result = await service.ClassifyWithAiAsync([Input("a")], [Leisure, inactive], default);

        Assert.DoesNotContain(model.Requests.Single().Categories, c => c.Name == "Antiga"); // inativa nem entra no menu
        Assert.Null(result.Outcomes.Single().CategoryId);
    }

    [Theory]
    [InlineData("ProviderAuthenticationFailed", false, 1)]
    [InlineData("ProviderRateLimited", true, 3)]
    [InlineData("ProviderTimeout", true, 3)]
    public async Task ProviderFailuresAreControlledAndRetriesAreBounded(string code, bool retryable, int expectedCalls)
    {
        var (service, model) = Create(o => o.MaxRetries = 2);
        model.Failure = new FinancialClassificationModelException(code, retryable);

        var result = await service.ClassifyWithAiAsync([Input("a"), Input("b", description: "OUTRA LOJA")], [Leisure], default);

        Assert.Equal(expectedCalls, model.Calls);
        Assert.All(result.Outcomes, o => { Assert.Equal(FinancialClassificationService.SkippedMethod, o.Method); Assert.Null(o.CategoryId); }); // não tentado: elegível depois
        Assert.Equal((code, "ProviderFailure"), (result.Usage.FailureCode, result.Usage.StoppedBecause));
    }

    [Fact]
    public async Task ARateLimitThatClearsOnRetryStillClassifies()
    {
        var (service, model) = Create(o => o.MaxRetries = 2);
        var attempts = 0;
        var inner = FakeFinancialClassificationModel.Always("Lazer");
        model.Responder = request => ++attempts < 2 ? throw new FinancialClassificationModelException("ProviderRateLimited", true) : inner(request);

        var result = await service.ClassifyWithAiAsync([Input("a")], [Leisure], default);

        Assert.Equal(2, model.Calls);
        Assert.Equal(Leisure.Id, result.Outcomes.Single().CategoryId);
    }

    [Fact]
    public async Task MissingCredentialOrDisabledAiMeansLocalOnlyWithoutAnyCall()
    {
        var (service, model) = Create(available: false);

        var result = await service.ClassifyWithAiAsync([Input("a")], [Leisure], default);

        Assert.Equal(0, model.Calls);
        Assert.Equal("AiUnavailable", result.Usage.StoppedBecause);
        Assert.Equal(FinancialClassificationService.SkippedMethod, result.Outcomes.Single().Method);
    }

    [Fact]
    public async Task RunLimitsOnItemsAndCostStopTheModelAndLeaveTheRestEligible()
    {
        var (service, model) = Create(o => { o.MaxItemsPerRun = 3; o.BatchSize = 2; });
        model.Responder = FakeFinancialClassificationModel.Always("Lazer");
        var inputs = Enumerable.Range(0, 5).Select(i => Input($"k{i}", description: $"CINEMA {(char)('A' + i)}", card: true)).ToList();

        var byItems = await service.ClassifyWithAiAsync(inputs, [Leisure], default);

        Assert.Equal(3, byItems.Outcomes.Count(o => o.CategoryId == Leisure.Id));
        Assert.Equal(2, byItems.Outcomes.Count(o => o.Method == FinancialClassificationService.SkippedMethod));
        Assert.Equal("MaxItemsPerRun", byItems.Usage.StoppedBecause);

        var (cheap, cheapModel) = Create(o => o.MaxEstimatedCostUsdPerRun = 0.0000001m);
        cheapModel.Responder = FakeFinancialClassificationModel.Always("Lazer");
        var byCost = await cheap.ClassifyWithAiAsync(inputs, [Leisure], default);
        Assert.Equal(0, cheapModel.Calls);
        Assert.Equal("MaxCostPerRun", byCost.Usage.StoppedBecause);
    }

    [Fact]
    public async Task EntriesWithoutAnyEligibleCategoryOfTheirTypeNeverReachTheModel()
    {
        var (service, model) = Create();

        var result = await service.ClassifyWithAiAsync([Input("a", kind: FinancialMovementKind.Income, description: "RECEBIMENTO LOJA X")], [Leisure], default);

        Assert.Equal(0, model.Calls);
        Assert.True(result.Outcomes.Single().NeedsReview);
    }

    // ---- 5. Sugestão de categoria nova (consultor financeiro) -------------------------------------------------------------

    [Fact]
    public async Task WhenNoExistingCategoryFitsTheModelSuggestsANewOneAndTheBackendProposesItOnce()
    {
        var (service, model) = Create();
        model.Responder = FakeFinancialClassificationModel.Suggest("Entretenimento");
        var inputs = new[] { Input("a", description: "CINEMA CENTRAL", card: true), Input("b", description: "TEATRO MUNICIPAL", card: true) };

        var result = await service.ClassifyWithAiAsync(inputs, [Food], default);

        Assert.All(result.Outcomes, o => Assert.Equal(("Entretenimento", CategoryType.Expense, FinancialClassificationSource.AI, (Guid?)null), (o.ProposedName, o.ProposedType, o.Source, o.CategoryId)));
        Assert.Contains("consultor financeiro", OpenAiFinancialClassificationModel.SystemPrompt);
    }

    [Fact]
    public async Task ASuggestionEquivalentToAnExistingCategoryReusesItAndOneEquivalentToAnInactiveOneIsNeverRecreated()
    {
        var (service, model) = Create();
        var existing = new MappableCategory(Guid.NewGuid(), "Entretenimento", CategoryType.Expense, true);
        model.Responder = FakeFinancialClassificationModel.Suggest("  entretenimento ");

        var reused = await service.ClassifyWithAiAsync([Input("a", card: true)], [existing], default);
        var inactive = new MappableCategory(Guid.NewGuid(), "Entretenimento", CategoryType.Expense, false);
        var blocked = await service.ClassifyWithAiAsync([Input("b", card: true)], [inactive, Leisure], default);

        Assert.Equal((existing.Id, (string?)null), (reused.Outcomes.Single().CategoryId, reused.Outcomes.Single().ProposedName));
        Assert.Null(blocked.Outcomes.Single().ProposedName);
        Assert.Null(blocked.Outcomes.Single().CategoryId);
        Assert.True(blocked.Outcomes.Single().NeedsReview);
    }

    [Theory]
    [InlineData("Outros")]
    [InlineData("Diversos")]
    [InlineData("Loja 123")]
    [InlineData("joao@exemplo.com")]
    [InlineData("Categoria muito longa que nao deveria ser aceita como nome")]
    [InlineData("A")]
    [InlineData("")]
    public async Task UnsafeOrGenericSuggestedNamesAreRejected(string suggestion)
    {
        var (service, model) = Create();
        model.Responder = FakeFinancialClassificationModel.Suggest(suggestion);

        var result = await service.ClassifyWithAiAsync([Input("a", card: true)], [Leisure], default);

        Assert.Null(result.Outcomes.Single().ProposedName);
        Assert.True(result.Outcomes.Single().NeedsReview);
    }

    [Fact]
    public async Task SuggestionsRespectTheConfidenceTheModelReviewFlagTheOptionAndTheNewCategoryLimit()
    {
        var (service, model) = Create(o => o.MaxNewCategoriesPerRun = 1);
        model.Responder = request => new FinancialModelResponse([
            new(0, null, 0.95, false, "ok", "Viagens"),
            new(1, null, 0.40, false, "baixa confiança", "Esportes"),
            new(2, null, 0.95, true, "incerto", "Esportes"),
            new(3, null, 0.95, false, "outra", "Hobbies"),
            new(4, null, 0.95, false, "mesma", "viagens"),
        ], 10, 5);
        var inputs = Enumerable.Range(0, 5).Select(i => Input($"k{i}", description: $"LOJA QUALQUER {(char)('A' + i)}")).ToList();

        var result = await service.ClassifyWithAiAsync(inputs, [Leisure], default);

        var byKey = result.Outcomes.ToDictionary(o => o.Key);
        Assert.Equal("Viagens", byKey["k0"].ProposedName);
        Assert.Null(byKey["k1"].ProposedName); // confiança baixa
        Assert.Null(byKey["k2"].ProposedName); // o próprio modelo pediu revisão
        Assert.Null(byKey["k3"].ProposedName); // passou do limite de 1 categoria nova por execução
        Assert.Equal("Viagens", byKey["k4"].ProposedName); // mesmo nome (equivalente) não conta como outra categoria

        var (disabled, disabledModel) = Create(o => o.AllowCategorySuggestions = false);
        disabledModel.Responder = FakeFinancialClassificationModel.Suggest("Viagens");
        Assert.Null((await disabled.ClassifyWithAiAsync([Input("a", card: true)], [Leisure], default)).Outcomes.Single().ProposedName);
    }
}
