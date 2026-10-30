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
    public Guid WorkspaceId { get; private init; }
    public Guid CreditCardId { get; private init; }
    public Guid CategoryId { get; private set; }
    /// <summary>Natureza da despesa da compra; as parcelas a herdam (não têm natureza própria). Compra de cartão é sempre despesa.</summary>
    public ExpenseNature Nature { get; private set; }
    public string Description { get; private set; }
    public DateOnly PurchaseDate { get; private set; }
    public decimal TotalAmount { get; private set; }
    public int InstallmentCount { get; private set; }
    public int PreviouslyPaidInstallments { get; private set; }
    public DateOnly? FirstPendingCompetence { get; private set; }
    public CreditCardPurchaseStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedByUserId { get; private init; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public IReadOnlyCollection<FinancialCreditCardInstallment> Installments => installments;

    public static FinancialCreditCardPurchase Create(Guid workspaceId, Guid cardId, Guid categoryId, string description, DateOnly date, decimal total, int count, Guid userId, DateTimeOffset now, ExpenseNature nature = ExpenseNature.Undefined)
    {
        var purchase = new FinancialCreditCardPurchase { WorkspaceId = workspaceId, CreditCardId = cardId, CategoryId = categoryId, Nature = nature, Description = description, PurchaseDate = date, TotalAmount = total, InstallmentCount = count, Status = CreditCardPurchaseStatus.Active, CreatedAt = now, UpdatedAt = now, CreatedByUserId = userId, UpdatedByUserId = userId };
        var totalCents = decimal.ToInt64(total * 100m);
        var baseCents = totalCents / count;
        var remainder = totalCents % count;
        for (var number = 1; number <= count; number++) purchase.installments.Add(FinancialCreditCardInstallment.Create(workspaceId, purchase.Id, number, baseCents + (number <= remainder ? 1 : 0), now));
        return purchase;
    }

    /// <summary>
    /// Compra vinda de transação de cartão importada da Pluggy (T09.3): uma parcela, sem histórico de parcelas pagas. Cada
    /// transação da Pluggy (inclusive cada parcela de uma compra parcelada) vira uma compra de parcela única, na fatura em que a Pluggy a lançou.
    /// </summary>
    public static FinancialCreditCardPurchase CreateImported(Guid workspaceId, Guid cardId, Guid categoryId, string description, DateOnly date, decimal amount, Guid userId, DateTimeOffset now) =>
        Create(workspaceId, cardId, categoryId, description.Trim(), date, amount, 1, userId, now);

    /// <summary>Corrige apenas a categoria interna (dados de origem intactos).</summary>
    public void Recategorize(Guid categoryId, Guid userId, DateTimeOffset now)
    {
        if (Status != CreditCardPurchaseStatus.Active) throw new InvalidOperationException("Compra cancelada não pode ser editada.");
        CategoryId = categoryId; UpdatedAt = now; UpdatedByUserId = userId;
    }

    public void Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status == CreditCardPurchaseStatus.Cancelled) return;
        Status = CreditCardPurchaseStatus.Cancelled; CancelledAt = now; CancelledByUserId = userId; UpdatedAt = now; UpdatedByUserId = userId;
        foreach (var installment in installments) installment.Cancel();
    }

    public static FinancialCreditCardPurchase CreateOngoing(Guid workspaceId, Guid cardId, Guid categoryId, string description,
        DateOnly date, decimal total, int count, int paid, DateOnly? firstPending, Guid userId, DateTimeOffset now, ExpenseNature nature = ExpenseNature.Undefined)
    {
        ValidateSchedule(description, total, count, paid, firstPending);
        var purchase = Create(workspaceId, cardId, categoryId, description.Trim(), date, total, count, userId, now, nature);
        purchase.PreviouslyPaidInstallments = paid;
        purchase.FirstPendingCompetence = firstPending;
        // Historical payments are metadata, never invoice payments or bank transactions.
        purchase.installments.RemoveAll(i => i.InstallmentNumber <= paid);
        return purchase;
    }

    /// <summary>Corrige só a natureza (fixa/variável/não definida): não toca valor, parcelas, fatura ou categoria. Cancelada não é editável.</summary>
    public void ChangeNature(ExpenseNature nature, Guid userId, DateTimeOffset now)
    {
        if (Status != CreditCardPurchaseStatus.Active) throw new InvalidOperationException("Compra cancelada não pode ser editada.");
        if (Nature == nature) return;
        Nature = nature; UpdatedAt = now; UpdatedByUserId = userId;
    }

    public void UpdateSchedule(Guid categoryId, string description, DateOnly date, decimal total, int count, int paid,
        DateOnly? firstPending, Guid userId, DateTimeOffset now, ExpenseNature? nature = null)
    {
        ValidateSchedule(description, total, count, paid, firstPending);
        if (Status != CreditCardPurchaseStatus.Active) throw new InvalidOperationException("Compra cancelada não pode ser editada.");
        if (paid != PreviouslyPaidInstallments || PreviouslyPaidInstallments > 0 &&
            (total != TotalAmount || count != InstallmentCount))
            throw new InvalidOperationException("A alteração modificaria o histórico de parcelas já pagas.");
        var replacement = CreateOngoing(WorkspaceId, CreditCardId, categoryId, description, date, total, count, paid, firstPending, userId, now);
        foreach (var item in installments) item.Cancel();
        foreach (var next in replacement.Installments)
        {
            var old = installments.SingleOrDefault(i => i.InstallmentNumber == next.InstallmentNumber);
            if (old is null) installments.Add(FinancialCreditCardInstallment.Create(WorkspaceId, Id, next.InstallmentNumber, decimal.ToInt64(next.Amount * 100m), now));
            else old.Revise(next.Amount);
        }
        CategoryId = categoryId; Description = description.Trim(); PurchaseDate = date; TotalAmount = total; if (nature is { } chosen) Nature = chosen;
        InstallmentCount = count; FirstPendingCompetence = firstPending; UpdatedAt = now; UpdatedByUserId = userId;
    }

    private static void ValidateSchedule(string description, decimal total, int count, int paid, DateOnly? competence)
    {
        if (CreditCardPurchaseDetails.Validate(description, total, count, out _) is { } error) throw new ArgumentException(error);
        if (paid < 0 || paid >= count || paid > 0 && competence is null || competence is { Day: not 1 })
            throw new ArgumentException("Informe parcelas pagas entre zero e N-1 e a primeira competência pendente.");
    }
}

public sealed class FinancialCreditCardInstallment : Entity
{
    private FinancialCreditCardInstallment() { }
    public Guid WorkspaceId { get; private init; }
    public Guid PurchaseId { get; private init; }
    public Guid? InvoiceId { get; private set; }
    public int InstallmentNumber { get; private init; }
    public decimal Amount { get; private set; }
    public Guid? OfficialCategoryId { get; private set; }
    public CreditCardInstallmentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    internal static FinancialCreditCardInstallment Create(Guid workspaceId, Guid purchaseId, int number, long cents, DateTimeOffset now) => new() { WorkspaceId = workspaceId, PurchaseId = purchaseId, InstallmentNumber = number, Amount = cents / 100m, Status = CreditCardInstallmentStatus.Active, CreatedAt = now };
    internal void Cancel() => Status = CreditCardInstallmentStatus.Cancelled;
    internal void Revise(decimal amount) { Amount = amount; Status = CreditCardInstallmentStatus.Active; }
    public void AssignInvoice(Guid invoiceId) => InvoiceId = invoiceId;
    public void ApplyOfficial(decimal amount, Guid categoryId)
    {
        if (amount <= 0 || Money.Validate(amount, "Parcela") is not null) throw new ArgumentException("Parcela inválida.");
        Amount = amount; OfficialCategoryId = categoryId; Status = CreditCardInstallmentStatus.Active;
    }
    public void SupersedeOfficial() => Status = CreditCardInstallmentStatus.Cancelled;
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
