using System.Globalization;
using System.Text.Json;
using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Category", "REGRESSION")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
[Trait("Profile", "FAST")]
public sealed class StatementFinancialAssociationTests
{
    [Fact]
    public void SummaryAndDetailKeepOriginalOccurrencesButCountInternationalIofOnce()
    {
        var (facts, source) = Example(); var original = JsonSerializer.Serialize(facts);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        var link = Assert.Single(result.Associations);
        Assert.Equal(0, link.SummaryComponentIndex); Assert.Equal([1], link.DetailComponentIndexes);
        Assert.Equal("InternationalIofSummaryOfDetails", link.Code);
        Assert.Equal("785.33", result.CurrentInvoice.ComponentsTotal); Assert.Equal("0.00", result.CurrentInvoice.Difference);
        Assert.Equal("Reconciled", result.CurrentInvoice.ArithmeticStatus);
        Assert.Equal(original, JsonSerializer.Serialize(result.Document));
        Assert.Equal(2, result.Document.Components.Count(c => c.Kind == "Tax"));
        Assert.False(result.ImportEligible); Assert.True(result.ReadOnly); Assert.False(result.CoverageProven);
    }

    [Theory]
    [InlineData("1.27")]
    [InlineData("8.39")]
    public void AssociationIsNotHardCodedToAnAmountOrAnOfficialTotal(string amount)
    {
        var (facts, source) = Example(amount, "999.99");
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Single(result.Associations);
        Assert.Equal(Money(781.31m + decimal.Parse(amount, CultureInfo.InvariantCulture)), result.CurrentInvoice.ComponentsTotal);
        Assert.Equal("Divergent", result.CurrentInvoice.ArithmeticStatus);
    }

    [Fact]
    public void TwoEqualLegitimateTaxesRemainTwoMovementsUnderAnAggregateSummary()
    {
        var (facts, source) = Example("4.02", "789.35", twoDetails: true, summary: "8.04");
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Equal([1, 2], Assert.Single(result.Associations).DetailComponentIndexes);
        Assert.Equal("789.35", result.CurrentInvoice.ComponentsTotal);
        Assert.Equal("0.00", result.CurrentInvoice.Difference);
        Assert.Equal(3, result.Document.Components.Count(c => c.Kind == "Tax"));
    }

    [Fact]
    public void EqualValuesDoNotAllowPickingOnlyOneOfTwoTaxesToCloseTheTotal()
    {
        var (facts, source) = Example(twoDetails: true);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Empty(result.Associations);
        Assert.Contains(result.Diagnostics, d => d.Code == "FinancialSummaryAssociationUnproven");
        Assert.Equal("793.37", result.CurrentInvoice.ComponentsTotal);
        Assert.Equal("Unproven", result.CurrentInvoice.Status);
    }

    [Fact]
    public void IdenticalPurchasesAtDifferentDatesAreNeverCollapsed()
    {
        var (facts, source) = Example(); var purchase = facts.Purchases.Items[^1];
        var second = purchase with { Evidence = purchase.Evidence.Replace("02 OUT", "03 OUT", StringComparison.Ordinal) };
        facts = facts with { Purchases = facts.Purchases with { Items = facts.Purchases.Items.Append(second).ToArray() } };
        source = new(source.Pages.Select(p => p.Number == 5 ? p with { Text = p.Text + "\n" + second.Evidence } : p).ToArray());
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Equal("900.18", result.CurrentInvoice.ComponentsTotal);
        Assert.Equal(7, result.Document.Purchases.Items.Count);
        Assert.Contains(result.Duplicates, d => d.Code == "PossibleDuplicate");
    }

    [Theory]
    [InlineData("missing-summary")]
    [InlineData("missing-detail")]
    [InlineData("missing-currency")]
    [InlineData("missing-purchase")]
    [InlineData("wrong-tax-date")]
    [InlineData("remote-section")]
    [InlineData("low-confidence")]
    [InlineData("wrong-nature")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-amount")]
    [InlineData("ocr-only")]
    [InlineData("duplicate-summary")]
    [InlineData("reused-detail")]
    public void MissingOrAmbiguousEvidenceDoesNotAuthorizeAssociation(string mutation)
    {
        var (facts, source) = Example(); var detail = facts.Components[1];
        var p4 = source.Pages[0]; var p5 = source.Pages[1];
        switch (mutation)
        {
            case "missing-summary": p4 = p4 with { Text = p4.Text.Replace(facts.Components[0].Evidence, "unrelated", StringComparison.Ordinal) }; break;
            case "missing-detail": p5 = p5 with { Text = p5.Text.Replace(detail.Evidence, "unrelated", StringComparison.Ordinal) }; break;
            case "missing-currency": p5 = p5 with { Text = p5.Text.Replace("BRL 110.00 = USD 21.14", "", StringComparison.Ordinal) }; break;
            case "missing-purchase": facts = facts with { Purchases = facts.Purchases with { Items = facts.Purchases.Items.Take(5).ToArray() } }; break;
            case "wrong-tax-date":
                detail = detail with { Evidence = detail.Evidence.Replace("02 OUT", "03 OUT", StringComparison.Ordinal) };
                p5 = p5 with { Text = p5.Text.Replace("02 OUT IOF", "03 OUT IOF", StringComparison.Ordinal) }; break;
            case "remote-section": p5 = p5 with { Text = p5.Text.Replace("02 OUT IOF", "Ofertas\n02 OUT IOF", StringComparison.Ordinal) }; break;
            case "low-confidence": detail = detail with { Confidence = "Low" }; break;
            case "wrong-nature": detail = detail with { Nature = "Credit" }; break;
            case "wrong-kind": detail = detail with { Kind = "Fee" }; break;
            case "wrong-amount": detail = detail with { Amount = "9.99" }; break;
            case "ocr-only": p5 = p5 with { TextSource = "OCR" }; break;
            case "duplicate-summary": facts = facts with { Components = facts.Components.Append(facts.Components[0]).ToArray() }; break;
            case "reused-detail": facts = facts with { Components = facts.Components.Append(detail).ToArray() }; break;
        }
        facts = facts with { Components = facts.Components.Select((c, index) => index == 1 ? detail : c).ToArray() };
        var result = StatementFinancialClassifier.Evaluate(facts, new([p4, p5]));
        Assert.Empty(result.Associations); Assert.False(result.ImportEligible);
    }

    [Fact]
    public void ASecondUncertainIofCannotBeIgnoredToCreateAnAssociation()
    {
        var (facts, source) = Example();
        facts = facts with { Components = facts.Components.Append(facts.Components[1] with { Confidence = "Low", Amount = "1.00" }).ToArray() };
        Assert.Empty(StatementFinancialClassifier.Evaluate(facts, source).Associations);
    }

    [Fact]
    public void AnUnrelatedMonetaryFactDoesNotDisappearWhenArithmeticReconciles()
    {
        var (facts, source) = Example();
        source = new([source.Pages[0] with { Text = source.Pages[0].Text + "\nValor não classificado 9,45" }, source.Pages[1]]);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Single(result.Associations); Assert.Equal("Reconciled", result.CurrentInvoice.ArithmeticStatus);
        Assert.Equal("Unproven", result.CurrentInvoice.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "FinancialAmountsUnclassified");
        Assert.False(result.CoverageProven); Assert.False(result.ImportEligible);
    }

    [Fact]
    public void AssociationIsStableUnderComponentReordering()
    {
        var (facts, source) = Example(); facts = facts with { Components = facts.Components.Reverse().ToArray() };
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        var association = Assert.Single(result.Associations);
        Assert.Equal("IOF de compras internacionais", facts.Components[association.SummaryComponentIndex].Description);
        Assert.Equal("785.33", result.CurrentInvoice.ComponentsTotal);
    }

    [Theory]
    [InlineData("Total da próxima fatura", "NextInvoice")]
    [InlineData("Saldo projetado", "NextInvoice")]
    [InlineData("Principal", "CurrentInvoice")]
    [InlineData("Pagamento mínimo", "CurrentInvoice")]
    public void InformationalRowsMislabelledAsOperationsDoNotCreateAdditionalMovements(string label, string scope)
    {
        var (facts, source) = Example();
        var c = new StatementFinancialComponent(label, "FinancialOperation", scope, "Debit", "BRL", "100.00", 4, label + " 100,00", 4, "RESUMO DA FATURA ATUAL", null, null, "High");
        facts = facts with { Components = facts.Components.Append(c).ToArray() };
        source = new([source.Pages[0] with { Text = source.Pages[0].Text + "\n" + c.Evidence }, source.Pages[1]]);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Equal("785.33", result.CurrentInvoice.ComponentsTotal); Assert.Equal("0.00", result.NextInvoice.ComponentsTotal);
        Assert.Contains(result.Diagnostics, d => d.Code == "FinancialInformationalValueAsPosting");
        Assert.Equal("Unproven", result.CurrentInvoice.Status);
    }

    [Fact]
    public void FutureOffersWithInstallmentMarkersNeverBecomeContractedObligations()
    {
        var (facts, source) = Example();
        var offer = new StatementFinancialComponent("CONTRATO 02/03", "FinancialOperation", "NextInvoice", "Debit", "BRL", "50.00", 5,
            "CONTRATO 02/03 50,00", 5, "Simulação de próximas faturas", 2, 3, "High");
        facts = facts with { Components = facts.Components.Append(offer).ToArray() };
        source = new([source.Pages[0], source.Pages[1] with { Text = source.Pages[1].Text + "\n" + offer.ContextEvidence + "\n" + offer.Evidence }]);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Equal("0.00", result.NextInvoice.ComponentsTotal); Assert.Contains(result.Diagnostics, d => d.Code == "FinancialRoleEvidenceInvalid");
    }

    [Theory]
    [InlineData("Unreadable")]
    [InlineData("NativeConflict")]
    public void VisualUncertaintyIsNeverPromotedToDocumentaryIntegrity(string status)
    {
        var (facts, source) = Example();
        source = new([source.Pages[0], source.Pages[1] with { VisualRegions = [new(5, 0, 0, 0, 100, 100, status)] }]);
        var result = StatementFinancialClassifier.Evaluate(facts, source);
        Assert.Empty(result.Associations);
        Assert.Contains(result.Diagnostics, d => d.Code == "FinancialVisualEvidenceUnproven");
        Assert.Equal("Unproven", result.CurrentInvoice.Status); Assert.False(result.ImportEligible);
    }

    private static (StatementFinancialDocument, CreditCardPdfExtractedDocument) Example(string tax = "4.02", string official = "785.33", bool twoDetails = false, string? summary = null)
    {
        decimal[] amounts = [41.33m, 222.61m, 143.86m, 146.88m, 111.78m, 114.85m];
        var purchases = amounts.Select((amount, index) => new CreditCardStatementAgentItem(index == 5 ? "LOJA EXTERIOR" : "LOJA" + index,
            Money(amount), "Debit", "Purchase", null, null, "CurrentInvoice", true, 5,
            $"02 OUT {(index == 5 ? "LOJA EXTERIOR" : "LOJA" + index)} R$ {Local(Money(amount))}", "High")).ToArray();
        var s = new StatementFinancialComponent("IOF de compras internacionais", "Tax", "CurrentInvoice", "Debit", "BRL", summary ?? tax, 4,
            "IOF de compras internacionais R$ " + Local(summary ?? tax), 4, "RESUMO DA FATURA ATUAL", null, null, "High");
        var d = new StatementFinancialComponent("IOF de LOJA EXTERIOR", "Tax", "CurrentInvoice", "Debit", "BRL", tax, 5,
            "02 OUT IOF de \"LOJA EXTERIOR\" R$ " + Local(tax), 5, "TRANSAÇÕES DE 05 SET A 05 OUT", null, null, "High");
        var total = s with { Description = "Total da fatura", Kind = "CurrentTotal", Scope = "Control", Amount = official, Evidence = "Total da fatura R$ " + Local(official) };
        var components = new List<StatementFinancialComponent> { s, d };
        var text = "TRANSAÇÕES DE 05 SET A 05 OUT\n" + string.Join('\n', purchases.Select(p => p.Evidence)) + "\nBRL 110.00 = USD 21.14\n" + d.Evidence;
        if (twoDetails)
        {
            var second = d with { Description = "IOF de LOJA4", Evidence = d.Evidence.Replace("LOJA EXTERIOR", "LOJA4", StringComparison.Ordinal) }; components.Add(second);
            // Both purchases already exist in the six-row source; equal taxes do not merge them.
            text = text.Replace(purchases[4].Evidence, purchases[4].Evidence + "\nBRL 100.00 = USD 20.00\n" + second.Evidence, StringComparison.Ordinal);
        }
        components.Add(total);
        return (new("3.0", new("2.0", new(null, null, "2026-10", null, "2026-10-13", official), purchases), components),
            new([new(4, "RESUMO DA FATURA ATUAL\n" + s.Evidence + "\n" + total.Evidence), new(5, text)]));
    }
    private static string Local(string value) => value.Replace('.', ',');
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
