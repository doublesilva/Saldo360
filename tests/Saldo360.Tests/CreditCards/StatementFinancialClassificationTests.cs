using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
[Trait("Profile", "FAST")]
public sealed class StatementFinancialClassificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntegralRequestHasOneProviderCallAndNoRetryOnInvalidResponse(bool invalid)
    {
        var facts = Reference(); var provider = new Provider(invalid ? "{}" : JsonSerializer.Serialize(facts, CreditCardStatementAgentSchema.Json));
        var schema = StatementFinancialClassifier.Schema(OpenAiCreditCardStatementAgentProvider.PurchasesOnlySchema);
        if (invalid)
            Assert.Equal("FinancialClassificationSchemaInvalid", (await Assert.ThrowsAsync<CreditCardStatementAgentUnavailableException>(
                () => StatementFinancialClassifier.ExtractAsync(provider, schema, Source(facts), default))).FailureCode);
        else
            Assert.Equal("6806.73", (await StatementFinancialClassifier.ExtractAsync(provider, schema, Source(facts), default)).CurrentInvoice.ComponentsTotal);
        Assert.Equal(1, provider.Calls); Assert.False(provider.Request!.PurchasesOnly);
        Assert.Equal("credit_card_statement_integral", provider.Request.SchemaName);
        Assert.NotNull(provider.Request.OutputSchema); Assert.Single(provider.Request.ImagePages);
        Assert.Contains("contratação", provider.Request.SystemPrompt);
    }
    [Fact]
    public void FinancialGroupsReconcileWithoutAddingSummariesOffersOrPrincipalTwice()
    {
        var facts = Reference();
        var result = StatementFinancialClassifier.Evaluate(facts, Source(facts));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new("Reconciled", "6806.73", "6806.73", "0.00"), result.CurrentInvoice);
        Assert.Equal(new("Reconciled", "4286.25", "4286.25", "0.00"), result.NextInvoice);
        Assert.Equal(41, facts.Purchases.Items.Count(i => i.IncludedInCurrentTotal));
        Assert.Equal(10, facts.Purchases.Items.Count(i => i.Section == "FutureInstallments"));
        Assert.Equal(4, facts.Components.Count(c => c.Scope == "NextInvoice"));
        Assert.True(result.ReadOnly); Assert.False(result.CoverageProven);
        Assert.All(facts.Purchases.Items, i => Assert.Null(i.TransactionDate));
        Assert.Equal("9273.59", Assert.Single(facts.Components, c => c.Kind == "RemainingFutureTotal").Amount);
    }

    [Fact]
    public void MissingComponentsRemainAnExplicitDifferenceNeverAnInventedAdjustment()
    {
        var facts = Reference();
        facts = facts with { Components = facts.Components.Where(c => c.Kind != "Tax").ToArray() };
        var result = StatementFinancialClassifier.Evaluate(facts, Source(facts));
        Assert.Equal("Divergent", result.CurrentInvoice.Status);
        Assert.Equal("3.00", result.CurrentInvoice.Difference);
        Assert.DoesNotContain(result.Document.Components, c => c.Description.Contains("ajuste", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Rate", "CurrentInvoice", "BRL", "Debit", "Juros cobrados")]
    [InlineData("PrincipalDetail", "CurrentInvoice", "BRL", "Debit", "Produtos e serviços")]
    [InlineData("CurrentTotal", "CurrentInvoice", "BRL", "Debit", "Total da fatura")]
    [InlineData("Offer", "CurrentInvoice", "BRL", "Debit", "Oferta de parcelamento")]
    [InlineData("FinancialOperation", "NextInvoice", "BRL", "Debit", "Simulação de próximas faturas")]
    [InlineData("FinancialOperation", "NextInvoice", "BRL", "Debit", "Produtos e serviços")]
    [InlineData("Tax", "CurrentInvoice", "Percent", "Debit", "Encargos cobrados")]
    [InlineData("Refund", "CurrentInvoice", "BRL", "Debit", "Lançamentos atuais")]
    [InlineData("Subtotal", "Information", "BRL", "Debit", "Resumo")]
    public void InvalidRolesCannotClaimReconciliation(string kind, string scope, string unit, string nature, string context)
    {
        var facts = Reference(); var c = Component("TESTE01/02", "1.00", kind, scope, nature, unit, 1, 2, context);
        facts = facts with { Components = facts.Components.Append(c).ToArray() };
        var result = StatementFinancialClassifier.Evaluate(facts, Source(facts));
        Assert.Contains(result.Diagnostics, d => d.Code == "FinancialRoleEvidenceInvalid");
        Assert.Equal("Unproven", result.CurrentInvoice.Status);
    }

    [Fact]
    public void UnknownAndLowConfidenceAreNotSilentlyPromoted()
    {
        var facts = Reference(); facts = facts with { Components = facts.Components.Append(Component("AMBIGUO", "1.00", "Unknown", "Information") with { Confidence = "Low" }).ToArray() };
        Assert.Contains(StatementFinancialClassifier.Evaluate(facts, Source(facts)).Diagnostics, d => d.Code == "FinancialClassificationUncertain");
    }

    [Fact]
    public void MissingQuoteWrongAmountAndConflictingReferencesFailClosed()
    {
        var facts = Reference(); var source = Source(facts);
        var bad = facts.Components.Select(c => c.Kind == "Tax" ? c with { Evidence = "INVENTADO 9.99" } : c).ToArray();
        var result = StatementFinancialClassifier.Evaluate(facts with { Components = bad }, source);
        Assert.Contains(result.Diagnostics, d => d.Code == "EvidenceQuoteNotFound");
        Assert.Contains(result.Diagnostics, d => d.Code == "EvidenceAmountNotFound");
        var conflict = Component("Próxima fatura conflitante", "1.00", "NextInvoiceTotal", "Control");
        var conflicting = facts with { Components = facts.Components.Append(conflict).ToArray() };
        result = StatementFinancialClassifier.Evaluate(conflicting, Source(conflicting));
        Assert.Contains(result.Diagnostics, d => d.Code == "FinancialNextTotalConflict");
        Assert.Null(result.NextInvoice.OfficialTotal);
    }

    [Fact]
    public void MissingNextReferenceDoesNotReconstructRemainingInvoices()
    {
        var facts = Reference(); facts = facts with { Components = facts.Components.Where(c => c.Kind != "NextInvoiceTotal").ToArray() };
        var result = StatementFinancialClassifier.Evaluate(facts, Source(facts));
        Assert.Equal("ReferenceAbsent", result.NextInvoice.Status); Assert.Null(result.NextInvoice.Difference);
        Assert.Equal("4286.25", result.NextInvoice.ComponentsTotal);
    }

    [Fact]
    public void ExistingObligationsAndExtractionDuplicatesAreCandidatesNeverDeleted()
    {
        var facts = Reference(); var current = facts.Purchases.Items[1];
        facts = facts with { Purchases = facts.Purchases with { Items = facts.Purchases.Items.Append(current).Append(current with { Section = "FutureInstallments", IncludedInCurrentTotal = false, Evidence = current.Evidence + " FUTURO" }).ToArray() } };
        var duplicates = StatementFinancialClassifier.FindDuplicates(facts, [new(current.OriginalDescription, current.Amount, current.InstallmentNumber!.Value, current.InstallmentCount!.Value)]);
        Assert.Contains(duplicates, d => d.Code == "RepeatedEvidence");
        Assert.Contains(duplicates, d => d.Code == "CurrentFutureOverlap");
        Assert.Contains(duplicates, d => d.Code == "ExistingObligationCandidate");
        Assert.Equal(53, facts.Purchases.Items.Count);
    }

    [Fact]
    public void ConsecutiveFutureInstallmentIsNotADuplicateOfCurrentNumber()
    {
        var facts = Reference();
        Assert.DoesNotContain(StatementFinancialClassifier.FindDuplicates(facts, []), d => d.Code == "CurrentFutureOverlap");
    }

    [Fact]
    public void ContractIsStrictAndLegacyPurchasesRemainCompatible()
    {
        var original = Reference(); var json = JsonSerializer.Serialize(original, CreditCardStatementAgentSchema.Json);
        Assert.True(StatementFinancialClassifier.TryRead(json, 1, out var parsed));
        Assert.Equal(JsonSerializer.Serialize(original, CreditCardStatementAgentSchema.Json), JsonSerializer.Serialize(parsed, CreditCardStatementAgentSchema.Json));
        Assert.True(CreditCardStatementAgentSchema.TryValidate(JsonSerializer.Serialize(parsed!.Purchases, CreditCardStatementAgentSchema.Json), 1, out _, out _, out _, true));
        var node = JsonNode.Parse(json)!; node["components"]![0]!["unexpected"] = "ignored?";
        Assert.False(StatementFinancialClassifier.TryRead(node.ToJsonString(), 1, out _));
        node = JsonNode.Parse(json)!; node["components"]![0]!.AsObject().Remove("installmentCount");
        Assert.False(StatementFinancialClassifier.TryRead(node.ToJsonString(), 1, out _));
        Assert.False(new StatementDocumentaryImportOptions().IntegralClassificationEnabled);
        Assert.DoesNotContain("IntegralClassificationEnabled", JsonSerializer.Serialize(new StatementDocumentaryImportOptions()));
    }

    [Theory]
    [InlineData("1.500")]
    [InlineData("-1.00")]
    [InlineData("10000000000000.00")]
    [InlineData("NaN")]
    public void InvalidMoneyIsRejectedNotRounded(string amount)
    {
        var node = JsonSerializer.SerializeToNode(Reference(), CreditCardStatementAgentSchema.Json)!;
        node["components"]![0]!["amount"] = amount;
        Assert.False(StatementFinancialClassifier.TryRead(node.ToJsonString(), 1, out _));
    }

    [Fact]
    public void InformationalRatesKeepDocumentaryPrecisionWithoutBeingRoundedToMoney()
    {
        var facts = Reference();
        var rate = Component("ALIQUOTA", "0.00820", "Rate", "Information", unit: "Percent") with { Evidence = "ALIQUOTA 0,00820%" };
        facts = facts with { Components = facts.Components.Append(rate).ToArray() };
        Assert.True(StatementFinancialClassifier.TryRead(JsonSerializer.Serialize(facts, CreditCardStatementAgentSchema.Json), 1, out var parsed));
        var result = StatementFinancialClassifier.Evaluate(parsed!, Source(facts));
        Assert.Empty(result.Diagnostics); Assert.Equal("6806.73", result.CurrentInvoice.ComponentsTotal);
        Assert.Equal("0.00820", result.Document.Components[^1].Amount);
    }

    [Fact]
    public void UnrepresentedMonetaryOccurrenceIsReportedEvenWhenCurrentArithmeticAgrees()
    {
        var facts = Reference(); var source = Source(facts);
        source = new([source.Pages[0] with { Text = source.Pages[0].Text + "\nJUROS NÃO CLASSIFICADOS 7,23" }]);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Contains(result.Diagnostics, d => d.Code == "FinancialAmountsUnclassified");
        Assert.Equal(1, Assert.Single(result.Pages).UnclassifiedAmounts);
        Assert.Equal("0.00", result.CurrentInvoice.Difference); Assert.Equal("Unproven", result.CurrentInvoice.Status);
    }

    [Fact]
    public void StrictSchemaDoesNotChangeSchemaTwoAndEveryObjectRejectsExtraProperties()
    {
        var old = OpenAiCreditCardStatementAgentProvider.PurchasesOnlySchema.GetRawText();
        var schema = StatementFinancialClassifier.Schema(OpenAiCreditCardStatementAgentProvider.PurchasesOnlySchema);
        Check(schema); Assert.Equal(old, OpenAiCreditCardStatementAgentProvider.PurchasesOnlySchema.GetRawText());
        void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
                { Assert.False(value.GetProperty("additionalProperties").GetBoolean()); Assert.Equal(value.GetProperty("properties").EnumerateObject().Count(), value.GetProperty("required").GetArrayLength()); }
                foreach (var p in value.EnumerateObject()) Check(p.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var p in value.EnumerateArray()) Check(p);
        }
    }

    [Fact]
    public void FinancialReadOnlyCannotBecomeAnOfficialWriteEvenIfCallerRemovesBlock()
    {
        var facts = Reference(); var report = StatementFinancialClassifier.Evaluate(facts, Source(facts));
        var preview = new CreditCardStatementAgentPreview("synthetic.pdf", 1, facts.Purchases.Header, [], new("Reconciled", "6806.73", "6806.73", "0.00", false));
        var extraction = new CreditCardStatementExtractionResult("AgentPrimary", "Agent", preview, AgentExtractionAssessment.Reliable, false, null, null, null, null) { FinancialClassification = report };
        var card = Guid.NewGuid();
        var analysis = new InboxStatementAnalysis("2.0", card, "hash", "Reliable", extraction, [], new Dictionary<string, Guid>());
        Assert.False(OfficialStatementAnalysisValidation.Revalidate(analysis, card, out _));
    }

    [Fact]
    public void MarkerFreeExistingPurchaseMatchesOnlyAsCandidateAndRefundIsNotAnotherObligation()
    {
        var facts = Reference(); var purchase = facts.Purchases.Items[1];
        var candidates = StatementFinancialClassifier.FindDuplicates(facts,
            [new(StatementKnownInstallmentImport.DescriptionKey(purchase.OriginalDescription), purchase.Amount, purchase.InstallmentNumber!.Value, purchase.InstallmentCount!.Value)]);
        Assert.Contains(candidates, d => d.ItemIndex == 1 && d.Code == "ExistingObligationCandidate");
        var refund = Component("REFUNDED SHOP", "99.00", "Refund", "CurrentInvoice", "Credit");
        facts = facts with { Components = facts.Components.Append(refund).ToArray() };
        Assert.DoesNotContain(StatementFinancialClassifier.FindDuplicates(facts, [new("REFUNDED SHOP", "99.00", 1, 1)]),
            d => d.ItemIndex == facts.Purchases.Items.Count + facts.Components.Count - 1 && d.Code == "ExistingObligationCandidate");
    }

    [Fact]
    public void PrincipalAndInterestCanShareLiteralQuoteWithoutBeingDuplicatePostings()
    {
        const string quote = "OPERACAO 115.00 Principal 100.00 Juros 15.00";
        var facts = new StatementFinancialDocument("3.0", new("2.0", new(null, null, null, null, null, "115.00"), []),
            [Component("OPERACAO", "115.00", "FinancialOperation", "CurrentInvoice") with { Evidence = quote },
             Component("Principal", "100.00", "PrincipalDetail", "Information") with { Evidence = quote },
             Component("Juros", "15.00", "InterestDetail", "Information") with { Evidence = quote }]);
        var source = new CreditCardPdfExtractedDocument([new(1, "Lançamentos atuais\n" + quote)]);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Empty(result.Diagnostics); Assert.Equal("115.00", result.CurrentInvoice.ComponentsTotal);
        var doubled = facts with { Components = facts.Components.Append(facts.Components[0]).ToArray() };
        Assert.Contains(StatementFinancialClassifier.Evaluate(doubled, source).Diagnostics, d => d.Code == "FinancialEvidenceOccurrenceMismatch");
    }

    [Theory]
    [InlineData("03/06 OPERACAO 1.00")]
    [InlineData("OPERACAO CODIGO03/06 1.00")]
    [InlineData("OPERACAO em 03/06 1.00")]
    public void DateAndCommercialCodeCannotProveAFutureContract(string quote)
    {
        var c = Component("OPERACAO", "1.00", "FinancialOperation", "NextInvoice", number: 3, count: 6, context: "Compras parceladas - próximas faturas") with { Evidence = quote };
        var facts = Reference() with { Components = [c] };
        Assert.Contains(StatementFinancialClassifier.Evaluate(facts, Source(facts)).Diagnostics, d => d.Code == "FinancialRoleEvidenceInvalid");
    }

    // Sanitized reference SHAPE, not a captured model response. Amounts reproduce the documentary
    // arithmetic, labels/identity are synthetic; scope is deliberately not promoted to coverage proof.
    internal static StatementFinancialDocument Reference()
    {
        decimal[] current = [53.71m,320.71m,199m,102.50m,100.65m,112.89m,201.47m,31.60m,242.99m,47.35m,47.76m,252.94m,81.01m,139.80m,29.90m,6.99m,10m,33.27m,45.05m,46.99m,80m,22.89m,20m,100m,24.90m,10.85m,99.90m,29.28m,22.89m,20.98m,9.12m,100m,38.58m,67.61m,22.99m,81.14m,21.57m,233.71m,28.98m,19.90m,54.60m];
        (int N, int Count)[] ratios = [(7,7),(7,10),(6,12),(6,6),(5,6),(5,12),(5,12),(4,5),(4,10),(4,4),(4,4),(2,3),(2,5),(2,5)];
        var items = current.Select((a,i) => Item(i, a, i < ratios.Length ? ratios[i].N : null, i < ratios.Length ? ratios[i].Count : null, false)).ToList();
        int[] next = [1,2,4,5,6,7,8,11,12,13];
        items.AddRange(next.Select(i => Item(i, current[i], ratios[i].N + 1, ratios[i].Count, true)));
        var components = new List<StatementFinancialComponent>
        {
            Component("CREDITO A", "0.04", "Credit", "CurrentInvoice", "Credit"), Component("ESTORNO B", "29.90", "Refund", "CurrentInvoice", "Credit"),
            Component("ENCARGOS", "12.39", "Fee", "CurrentInvoice"), Component("IOF EXTERIOR", "1.91", "Tax", "CurrentInvoice"), Component("IOF FINANCIAMENTO", "1.09", "Tax", "CurrentInvoice"),
            Component("PAGAMENTO ANTERIOR", "10999.20", "Payment", "Information", "Credit"),
            Component("PRINCIPAL", "1385.89", "PrincipalDetail", "Information"), Component("JUROS DETALHADOS", "428.84", "InterestDetail", "Information"),
            Component("SALDO ANTERIOR", "9228.49", "PreviousBalance", "Information"), Component("LIMITE", "22000.00", "Limit", "Information"),
            Component("SIMULACAO", "150.00", "Simulation", "Offer", context: "Simulação de parcelamento"),
            Component("OFERTA", "5.99", "Offer", "Offer", unit: "Percent", context: "Oferta não contratada"),
            Component("Lançamentos no cartão", "3144.32", "Subtotal", "Control"), Component("Lançamentos internacionais", "56.51", "Subtotal", "Control"),
            Component("Produtos e serviços", "3604.81", "Subtotal", "Control"), Component("Total lançamentos atuais", "6805.64", "Subtotal", "Control"),
            Component("Total da fatura", "6806.73", "CurrentTotal", "Control"), Component("Próxima fatura", "4286.25", "NextInvoiceTotal", "Control"),
            Component("Demais faturas", "9273.59", "RemainingFutureTotal", "Control"), Component("Total para próximas faturas", "13559.84", "FutureTotal", "Control")
        };
        decimal[] operations = [491.69m,309.93m,155.55m,167.93m,1814.73m,464.98m,200m];
        for (var i = 0; i < operations.Length; i++) components.Add(Component("OPERACAO" + i, Money(operations[i]), "FinancialOperation", "CurrentInvoice"));
        decimal[] future = [155.55m,167.93m,1814.73m,464.98m];
        for (var i = 0; i < future.Length; i++) components.Add(Component($"CONTRATO{i} 02/03", Money(future[i]), "FinancialOperation", "NextInvoice", number: 2, count: 3, context: "Compras parceladas - próximas faturas"));
        return new("3.0", new("2.0", new(null, null, "2026-10", null, "2026-10-15", "6806.73"), items), components);
    }
    private static CreditCardStatementAgentItem Item(int id, decimal amount, int? n, int? count, bool future)
    {
        var label = $"SHOP{id}" + (n is null ? "" : $" {n:00}/{count:00}");
        return new(label, Money(amount), "Debit", n is null ? "Purchase" : "Installment", n, count,
            future ? "FutureInstallments" : "CurrentInvoice", !future, 1, $"04/07 {label} {Money(amount)}", "High");
    }
    internal static StatementFinancialComponent Component(string label, string amount, string kind, string scope, string nature = "Debit", string unit = "BRL", int? number = null, int? count = null, string context = "Lançamentos atuais") =>
        new(label, kind, scope, nature, unit, amount, 1, label + " " + amount, 1, context, number, count, "High");
    internal static CreditCardPdfExtractedDocument Source(StatementFinancialDocument facts) => new([new(1,
        string.Join('\n', facts.Purchases.Items.Select(i => i.Evidence).Concat(facts.Components.SelectMany(c => new[] { c.ContextEvidence, c.Evidence }))))]);
    private static string Money(decimal n) => n.ToString("0.00", CultureInfo.InvariantCulture);
    private sealed class Provider(string response) : ICreditCardStatementAgentProvider
    {
        public int Calls; public CreditCardStatementAgentProviderRequest? Request;
        public Task<string> CompleteAsync(CreditCardStatementAgentProviderRequest request, CancellationToken ct)
        { Calls++; Request = request; return Task.FromResult(response); }
    }
}
