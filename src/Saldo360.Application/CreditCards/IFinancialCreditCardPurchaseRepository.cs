using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardPurchaseRepository
{
    Task<T> InWorkspaceTransactionAsync<T>(Guid workspaceId, Func<Task<T>> operation, CancellationToken ct);
    Task<bool> CanEditAsync(Guid workspaceId, Guid cardId, Guid purchaseId, DateOnly today, CancellationToken ct);
    Task<CreditCardPortfolioDto> GetPortfolioAsync(Guid workspaceId, string competence, DateOnly today, CancellationToken ct);
    Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken);
    Task<FinancialCreditCardPurchase?> FindAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken);
    Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardPurchaseDto>> ListAsync(Guid workspaceId, Guid cardId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<CreditCardCalendarConfig?> GetCardCalendarAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken);
    Task<FinancialCreditCardInvoice?> FindInvoiceAsync(Guid workspaceId, Guid cardId, DateOnly closingDate, CancellationToken cancellationToken);
    Task AddInvoiceAsync(FinancialCreditCardInvoice invoice, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ListInvoicesAsync(Guid workspaceId, Guid cardId, string from, string through, DateOnly today, CancellationToken cancellationToken);
    Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid workspaceId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken);
    Task<bool> CanCancelAsync(Guid workspaceId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ProjectInvoicesAsync(Guid workspaceId, Guid cardId, string from, int months, DateOnly today, CancellationToken cancellationToken);
    Task<FinancialCreditCardLimitDto?> GetLimitAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken);
    Task<bool> IsActiveCardAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken);
    /// <summary>Categoria de despesa do orçamento disponível para NOVA classificação (ativa e, numa subcategoria, com a principal ativa); a atual da compra pode estar inativa.</summary>
    Task<bool> IsExpenseCategoryAsync(Guid workspaceId, Guid categoryId, Guid? currentCategoryId, CancellationToken cancellationToken);
}

public sealed record CreditCardCalendarConfig(int ClosingDay, int DueDay);
