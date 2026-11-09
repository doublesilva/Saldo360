namespace Saldo360.Application.CreditCards.Projection;

// T09.4: modelo único de obrigações dos cartões. Tudo aqui é puro (sem banco, rede ou relógio): os fatos entram, as projeções saem.
// Nada disto é gravado como lançamento: faturas projetadas, parcelas conhecidas e estimadas são só leitura.

/// <summary>De onde vem a parcela: já está numa fatura, foi devolvida pelo banco mas ainda não faturada, ou foi estimada.</summary>
public enum InstallmentOrigin { InInvoice, Known, Estimated }

/// <summary>Fase do ciclo da fatura: fechada pelo banco, a aberta (próxima a fechar) ou futura.</summary>
public enum InvoicePhase { Closed, Open, Future }

/// <summary>De onde sai o valor da obrigação: total do banco (confirmado), projeção da fatura aberta ou projeção de fatura futura.</summary>
public enum ObligationBasis { BankTotal, ProjectedOpen, ProjectedFuture }

public enum InvoicePaymentState { Unpaid, Partial, Paid, Overpaid }

public enum ChainStatus { Complete, Estimated, Ambiguous, Interrupted, InsufficientEvidence, GapDetected }

public enum ObligationKind { Invoice, KnownImported }

/// <summary>Parcela devolvida pela instituição (numero/total) que pertence a uma compra parcelada do cartão.</summary>
public sealed record InstallmentObservation(
    string SourceId, string Description, decimal Amount, int Number, int Total, DateOnly Date,
    bool InInvoice, DateOnly? BillClosingDate, DateOnly? BillDueDate, bool Financing = false);

/// <summary>Compra à vista ainda pendente (não faturada): entra na fatura aberta como valor provisório.</summary>
public sealed record PendingPurchaseFact(string SourceId, string Description, decimal Amount, DateOnly Date);

/// <summary>Obrigação conhecida importada sem fatura (extrato histórico); vence na data informada, se houver.</summary>
public sealed record KnownImportedFact(Guid Id, decimal Amount, DateOnly? DueDate);
/// <summary>Cobrança recorrente de cartão. <c>Manual</c> = cadastrada pelo usuário (identidade lógica = recorrência + sequência; valor fixo é comprometido); detectada pelo Pluggy é sempre estimativa.</summary>
public sealed record RecurringChargeFact(Guid RecurrenceId, string Description, decimal Amount, DateOnly ChargeDate, bool IsEstimated, bool Manual = false, int? Sequence = null, Guid? CategoryId = null);

/// <summary>Uma fatura persistida do Saldo360, com a composição local, os pagamentos e o que o banco informou.</summary>
public sealed record CardInvoiceFact(
    Guid InvoiceId, DateOnly CycleStart, DateOnly Closing, DateOnly Due, string Competence,
    bool ClosedByBank, decimal? ConfirmedTotal, string? ConfirmedSource, decimal Composition, decimal Paid,
    string Reconciliation, string? ReconciliationReason, decimal? BankReportedPaid, decimal Financed = 0m);

public sealed record CardInfo(
    Guid Id, string Name, string Issuer, string? LastFour, string? Scope, string Origin, int ClosingDay, int DueDay,
    decimal CreditLimit, decimal AvailableLimit, string LimitSource, Guid? PayerAccountId, string? PayerName, string? PayerScope, bool PayerIsPrimary,
    string SyncStatus, DateTimeOffset? LastSyncedAt, bool IsArchived = false);

/// <summary>Tudo o que o motor precisa sobre um cartão. Os fatos vêm do banco já filtrados pelo orçamento.</summary>
public sealed record CardFacts(
    CardInfo Card, IReadOnlyList<Saldo360.Application.Pluggy.PluggyBillFact> Bills, IReadOnlyList<CardInvoiceFact> Invoices,
    IReadOnlyList<PendingPurchaseFact> Pending, IReadOnlyList<InstallmentObservation> Installments, IReadOnlyList<KnownImportedFact> KnownImported,
    IReadOnlyList<RecurringChargeFact>? Recurring = null,
    /// <summary>Categoria única já conhecida de cada compra parcelada (chave "descrição-base|total"): as parcelas projetadas apenas a herdam, nada é gravado por ocorrência.</summary>
    IReadOnlyDictionary<string, Guid>? SeriesCategories = null);

public sealed record ProjectedInstallment(
    int Number, int Total, decimal Amount, DateOnly Closing, DateOnly Due, InstallmentOrigin Origin, bool Absorbed, string? SourceId);

/// <summary>Compra parcelada reconstruída a partir das parcelas devolvidas. A estimativa só existe com evidência (periodicidade, valor e ciclo).</summary>
public sealed record InstallmentChain(
    string Key, string Description, int Total, decimal Amount, ChainStatus Status, string? Reason,
    IReadOnlyList<ProjectedInstallment> Installments, int Unprojected, bool IsFinancing = false)
{
    public int Estimated => Installments.Count(i => i.Origin == InstallmentOrigin.Estimated);
}

public sealed record CardInvoiceProjection(
    Guid CardId, Guid? InvoiceId, string Competence, DateOnly CycleStart, DateOnly Closing, DateOnly Due,
    InvoicePhase Phase, ObligationBasis Basis, bool Provisional, bool AwaitingBankBill,
    decimal? BankTotal, string? BankTotalSource, decimal Composition, decimal PendingKnown, decimal KnownInstallments, decimal Estimated,
    decimal ProjectedTotal, decimal Obligation, decimal Paid, decimal Remaining, InvoicePaymentState PaymentState,
    string Reconciliation, string? ReconciliationReason, decimal? BankReportedPaid, decimal? Difference,
    int AbsorbedItems, int UnprojectedInstallments, int AmbiguousChains, IReadOnlyList<string> Flags,
    /// <summary>Financiamento de saldo de fatura: obrigação de pagamento (está em <c>ProjectedTotal</c>/<c>Obligation</c>), mas NÃO é consumo — fica fora de <c>Composition</c>, <c>KnownInstallments</c> e <c>Estimated</c>.</summary>
    decimal Financed = 0m);

/// <summary>Obrigação consumida por Agenda, saldo projetado e Dashboard. Uma por fatura, mais uma por obrigação conhecida sem fatura.</summary>
public sealed record CardObligationItem(
    Guid CardId, Guid? PayerAccountId, DateOnly? DueDate, decimal Obligation, decimal Paid, decimal Remaining,
    ObligationKind Kind, InvoicePhase? Phase, Guid? InvoiceId, string? Competence);

public sealed record CardCoverage(bool Complete, IReadOnlyList<string> Reasons);

public sealed record CardProjection(
    CardInfo Card, CardCycleSchedule Schedule, IReadOnlyList<CardInvoiceProjection> Invoices, IReadOnlyList<InstallmentChain> Chains,
    IReadOnlyList<CardObligationItem> Items, CardCoverage Coverage, IReadOnlyList<CardOccurrence>? Occurrences = null);

/// <summary>Ocorrência individual que compõe uma fatura ainda não fechada pelo banco (leitura): parcela conhecida ou estimada, compra pendente ou cobrança recorrente. Nada é gravado.</summary>
public sealed record CardOccurrence(
    Guid CardId, string Kind, string Key, string Description, decimal Amount, DateOnly Date, DateOnly Closing, DateOnly Due, string Competence, int? Number, int? Total, string Quality, Guid? CategoryId = null);

public static class CardCoverageReasons
{
    public const string AwaitingBankBill = "AwaitingBankBill";
    public const string UnprojectedInstallments = "UnprojectedInstallments";
    public const string AmbiguousInstallments = "AmbiguousInstallments";
    public const string WeakSchedule = "WeakSchedule";
    public const string MissingPayerAccount = "MissingPayerAccount";
    public const string KnownWithoutDueDate = "KnownWithoutDueDate";
    public const string SyncAttention = "SyncAttention";
    public const string NoBankHistory = "NoBankHistory";
    /// <summary>Há financiamento de fatura: o banco não discriminou principal e encargos, então a cobertura é parcial (encargos desconhecidos, nunca zero).</summary>
    public const string InvoiceFinancingComposition = "InvoiceFinancingComposition";
}
