using Saldo360.Application.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardPdfPreReconciliationServiceTests
{
    private static readonly Guid FamilyId = Guid.NewGuid();
    private static readonly Guid CardId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task ClassifiesDocumentComponentsWithoutConvertingInformationalOrUnsupportedRows()
    {
        var operations = new List<CreditCardPdfExistingOperation>
        {
            new("HistoricalInstallment", Guid.NewGuid(), null, new(2026, 9, 5), "LOJA X", 150m, 2, 6, "2026-10"),
            new("InvoicePayment", Guid.NewGuid(), null, new(2026, 10, 1), "Pagamento de fatura", -100m, null, null, "2026-10"),
        };
        var service = CreateService(Preview([
            Item(1, "Purchase", "Debit", "2026-10-02", "MERCADO", "10.25"),
            Item(2, "Installment", "Debit", "2026-09-05", "LOJA X", "150.00", 2, 6),
            Item(3, "Payment", "Credit", "2026-10-01", "PAGAMENTO", "-100.00"),
            Item(4, "PreviousBalance", "Debit", null, "SALDO ANTERIOR", "100.00"),
            Item(5, "FutureInstallment", "Informational", null, "LOJA X", "150.00", 3, 6, false),
            Item(6, "Fee", "Debit", "2026-10-03", "IOF", "2.00"),
            Item(7, "Credit", "Credit", "2026-10-04", "ESTORNO", "-5.00"),
        ]), operations);

        var result = await Run(service, [1, 2, 3]);

        Assert.Equal(CreditCardPdfPreviewAccess.Allowed, result.Access);
        Assert.Equal(3, result.Report!.Counts.Create);
        Assert.Equal(1, result.Report.Counts.NeedsReview);
        Assert.Equal(3, result.Report.Counts.Informational);
        Assert.Equal(0, result.Report.Counts.Unsupported);
        var candidate = Assert.Single(result.Report.Items, item => item.RowNumber == 2);
        Assert.Equal("NeedsReview", candidate.Classification);
        Assert.Equal("HistoricalInstallment", candidate.MatchedEntityType);
        Assert.Equal("Informational", Assert.Single(result.Report.Items, item => item.Type == "Payment").Classification);
        Assert.Equal("Create", Assert.Single(result.Report.Items, item => item.Type == "Fee").Classification);
        Assert.Equal("NeedsReview", result.Report.Destination.MaskedCard);
        Assert.Equal("0.00", result.Report.Reconciliation.Difference);
        Assert.DoesNotContain("HolderName", System.Text.Json.JsonSerializer.Serialize(result.Report));
    }

    [Fact]
    public async Task RepeatedDocumentHasStableIdentityAndStrongIdentityDetectsExistingAndConflict()
    {
        var bytes = new byte[] { 9, 8, 7, 6 };
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var operations = new List<CreditCardPdfExistingOperation>
        {
            new("ImportedInstallmentLink", Guid.NewGuid(), $"pdf:{hash}:p2:r10", new(2026, 10, 2), "LOJA", 40m, 1, 1, "2026-10"),
            new("HistoricalInstallment", Guid.NewGuid(), $"pdf:{hash}:p2:r11", new(2026, 10, 3), "OUTRA", 99m, 2, 3, "2026-10"),
        };
        var service = CreateService(Preview([
            Item(10, "Purchase", "Debit", "2026-10-02", "LOJA", "40.00", page: 2),
            Item(11, "Installment", "Debit", "2026-10-03", "OUTRA", "50.00", 2, 3, page: 2),
        ]), operations);

        var first = await Run(service, bytes);
        var repeated = await Run(service, bytes);

        Assert.Equal(first.Report!.DocumentKey, repeated.Report!.DocumentKey);
        Assert.Equal("AlreadyExists", first.Report.Items[0].Classification);
        Assert.Equal("Conflict", first.Report.Items[1].Classification);
        Assert.Equal(2, operations.Count);
    }

    private static CreditCardPdfPreReconciliationService CreateService(
        CreditCardPdfPreview preview, IReadOnlyList<CreditCardPdfExistingOperation> operations) =>
        new(new FakePreviewService(preview), new FakeRepository(operations));

    private static Task<CreditCardPdfPreReconciliationResult> Run(
        CreditCardPdfPreReconciliationService service, byte[] bytes) =>
        service.PreReconcileAsync(FamilyId, CardId, UserId, new MemoryStream(bytes), bytes.Length,
            "fatura.pdf", null, null, null, null, CancellationToken.None);

    private static CreditCardPdfPreview Preview(IReadOnlyList<CreditCardPdfPreviewItem> items) =>
        new("fatura.pdf", 3, "ItauMastercard",
            new("Itau", "9876", "2026-10", "2026-10-08", "2026-10-15", "297.25"),
            new(items.Count, 0, 0, 0), items, [])
        { Reconciliation = new("Reconciled", "297.25", "297.25", "0.00", false) };

    private static CreditCardPdfPreviewItem Item(
        int row, string type, string nature, string? date, string description, string amount,
        int? installment = null, int? count = null, bool current = true, int page = 1) =>
        new(row, page, description, date, description, amount, type, installment, count, "2026-10", "Valid", [], [])
        { Nature = nature, IncludedInCurrentTotal = current, OriginalDescription = description, NormalizedDescription = description };

    private sealed class FakePreviewService(CreditCardPdfPreview preview) : ICreditCardPdfPreviewService
    {
        public Task<CreditCardPdfPreviewResult> PreviewAsync(Guid familyId, Guid cardId, Guid userId, Stream pdf, long length, string fileName, string? issuerProfile, string? competence, string? dateFormat, string? password, CancellationToken cancellationToken) =>
            Task.FromResult(new CreditCardPdfPreviewResult(CreditCardPdfPreviewAccess.Allowed, preview, null));
    }

    private sealed class FakeRepository(IReadOnlyList<CreditCardPdfExistingOperation> operations) : ICreditCardPdfPreReconciliationRepository
    {
        public Task<CreditCardPdfPreReconciliationSnapshot?> GetSnapshotAsync(Guid familyId, Guid cardId, Guid userId, string? competence, CancellationToken cancellationToken) =>
            Task.FromResult<CreditCardPdfPreReconciliationSnapshot?>(new(
                "Cartão teste", "Itau", UserId,
                new(Guid.NewGuid(), "2026-10", new(2026, 10, 8), new(2026, 10, 15)), operations));
    }
}
