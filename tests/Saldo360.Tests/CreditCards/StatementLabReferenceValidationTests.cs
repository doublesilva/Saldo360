using System.Text.Json;
using Saldo360.Application.CreditCards;
using Saldo360.Infrastructure.CreditCards;

namespace Saldo360.Tests.CreditCards;

[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
public sealed class StatementLabReferenceValidationTests
{
    private const string Hash = "synthetic-sha256";
    private static CreditCardStatementAgentDocument Facts => new("2.0", new(null, null, "2026-10", null, "2026-10-13", "10.00"),
        [new("PRIVATE-MERCHANT03/12", "10.00", "Debit", "Installment", 3, 12, "CurrentInvoice", true, 1, "PRIVATE-MERCHANT03/12 10.00", "High", "2026-07-04"),
         new("PRIVATE-MERCHANT04/12", "10.00", "Debit", "Installment", 4, 12, "FutureInstallments", false, 1, "PRIVATE-MERCHANT04/12 10.00", "High", "2026-07-04")]);
    private static CreditCardStatementExtractionResult Result => new("AgentPrimary", "Agent", new("synthetic.pdf", 1, Facts.Header, Facts.Items,
        new("Reconciled", "10.00", "10.00", "0.00", false)), AgentExtractionAssessment.Reliable, false, null, null, null, null)
    {
        ValidationStatus = "Valid", PageCoverage = [new(1, "Native", "NativeWithImage", true)],
        OutputCoverage = new(1, [new(1, 1, true, true, true, 2, "ReturnedValidUnproven", null)], [new(1, [1], 2, "ReturnedValidUnproven", null)])
    };
    [Trait("Category", "INFRA")]
    [Fact]
    public void ExactReferenceMatchesWithoutChangingFinancialAssessmentOrCoverage()
    {
        var original = Result with { DocumentWarnings = ["RecognitionCoverageUnproven", "PurchasesOnlyScope"] };
        var evaluated = StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts), original);
        Assert.True(evaluated.Approved); Assert.Empty(evaluated.Diagnostics);
        Assert.Equal(AgentExtractionAssessment.Reliable, original.Assessment); Assert.False(original.OutputCoverage!.RecognitionComplete);
    }
    [Trait("Category", "INFRA")]
    [Theory]
    [InlineData("number")]
    [InlineData("amount")]
    [InlineData("date")]
    [InlineData("description")]
    [InlineData("section")]
    public void WrongFieldsCannotPassReferenceAndDiagnosticsContainNoFinancialValues(string field)
    {
        var first = Facts.Items[0];
        var wrong = field switch {
            "number" => first with { InstallmentNumber = 12 }, "amount" => first with { Amount = "20.00" },
            "date" => first with { TransactionDate = null }, "description" => first with { OriginalDescription = "OTHER" },
            _ => first with { Section = "FutureInstallments", IncludedInCurrentTotal = false } };
        var result = Result with { Primary = Result.Primary! with { Items = [wrong, Facts.Items[1]] } };
        var compared = StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts), result);
        Assert.False(compared.Approved); Assert.False(compared.ExactMatch);
        var json = JsonSerializer.Serialize(compared); Assert.DoesNotContain("PRIVATE-MERCHANT", json); Assert.DoesNotContain("20.00", json);
    }
    [Trait("Category", "INFRA")]
    [Fact]
    public void MissingRowsAndExtraCopiesFailButLegitimateIdenticalRowsAreNotDeduplicated()
    {
        var extra = Result with { Primary = Result.Primary! with { Items = [.. Facts.Items, Facts.Items[0]] } };
        Assert.Contains(StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts), extra).Diagnostics, d => d.Code == "ReferenceUnexpectedOrDuplicateItem");
        Assert.True(StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts with { Items = [.. Facts.Items, Facts.Items[0]] }), extra).ExactMatch);
        var missing = Result with { Primary = Result.Primary! with { Items = [Facts.Items[0]] } };
        Assert.False(StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts), missing).ExactMatch);
    }
    [Trait("Category", "INFRA")]
    [Fact]
    public void RejectionUnknownSourceCoverageAndOcrAreNeverApprovedByExactMoneyAlone()
    {
        foreach (var result in new[] { Result with { Assessment = AgentExtractionAssessment.Rejected },
            Result with { PrimarySource = "Parser" },
            Result with { Assessment = AgentExtractionAssessment.NeedsReview, DocumentWarnings = ["PageWithoutItemsUnproven"] },
            Result with { Primary = null }, Result with { OutputCoverage = null },
            Result with { DocumentWarnings = ["OCRRequiresReview"] }, Result with { ValidationStatus = "NeedsReview" } })
            Assert.False(StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts), result).Approved,
                $"source={result.PrimarySource}; assessment={result.Assessment}; validation={result.ValidationStatus}; primary={result.Primary is not null}; coverage={result.OutputCoverage is not null}; warnings={string.Join(',', result.DocumentWarnings ?? [])}");
        Assert.False(StatementLabReferenceValidation.Compare("wrong-source", new(Hash, 1, Facts), Result).Approved);
    }
    [Trait("Category", "CORE")]
    [Fact]
    public void NativeCoordinatePreparationKeepsAllWordsAndSeparatesTableColumnsWithoutParsingFinancialFacts()
    {
        CreditCardPdfExtractedWord[] words = [new("10.00", 200, 20, 240, 30), new("SHOP03/12", 40, 20.5, 100, 30),
            new("04/07", 0, 20, 30, 30), new("FOOTER", 0, 50, 100, 60)];
        Assert.Equal("04/07 SHOP03/12 10.00\nFOOTER", CreditCardPdfTextExtractor.ReflowNativeWords(words, "unseparated"));
        Assert.Equal("unchanged", CreditCardPdfTextExtractor.ReflowNativeWords([], "unchanged"));
    }

    [Trait("Category", "CORE")]
    [Fact]
    public void LargeRasterRegionWithOnlyNativeFooterCannotProveCoverageButTranscribedBackgroundIsNotBlank()
    {
        CreditCardPdfExtractedWord[] footer = [new("FOOTER", 10, 810, 200, 820)];
        Assert.True(CreditCardPdfTextExtractor.UntranscribedRasterRegion(footer, 0, 0, 595, 842, 595 * 842));
        var native = Enumerable.Range(0, 20).SelectMany(row => Enumerable.Range(0, 17)
            .Select(column => new CreditCardPdfExtractedWord("NATIVE", 10 + column * 32, 20 + row * 35, 35 + column * 32, 28 + row * 35))).ToArray();
        Assert.False(CreditCardPdfTextExtractor.UntranscribedRasterRegion(native, 0, 0, 595, 842, 595 * 842));
    }

    [Trait("Category", "INFRA")]
    [Fact]
    public void DocumentaryReferenceCannotOverrideIncompleteCoverageEvenWithReliableAssessment()
    {
        var documentary = new StatementPurchaseDocumentValidation(true, false, new("Unverified", false), [], []);
        var original = Result with { PurchaseDocumentValidation = documentary,
            OutputCoverage = Result.OutputCoverage! with { PurchasesOnlyRowsVerified = true },
            DocumentWarnings = ["IdentityEvidenceUnverified", "PurchaseDatesIncomplete", "PurchasesOnlyScope"] };
        Assert.True(StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts), original).Approved);
        foreach (var incomplete in new[] {
            original with { PurchaseDocumentValidation = documentary with { CoverageComplete = false } },
            original with { OutputCoverage = original.OutputCoverage! with { PurchasesOnlyRowsVerified = false } },
            original with { PurchaseDocumentValidation = documentary with { Diagnostics = [new("DocumentIdentityContradiction", "$.header", Contradiction: true)] } } })
            Assert.False(StatementLabReferenceValidation.Compare(Hash, new(Hash, 1, Facts), incomplete).Approved);
        Assert.False(original.PurchaseDocumentValidation!.Identity.AuthorizedIdentityConfirmed);
        Assert.Equal(Facts.Items, original.Primary!.Items);
    }
}
