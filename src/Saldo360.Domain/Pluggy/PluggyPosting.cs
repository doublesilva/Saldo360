using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Pluggy;

/// <summary>O que a transação importada virou no núcleo financeiro.</summary>
public enum PluggyPostingKind
{
    /// <summary>Ainda nada (aguardando efetivação, em revisão ou ignorada).</summary>
    None,
    /// <summary>Lançamento de receita ou despesa em conta bancária.</summary>
    BankMovement,
    /// <summary>Uma das duas pontas de uma transferência entre contas integradas.</summary>
    Transfer,
    /// <summary>Compra (ou parcela) de cartão em uma fatura.</summary>
    CardPurchase,
    /// <summary>Pagamento de fatura: a ponta bancária gera o pagamento; a ponta do cartão só o evidencia.</summary>
    CardBillPayment,
}

public enum PluggyPostingState
{
    /// <summary>PENDING na Pluggy (ou data futura): nada entra no realizado até ser efetivada.</summary>
    WaitingPosted,
    /// <summary>Gerou (ou atualizou) o registro no núcleo financeiro.</summary>
    Applied,
    /// <summary>Sem classificação segura: não entra em receitas, despesas nem saldo até o usuário decidir.</summary>
    NeedsReview,
    /// <summary>Evidência de outro registro (ponta do cartão de um pagamento de fatura): não gera movimento próprio.</summary>
    Covered,
    /// <summary>O usuário decidiu não contabilizar (ou cancelou o registro gerado).</summary>
    Ignored,
}

/// <summary>
/// Origem da categoria do lançamento importado. <c>Mapped</c> é o valor legado da T09.3 (categoria da Pluggy casada por nome na criação) e
/// equivale a <c>Pluggy</c>; <c>Rule</c> é regra determinística local; <c>AI</c> é o fallback de IA; <c>User</c> é escolha manual.
/// </summary>
public enum PluggyCategorySource { None, Mapped, User, Pluggy, Rule, AI }

/// <summary>Decisão do usuário para um item em revisão.</summary>
public enum PluggyResolution { AsIncome, AsExpense, AsCardPurchase, Ignore }

/// <summary>
/// Rastro entre uma <see cref="PluggyImportedTransaction"/> (dado de origem, nunca alterado pelo usuário) e o que ela gerou no
/// núcleo financeiro. Uma linha por transação importada. Reprocessar é idempotente: a unicidade por transação importada e a
/// consulta ao estado aqui guardado impedem duplicar lançamentos.
/// </summary>
public sealed class PluggyPosting : Entity
{
    public const int ReasonMaxLength = 64;
    private PluggyPosting() { }

    public Guid WorkspaceId { get; private init; }
    public Guid ImportedTransactionId { get; private init; }
    public Guid AccountLinkId { get; private init; }
    public PluggyPostingState State { get; private set; }
    public PluggyPostingKind Kind { get; private set; }
    /// <summary>Motivo curto e estável (código), não texto livre: da revisão, da espera ou de ser ignorada.</summary>
    public string? Reason { get; private set; }
    public Guid? FinancialTransactionId { get; private set; }
    public Guid? FinancialTransferId { get; private set; }
    public Guid? CreditCardPurchaseId { get; private set; }
    public Guid? CreditCardInvoicePaymentId { get; private set; }
    /// <summary>Posting da outra ponta (transferência ou pagamento de fatura) quando a correspondência foi confirmada.</summary>
    public Guid? CounterpartPostingId { get; private set; }
    /// <summary>Decisão manual do usuário para este item; prevalece sobre a classificação automática.</summary>
    public PluggyResolution? Resolution { get; private set; }
    /// <summary>Categoria escolhida pelo usuário ou mapeada na criação. Nunca é sobrescrita por sincronizações.</summary>
    public Guid? CategoryId { get; private set; }
    public PluggyCategorySource CategorySource { get; private set; }
    /// <summary>Quando a classificação automática foi avaliada pela última vez (inclusive quando não houve categoria segura).</summary>
    public DateTimeOffset? CategoryClassifiedAt { get; private set; }
    /// <summary>Versão do método (regra, catálogo ou modelo + prompt) que produziu a avaliação. Nunca guarda prompt nem resposta bruta.</summary>
    public string? CategoryMethodVersion { get; private set; }
    /// <summary>Justificativa curta e saneada (sem dados bancários).</summary>
    public string? CategoryReason { get; private set; }
    /// <summary>A avaliação automática não chegou a uma categoria segura: o usuário precisa decidir.</summary>
    public bool CategoryNeedsReview { get; private set; }
    /// <summary>Dados com que o registro foi gerado: só uma mudança nestes valores atualiza o núcleo.</summary>
    public decimal? AppliedAmount { get; private set; }
    public DateOnly? AppliedDate { get; private set; }
    public CategoryType? AppliedType { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public Guid? ResolvedByUserId { get; private set; }

    public static PluggyPosting Create(Guid workspaceId, Guid importedTransactionId, Guid accountLinkId, DateTimeOffset now) => new()
    {
        WorkspaceId = workspaceId, ImportedTransactionId = importedTransactionId, AccountLinkId = accountLinkId,
        State = PluggyPostingState.WaitingPosted, Kind = PluggyPostingKind.None, CreatedAt = now, UpdatedAt = now,
    };

    public void Wait(string reason, DateTimeOffset now) => Move(PluggyPostingState.WaitingPosted, PluggyPostingKind.None, reason, now);

    public void Review(string reason, DateTimeOffset now) => Move(PluggyPostingState.NeedsReview, PluggyPostingKind.None, reason, now);

    public void Cover(Guid counterpartPostingId, DateTimeOffset now)
    {
        Move(PluggyPostingState.Covered, PluggyPostingKind.CardBillPayment, "CardBillPaymentEvidence", now);
        CounterpartPostingId = counterpartPostingId;
    }

    public void Ignore(string reason, Guid? userId, DateTimeOffset now)
    {
        Move(PluggyPostingState.Ignored, PluggyPostingKind.None, reason, now);
        if (userId is not null) { ResolvedAt = now; ResolvedByUserId = userId; }
    }

    public void ApplyBankMovement(Guid financialTransactionId, decimal amount, DateOnly date, CategoryType type, DateTimeOffset now)
    {
        Move(PluggyPostingState.Applied, PluggyPostingKind.BankMovement, null, now);
        FinancialTransactionId = financialTransactionId; FinancialTransferId = null; CreditCardPurchaseId = null; CreditCardInvoicePaymentId = null;
        Snapshot(amount, date, type);
    }

    public void ApplyTransfer(Guid financialTransferId, Guid counterpartPostingId, decimal amount, DateOnly date, DateTimeOffset now)
    {
        Move(PluggyPostingState.Applied, PluggyPostingKind.Transfer, null, now);
        FinancialTransactionId = null; CreditCardPurchaseId = null; CreditCardInvoicePaymentId = null;
        FinancialTransferId = financialTransferId; CounterpartPostingId = counterpartPostingId;
        Snapshot(amount, date, null);
    }

    public void ApplyCardPurchase(Guid purchaseId, decimal amount, DateOnly date, DateTimeOffset now)
    {
        Move(PluggyPostingState.Applied, PluggyPostingKind.CardPurchase, null, now);
        FinancialTransactionId = null; FinancialTransferId = null; CreditCardInvoicePaymentId = null;
        CreditCardPurchaseId = purchaseId;
        Snapshot(amount, date, CategoryType.Expense);
    }

    public void ApplyBillPayment(Guid financialTransactionId, Guid invoicePaymentId, Guid counterpartPostingId, decimal amount, DateOnly date, DateTimeOffset now)
    {
        Move(PluggyPostingState.Applied, PluggyPostingKind.CardBillPayment, null, now);
        FinancialTransferId = null; CreditCardPurchaseId = null;
        FinancialTransactionId = financialTransactionId; CreditCardInvoicePaymentId = invoicePaymentId; CounterpartPostingId = counterpartPostingId;
        Snapshot(amount, date, CategoryType.Expense);
    }

    /// <summary>Atualiza o retrato aplicado depois de o núcleo ter sido atualizado pela mudança de origem.</summary>
    public void Resnapshot(decimal amount, DateOnly date, DateTimeOffset now)
    {
        AppliedAmount = amount; AppliedDate = date; UpdatedAt = now;
    }

    public void Resolve(PluggyResolution resolution, Guid userId, DateTimeOffset now)
    {
        Resolution = resolution;
        ResolvedAt = now; ResolvedByUserId = userId; UpdatedAt = now;
    }

    public void SetCategory(Guid? categoryId, PluggyCategorySource source, DateTimeOffset now)
    {
        CategoryId = categoryId; CategorySource = categoryId is null ? PluggyCategorySource.None : source; UpdatedAt = now;
        if (source == PluggyCategorySource.User) { CategoryNeedsReview = false; CategoryReason = null; CategoryMethodVersion = null; CategoryClassifiedAt = now; }
    }

    public const int CategoryMethodMaxLength = 64;
    public const int CategoryReasonMaxLength = 200;

    /// <summary>
    /// Registra o resultado da classificação automática (regra, catálogo Pluggy ou IA) com a auditoria mínima. Sem categoria, guarda só que
    /// a avaliação ocorreu (e se precisa de revisão), para não reenviar à IA o que já foi tentado. Escolha do usuário nunca é sobrescrita.
    /// </summary>
    public void RecordClassification(Guid? categoryId, PluggyCategorySource source, string method, string? reason, bool needsReview, DateTimeOffset now)
    {
        if (CategorySource == PluggyCategorySource.User) return;
        if (source is PluggyCategorySource.None or PluggyCategorySource.User) throw new ArgumentOutOfRangeException(nameof(source));
        CategoryId = categoryId;
        CategorySource = categoryId is null ? PluggyCategorySource.None : source;
        CategoryClassifiedAt = now; UpdatedAt = now;
        CategoryNeedsReview = needsReview;
        CategoryMethodVersion = method.Length > CategoryMethodMaxLength ? method[..CategoryMethodMaxLength] : method;
        CategoryReason = reason is null ? null : reason.Length > CategoryReasonMaxLength ? reason[..CategoryReasonMaxLength] : reason;
    }

    private void Move(PluggyPostingState state, PluggyPostingKind kind, string? reason, DateTimeOffset now)
    {
        State = state; Kind = kind; UpdatedAt = now;
        Reason = reason is null ? null : reason.Length > ReasonMaxLength ? reason[..ReasonMaxLength] : reason;
        if (state is PluggyPostingState.WaitingPosted or PluggyPostingState.NeedsReview or PluggyPostingState.Ignored or PluggyPostingState.Covered)
        {
            FinancialTransactionId = null; FinancialTransferId = null; CreditCardPurchaseId = null; CreditCardInvoicePaymentId = null;
            if (state != PluggyPostingState.Covered) CounterpartPostingId = null;
            AppliedAmount = null; AppliedDate = null; AppliedType = null;
        }
    }

    private void Snapshot(decimal amount, DateOnly date, CategoryType? type) { AppliedAmount = amount; AppliedDate = date; AppliedType = type; }
}
