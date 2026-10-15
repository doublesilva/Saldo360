using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum CreditCardPurchaseStatus { Active, Cancelled }
public enum CreditCardInstallmentStatus { Active, Cancelled }

public sealed class FinancialCreditCardPurchase : Entity
{
    public const int DescriptionMaxLength = 200;
    public const int MaxInstallmentCount = 120;
    private readonly List<FinancialCreditCardInstallment> installments = [];
    private FinancialCreditCardPurchase() { Description = string.Empty; }
    public Guid FamilyId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid CategoryId { get; private init; }
    public string Description { get; private init; }
    public DateOnly PurchaseDate { get; private init; }
    public decimal TotalAmount { get; private init; }
    public int InstallmentCount { get; private init; }
    public CreditCardPurchaseStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedByUserId { get; private init; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public IReadOnlyCollection<FinancialCreditCardInstallment> Installments => installments;

    public static FinancialCreditCardPurchase Create(Guid familyId, Guid cardId, Guid categoryId, string description, DateOnly date, decimal total, int count, Guid userId, DateTimeOffset now)
    {
        var purchase = new FinancialCreditCardPurchase { FamilyId = familyId, CreditCardId = cardId, CategoryId = categoryId, Description = description, PurchaseDate = date, TotalAmount = total, InstallmentCount = count, Status = CreditCardPurchaseStatus.Active, CreatedAt = now, UpdatedAt = now, CreatedByUserId = userId, UpdatedByUserId = userId };
        var totalCents = decimal.ToInt64(total * 100m);
        var baseCents = totalCents / count;
        var remainder = totalCents % count;
        for (var number = 1; number <= count; number++) purchase.installments.Add(FinancialCreditCardInstallment.Create(familyId, purchase.Id, number, baseCents + (number <= remainder ? 1 : 0), now));
        return purchase;
    }

    public void Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status == CreditCardPurchaseStatus.Cancelled) return;
        Status = CreditCardPurchaseStatus.Cancelled; CancelledAt = now; CancelledByUserId = userId; UpdatedAt = now; UpdatedByUserId = userId;
        foreach (var installment in installments) installment.Cancel();
    }
}

public sealed class FinancialCreditCardInstallment : Entity
{
    private FinancialCreditCardInstallment() { }
    public Guid FamilyId { get; private init; }
    public Guid PurchaseId { get; private init; }
    public Guid? InvoiceId { get; private set; }
    public int InstallmentNumber { get; private init; }
    public decimal Amount { get; private init; }
    public CreditCardInstallmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    internal static FinancialCreditCardInstallment Create(Guid familyId, Guid purchaseId, int number, long cents, DateTimeOffset now) => new() { FamilyId = familyId, PurchaseId = purchaseId, InstallmentNumber = number, Amount = cents / 100m, Status = CreditCardInstallmentStatus.Active, CreatedAt = now };
    internal void Cancel() => Status = CreditCardInstallmentStatus.Cancelled;
    public void AssignInvoice(Guid invoiceId) => InvoiceId = invoiceId;
}

public static class CreditCardPurchaseDetails
{
    public static string? Validate(string? description, decimal? total, int? count, out string? normalizedDescription)
    {
        normalizedDescription = description?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDescription)) return "Informe a descrição da compra.";
        if (normalizedDescription.Length > FinancialCreditCardPurchase.DescriptionMaxLength) return $"A descrição deve ter no máximo {FinancialCreditCardPurchase.DescriptionMaxLength} caracteres.";
        if (total is null || total <= 0) return "O valor total deve ser maior que zero.";
        if (Money.Validate(total.Value, "O valor total") is { } moneyError) return moneyError;
        if (count is null or < 1 or > FinancialCreditCardPurchase.MaxInstallmentCount) return $"A quantidade de parcelas deve estar entre 1 e {FinancialCreditCardPurchase.MaxInstallmentCount}.";
        if (decimal.ToInt64(total.Value * 100m) < count.Value) return "O valor não permite uma parcela positiva para todas as parcelas.";
        return null;
    }
}
