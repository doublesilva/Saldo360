using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;
using System.Text;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Families;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Families;

namespace Saldo360.Tests.CreditCards;

public sealed class CreditCardCsvPreviewServiceTests
{
    private static readonly Guid Family = Guid.NewGuid();
    private static readonly Guid Card = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public async Task SupportsUtf8BomSemicolonQuotedMultilineAndExactValues()
    {
        var csv = "Data;Descrição;Valor;Identificador\n20/10/2026;\"Compra; importante\ncontinuação\";1.234,56;ext-42\n";
        var result = await PreviewAsync(csv, new(null, null, null, null, null, null, "Identificador", null, ";", "dd/MM/yyyy", "PtBr"));

        Assert.Equal(CreditCardCsvPreviewAccess.Allowed, result.Access);
        var row = Assert.Single(result.Preview!.Rows);
        Assert.Equal(("2026-10-20", "Compra; importante continuação", "1234.56", "ext-42", "Valid"),
            (row.TransactionDate, row.Description, row.Amount, row.ExternalId, row.Status));
    }

    [Fact]
    public async Task ReportsInvalidDateAndNegativeValueWarningWithoutWriting()
    {
        var result = await PreviewAsync("date,description,amount\n2026-10-01,Estorno,-12.30\n");

        var row = Assert.Single(result.Preview!.Rows);
        Assert.Equal("Warning", row.Status);
        Assert.Contains("Valor negativo", row.Warnings.Single());
        Assert.Equal(0, FakePurchase.SaveCalls);
    }

    [Fact]
    public async Task RejectsMissingMappedRequiredColumnAndAmbiguousAmount()
    {
        var missing = await PreviewAsync("date,description\n2026-10-01,Compra\n");
        Assert.Equal(CreditCardCsvPreviewAccess.Invalid, missing.Access);
        Assert.Contains("amountColumn", missing.Errors!.Keys);

        var ambiguous = await PreviewAsync("date,description,amount\n2026-10-01,Compra,1.234\n");
        var row = Assert.Single(ambiguous.Preview!.Rows);
        Assert.Contains("ambíguo", row.Errors.Single(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CreditCardCsvPreviewResult> PreviewAsync(string csv, CreditCardCsvPreviewRequest? request = null)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
        await using var stream = new MemoryStream(bytes);
        FakePurchase.SaveCalls = 0;
        return await new CreditCardCsvPreviewService(new FakeFamily(), new FakePurchase()).PreviewAsync(
            Family, Card, User, stream, bytes.Length,
            request ?? new(null, null, null, null, null, null, null, null, null, null, null), CancellationToken.None);
    }

    private sealed class FakeFamily : IFinancialWorkspaceRepository
    {
        public Task AddAsync(Saldo360.Domain.Families.Family family, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<FamilySummary>> ListForUserAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FamilySummary>>([]);
        public Task<FamilyDetails?> FindForMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<FamilyDetails?>(null);
        public Task<FinancialWorkspace> EnsurePersonalAsync(Guid user, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<FinancialWorkspaceRole?>(FinancialWorkspaceRole.Owner);
    }

    private sealed class FakePurchase : IFinancialCreditCardPurchaseRepository
    {
        public Task<T> InWorkspaceTransactionAsync<T>(Guid workspaceId, Func<Task<T>> operation, CancellationToken ct) => throw new NotSupportedException("CSV preview must not enter a write transaction.");
        public Task<bool> CanEditAsync(Guid workspaceId, Guid cardId, Guid purchaseId, DateOnly today, CancellationToken ct) => throw new NotSupportedException();
        public Task<CreditCardPortfolioDto> GetPortfolioAsync(Guid workspaceId, string competence, DateOnly today, CancellationToken ct) => throw new NotSupportedException();
        public static int SaveCalls { get; set; }
        public Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<FinancialCreditCardPurchase?> FindAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult<FinancialCreditCardPurchase?>(null);
        public Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult<FinancialCreditCardPurchaseDto?>(null);
        public Task<IReadOnlyList<FinancialCreditCardPurchaseDto>> ListAsync(Guid workspaceId, Guid cardId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialCreditCardPurchaseDto>>([]);
        public Task SaveChangesAsync(CancellationToken cancellationToken) { SaveCalls++; return Task.CompletedTask; }
        public Task<CreditCardCalendarConfig?> GetCardCalendarAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken) => Task.FromResult<CreditCardCalendarConfig?>(null);
        public Task<FinancialCreditCardInvoice?> FindInvoiceAsync(Guid workspaceId, Guid cardId, DateOnly closingDate, CancellationToken cancellationToken) => Task.FromResult<FinancialCreditCardInvoice?>(null);
        public Task AddInvoiceAsync(FinancialCreditCardInvoice invoice, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ListInvoicesAsync(Guid workspaceId, Guid cardId, string from, string through, DateOnly today, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialCreditCardInvoiceDto>>([]);
        public Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid workspaceId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken) => Task.FromResult<FinancialCreditCardInvoiceDto?>(null);
        public Task<bool> CanCancelAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<bool> HasActiveReconciliationAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ProjectInvoicesAsync(Guid workspaceId, Guid cardId, string from, int months, DateOnly today, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialCreditCardInvoiceDto>>([]);
        public Task<FinancialCreditCardLimitDto?> GetLimitAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken) => Task.FromResult<FinancialCreditCardLimitDto?>(null);
        public Task<bool> IsActiveCardAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> IsExpenseCategoryAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
