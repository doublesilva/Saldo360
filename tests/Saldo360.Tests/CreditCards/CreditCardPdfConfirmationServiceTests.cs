using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardPdfConfirmationServiceTests
{
    [Fact]
    public async Task RejectsDifferentDocumentGenericParserAndFinancialReviewRows()
    {
        var valid = Report();
        var wrongDocument = await Run(valid, new(true, "pdf-sha256:other", []));
        Assert.Equal(CreditCardPdfConfirmationAccess.Conflict, wrongDocument.Access);
        var generic = await Run(valid with { Parser = "Generic" }, Input());
        Assert.Equal(CreditCardPdfConfirmationAccess.Invalid, generic.Access);
        var review = valid with { Items = [Item("NeedsReview", "Purchase", true)] };
        Assert.Equal(CreditCardPdfConfirmationAccess.Invalid, (await Run(review, Input())).Access);
    }

    [Fact]
    public async Task RequiresCategoryForPurchaseButNeverForInformationalPayment()
    {
        var purchase = Report() with { Items = [Item("Create", "Purchase", true)] };
        Assert.Equal(CreditCardPdfConfirmationAccess.Invalid, (await Run(purchase, Input())).Access);

        var repository = new FakeRepository();
        var payment = Report() with { Items = [Item("Informational", "Payment", true)] };
        var result = await Run(payment, Input(), repository);
        Assert.Equal(CreditCardPdfConfirmationAccess.Allowed, result.Access);
        Assert.Equal("Payment", Assert.Single(repository.Confirmation!.Components).Type);
        Assert.Null(Assert.Single(repository.Confirmation.Components).CategoryId);
    }

    private static CreditCardPdfConfirmationInput Input() => new(true, "pdf-sha256:abc", []);
    private static Task<CreditCardPdfConfirmationOperationResult> Run(CreditCardPdfPreReconciliationReport report, CreditCardPdfConfirmationInput input, FakeRepository? repository = null)
    {
        repository ??= new();
        var service = new CreditCardPdfConfirmationService(new FakePreReconciliation(report), repository, TimeProvider.System);
        return service.ConfirmAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new MemoryStream([1]), 1, "fatura.pdf", null, null, null, "ephemeral", input, CancellationToken.None);
    }

    private static CreditCardPdfPreReconciliationReport Report() => new(
        "pdf-sha256:abc", "ItauMastercard", new("Itau", "9876", "2026-10", "2026-10-08", "2026-10-15", "0.00"),
        new("Reconciled", "0.00", "0.00", "0.00", false),
        new(Guid.NewGuid(), Guid.NewGuid(), "Validated", "Validated", "Validated", "NeedsReview", "Validated", Guid.NewGuid(), []),
        new(0, 0, 0, 0, 0, 0), [], [], []);

    private static CreditCardPdfPreReconciliationItem Item(string classification, string type, bool current) =>
        new("pdf:abc:p1:r1", 1, 1, type, type == "Payment" ? "Credit" : "Debit", "2026-10-01", type, type == "Payment" ? "-10.00" : "10.00", null, null, current, classification, null, null, []);

    private sealed class FakePreReconciliation(CreditCardPdfPreReconciliationReport report) : ICreditCardPdfPreReconciliationService
    {
        public Task<CreditCardPdfPreReconciliationResult> PreReconcileAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken) =>
            Task.FromResult(new CreditCardPdfPreReconciliationResult(CreditCardPdfPreviewAccess.Allowed, report, null));
    }

    private sealed class FakeRepository : ICreditCardPdfConfirmationRepository
    {
        public CreditCardPdfTrustedConfirmation? Confirmation { get; private set; }
        public Task<CreditCardPdfConfirmationOperationResult> ConfirmAsync(Guid familyId, Guid cardId, Guid userId, CreditCardPdfTrustedConfirmation confirmation, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Confirmation = confirmation;
            return Task.FromResult(new CreditCardPdfConfirmationOperationResult(CreditCardPdfConfirmationAccess.Allowed));
        }
    }
}
