using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

// ---- Contratos (T09.3). Nenhum tipo aqui expõe o formato da Pluggy ao núcleo financeiro nem ao React. --------------------

/// <summary>Conta externa disponível para vínculo, com sugestões para criar a conta/cartão do Saldo360. Número sempre mascarado.</summary>
public sealed record PluggyAvailableAccountView(string PluggyAccountId, string Kind, string DisplayName, string? MaskedNumber, string? PluggyType,
    string? PluggySubtype, string? SuggestedAccountType, bool SubtypeRecognized, string? Balance, string? Currency, PluggyCardSuggestion? Card,
    Guid? LinkedLinkId, bool Linkable, string? NotLinkableReason);

public sealed record PluggyCardSuggestion(int? ClosingDay, int? DueDay, string? CreditLimit, string? AvailableCreditLimit, string? Brand);

/// <summary>Valores informados pelo banco, sempre separados do que o Saldo360 calcula. Textos decimais exatos.</summary>
public sealed record PluggyObservedView(string? Balance, DateTimeOffset? ObservedAt, string? OverdraftContractedLimit, string? OverdraftUsedLimit,
    string? CreditLimit, string? AvailableCreditLimit);

public sealed record PluggyPostingCounts(int Applied, int Waiting, int NeedsReview, int Covered, int Ignored);

public sealed record PluggyAccountLinkView(Guid Id, Guid ConnectionId, string Origin, string Kind, Guid? FinancialAccountId, Guid? CreditCardId, string DisplayName,
    string? InstitutionName, string? PluggyType, string? PluggySubtype, string Status, DateOnly? ImportFrom, DateTimeOffset LinkedAt, DateTimeOffset? LastSyncedAt,
    PluggyObservedView Observed, PluggyPostingCounts Postings);

/// <summary>
/// Saldo observado × calculado de uma conta integrada. O calculado só existe com saldo inicial configurado (regra da T06.3); a diferença só
/// é apresentada quando os dois existem. Cartões não somam saldo de caixa: aqui aparecem limite e saldo devedor, cada um com a sua origem.
/// </summary>
public sealed record PluggyReconciliationView(string? ObservedBalance, DateTimeOffset? ObservedAt, string? CalculatedBalance, DateOnly? CalculatedAsOf,
    string? Difference, string? CalculatedUnavailableReason, int ReviewCount, int WaitingCount, string? SuggestedOpeningBalance, DateOnly? SuggestedOpeningBalanceDate,
    string? SuggestionUnavailableReason, PluggyCardFiguresView? Card, IReadOnlyList<string> Notes);

public sealed record PluggyCardFiguresView(string? CreditLimit, string? UsedLimit, string? AvailableLimit, string? ObservedBalanceDue, string? ObservedAvailableLimit,
    IReadOnlyList<PluggyBillReconciliationView> Bills)
{
    /// <summary>Dia habitual cadastrado no cartão e o sugerido pelo histórico de faturas do banco (quando consistente). Só é alterado com confirmação.</summary>
    public int? CurrentClosingDay { get; init; }
    public int? CurrentDueDay { get; init; }
    public int? SuggestedClosingDay { get; init; }
    public int? SuggestedDueDay { get; init; }
    public string? ClosingScheduleReason { get; init; }
    public string? DueScheduleReason { get; init; }
}

/// <summary>
/// Fatura do banco × fatura do Saldo360, em três dimensões independentes. <c>Cycle</c>: Open ou Closed (fechada pela instituição: a data de
/// fechamento informada já chegou e há total). <c>Payment</c>: Unpaid, PartiallyPaid ou Paid, pelo maior entre o que o Saldo360 registrou e o que o
/// banco informou em <c>payments[]</c>. <c>Reconciliation</c>: NotVerified, Reconciled, Divergent ou Incomplete (total bancário × o que foi importado).
/// Uma não depende da outra: fatura fechada pode estar divergente e paga.
/// </summary>
public sealed record PluggyBillReconciliationView(DateOnly? ClosingDate, DateOnly? DueDate, string? BankTotal, string? Saldo360Total, string? Difference,
    string Cycle, string Payment, string Reconciliation, string? ReconciliationReason, string? PaidAmount, string? BankReportedPaid, string? FinanceCharges, string? MinimumPayment);

/// <summary>Pagamento informado pelo banco dentro da fatura (<c>payments[]</c>).</summary>
public sealed record PluggyBillPaymentFact(decimal Amount, DateOnly Date, string? Mode, string? ValueType);

/// <summary>Fatura informada pelo banco, já normalizada (datas de negócio, total, encargos e pagamentos informados).</summary>
public sealed record PluggyBillFact(string BillId, DateOnly? ClosingDate, DateOnly? DueDate, decimal? Total, string? Currency)
{
    public decimal? FinanceChargesTotal { get; init; }
    public decimal? MinimumPayment { get; init; }
    public IReadOnlyList<PluggyBillPaymentFact> Payments { get; init; } = [];
}

/// <summary>Fatura do banco com o que o Saldo360 sabe dela: conciliação persistida, componentes importados, pagamentos registrados e informados.</summary>
public sealed record PluggyBillStateRow(PluggyBillFact Bill, PluggyBillReconciliation Reconciliation, string? ReconciliationReason, decimal? ComponentsTotal,
    bool InvoiceExists, decimal RecordedPaid, decimal ReportedPaid);

public sealed record PluggyAccountLinkDetailView(PluggyAccountLinkView Link, PluggyReconciliationView Reconciliation);

public sealed record PluggyReviewItemView(Guid PostingId, Guid ImportedTransactionId, Guid LinkId, string LinkDisplayName, string LinkKind, string State, string? Reason,
    DateOnly? Date, string? Amount, string? Direction, string Description, string? PluggyCategory, string PluggyStatus, string? OperationType,
    IReadOnlyList<string> AllowedResolutions, string? Resolution)
{
    /// <summary>Já contabilizado: o movimento já gerou efeito nos totais e saldos (ponta aplicada ou evidência coberta). Falso = fora dos totais até a decisão.</summary>
    public bool Accounted { get; init; }
    /// <summary>Efeito atual nos totais: <c>InTotals</c> (já conta) ou <c>OutOfTotals</c> (não conta até a decisão). Independe da categoria.</summary>
    public string Impact { get; init; } = "OutOfTotals";
    /// <summary><c>Evidence</c> = revisão financeira da evidência ainda não aplicada; <c>Link</c> = revisão do vínculo entre movimentos (transferência).</summary>
    public string ReviewKind { get; init; } = "Evidence";
    /// <summary>Dúvida de vínculo desta ponta (contraparte ambígua, datas diferentes...), inclusive quando ela já está contabilizada.</summary>
    public string? LinkReviewReason { get; init; }
    /// <summary><c>Inferred</c> (regra automática) ou <c>UserConfirmed</c>, quando a ponta já é uma transferência aplicada.</summary>
    public string? LinkOrigin { get; init; }
    public string? LinkRuleVersion { get; init; }
    public Guid? CounterpartPostingId { get; init; }
    public bool CanUndoLink { get; init; }
    /// <summary>Possíveis contrapartes (revisão de vínculo): outras pontas de mesmo valor em outra conta, com o motivo quando não podem ser confirmadas.</summary>
    public IReadOnlyList<PluggyTransferCandidateView> Candidates { get; init; } = [];
}

/// <summary>Possível contraparte de uma transferência. <c>CanConfirm</c> só é verdadeiro quando as regras de elegibilidade (mesmo dia, operação, contas distintas...) são atendidas.</summary>
public sealed record PluggyTransferCandidateView(Guid PostingId, Guid LinkId, string LinkDisplayName, DateOnly Date, string Amount, string Direction, string Description,
    string? OperationType, bool Accounted, bool SameDay, bool CanConfirm, string? BlockedReason);

public sealed record PluggyReviewPage(IReadOnlyList<PluggyReviewItemView> Items, int Total, int Page, int PageSize, PluggyPostingCounts Counts)
{
    /// <summary>Pontas já contabilizadas que aparecem na revisão por dúvida de vínculo (continuam nos totais até a decisão).</summary>
    public int AccountedInReview { get; init; }
}

public enum PluggyManualOutcome { Done, AlreadyDone, NotFound, Invalid, Conflict }

/// <summary>Resultado de uma ação manual sobre vínculos. <c>Reason</c> é um código estável (ver <see cref="PluggyTransferEligibility.Reasons"/>).</summary>
public sealed record PluggyManualResult(PluggyManualOutcome Outcome, string? Reason = null, PluggyApplySummary? Summary = null);

// ---- Prévia de recuperação (somente leitura: executa o motor numa transação que é sempre desfeita) -------------------------

public sealed record PluggyRecoveryFilter(decimal? Amount, DateOnly? From, DateOnly? To, int Take = 100);

public sealed record PluggyInterpretation(string State, string Kind, string? Reason, string? Effect);

/// <summary>
/// Um movimento importado e a cadeia até o efeito: interpretação atual x proposta pelo motor atual. <c>Outcome</c>: <c>Unchanged</c>, <c>WillChange</c>,
/// <c>NeedsDecision</c> (o motor preservaria o efeito aplicado, mas a regra atual leria diferente: exige decisão manual) ou <c>NotAccounted</c> (sem posting).
/// </summary>
public sealed record PluggyRecoveryItem(Guid? PostingId, Guid ImportedTransactionId, Guid LinkId, string LinkDisplayName, DateOnly? Date, string? Amount, string? Direction,
    string Description, string? OperationType, string PluggyStatus, PluggyInterpretation Current, PluggyInterpretation Proposed, string Outcome, string? Note);

public sealed record PluggyFigures(string Income, string Expense, string TransfersIn, string TransfersOut, string Net);

public sealed record PluggyAccountDelta(Guid LinkId, string LinkDisplayName, PluggyFigures Before, PluggyFigures After);

public sealed record PluggyRecoveryPreview(DateTimeOffset GeneratedAt, PluggyApplySummary WouldApply, IReadOnlyList<PluggyAccountDelta> Accounts, PluggyFigures TotalBefore, PluggyFigures TotalAfter,
    IReadOnlyList<PluggyRecoveryItem> Items, int ItemsTotal, int WillChange, int NeedsDecision)
{
    /// <summary>Sempre falso: a prévia nunca persiste nada.</summary>
    public bool Persisted => false;
}

public sealed record PluggyApplySummary(int Created, int Updated, int Waiting, int NeedsReview, int Covered, int TransfersPaired, int BillPaymentsPaired,
    int OutOfRange, int Unchanged)
{
    /// <summary>Compras reposicionadas na fatura real do banco (em vez da fatura calculada pelo dia de fechamento configurado).</summary>
    public int PurchasesRehomed { get; init; }
    /// <summary>Estornos, devoluções e cashback aplicados como crédito da fatura (nunca como receita).</summary>
    public int CardCreditsApplied { get; init; }
    /// <summary>Faturas em que o fechamento informado pela instituição foi registrado nesta execução.</summary>
    public int InvoicesClosedByInstitution { get; init; }
    /// <summary>Faturas calculadas por calendário, sem nenhum componente, removidas depois de as compras irem para a fatura real do banco.</summary>
    public int StaleInvoicesRemoved { get; init; }
    public int BillsEvaluated { get; init; }
    public int BalanceAnchorsUpdated { get; init; }
}

// ---- Entradas ---------------------------------------------------------------------------------------------------------

public static class PluggyLinkModes { public const string CreateNew = "CreateNew"; public const string LinkExisting = "LinkExisting"; }

/// <summary>Pedido de vínculo. O <c>PluggyAccountId</c> nunca é confiado: é conferido contra as contas do Item no backend.</summary>
public sealed record PluggyLinkRequest(Guid? ConnectionId, string? PluggyAccountId, string? Mode, Guid? ExistingAccountId, Guid? ExistingCardId, string? Name,
    string? FinancialScope, Guid? ResponsibleUserId, string? AccountType, DateOnly? ImportFrom, int? ClosingDay, int? DueDay, decimal? CreditLimit,
    Guid? PaymentAccountId);

public sealed record PluggyLinkPlan(Guid ConnectionId, string PluggyAccountId, PluggyLinkedAccountKind Kind, string DisplayName, string? InstitutionName, string? PluggyType,
    string? PluggySubtype, DateOnly? ImportFrom, PluggyObservedFigures Observed, AccountDetails? NewAccount, CreditCardDetails? NewCard, Guid? ExistingAccountId, Guid? ExistingCardId);

public enum PluggyLinkOutcome { Created, DuplicateExternal, DuplicateTarget, TargetNotFound, TargetInactive, Failed }

public sealed record PluggyLinkCreation(PluggyLinkOutcome Outcome, PluggyAccountLink? Link = null);

public sealed record PluggyReviewFilter(Guid? LinkId, int Page, int PageSize);

/// <summary>Rastro de uma transação em revisão (para resolver).</summary>
public sealed record PluggyReviewTarget(Guid PostingId, PluggyPostingState State, string? Type, PluggyLinkedAccountKind LinkKind);

// ---- Persistência de vínculos e rastros -------------------------------------------------------------------------------

public interface IPluggyFinancialRepository
{
    Task<IReadOnlyList<PluggyAccountLink>> ListLinksAsync(Guid workspaceId, Guid? connectionId, CancellationToken ct);
    Task<PluggyAccountLink?> FindLinkAsync(Guid workspaceId, Guid linkId, CancellationToken ct);
    Task<PluggyAccountLink?> FindLinkByExternalAsync(Guid workspaceId, Guid connectionId, string pluggyAccountId, CancellationToken ct);
    /// <summary>Cria o vínculo (e, se pedido, a conta/cartão do Saldo360) numa única transação, com unicidade garantida pelo banco.</summary>
    Task<PluggyLinkCreation> CreateLinkAsync(Guid workspaceId, Guid userId, PluggyLinkPlan plan, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, PluggyPostingCounts>> CountPostingsAsync(Guid workspaceId, CancellationToken ct);
    /// <summary>Atualiza valores observados e a situação das contas integradas do Item, a partir das contas devolvidas pela Pluggy.</summary>
    Task RefreshObservedAsync(Guid workspaceId, Guid connectionId, IReadOnlyDictionary<string, PluggyObservedFigures> figures, PluggyAccountLinkStatus status, CancellationToken ct);
    /// <summary>Guarda (insere ou atualiza) as faturas informadas pelo banco para um cartão integrado.</summary>
    Task UpsertBillsAsync(Guid workspaceId, Guid linkId, IReadOnlyList<PluggyBillFact> bills, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<PluggyBillFact>> ListBillsAsync(Guid workspaceId, Guid linkId, CancellationToken ct);
    /// <summary>Faturas do banco (mais recentes primeiro) com conciliação, componentes e pagamentos, para a tela de integração do cartão.</summary>
    Task<IReadOnlyList<PluggyBillStateRow>> ListBillStatesAsync(Guid workspaceId, Guid linkId, int take, CancellationToken ct);
    /// <summary>Registra a data da última coleta completa de uma conta integrada.</summary>
    Task MarkSyncedAsync(Guid workspaceId, Guid connectionId, string pluggyAccountId, DateTimeOffset now, CancellationToken ct);
    Task SetConnectionStatusAsync(Guid workspaceId, Guid connectionId, PluggyAccountLinkStatus status, CancellationToken ct);
    Task<PluggyReviewPage> ListReviewAsync(Guid workspaceId, PluggyReviewFilter filter, CancellationToken ct);
    Task<PluggyReviewTarget?> FindReviewTargetAsync(Guid workspaceId, Guid postingId, CancellationToken ct);
    Task SaveResolutionAsync(Guid workspaceId, Guid postingId, PluggyResolution resolution, Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<PluggyReviewItemView?> GetReviewItemAsync(Guid workspaceId, Guid postingId, CancellationToken ct);
    Task<IReadOnlyList<MappableCategory>> ListCategoriesAsync(Guid workspaceId, CancellationToken ct);
    /// <summary>Todas as pontas bancárias do orçamento (com a decisão do mapper) para listar possíveis contrapartes de uma transferência.</summary>
    Task<IReadOnlyList<PluggyTransferPoolRow>> ListTransferPoolAsync(Guid workspaceId, CancellationToken ct);
    /// <summary>Pares que o usuário rejeitou (ordem canônica), para a revisão não os oferecer como confirmáveis.</summary>
    Task<IReadOnlySet<(Guid First, Guid Second)>> ListRejectedPairsAsync(Guid workspaceId, CancellationToken ct);
    /// <summary>Data do movimento aplicado mais antigo da conta integrada (base do histórico para sugerir o saldo inicial).</summary>
    Task<DateOnly?> EarliestAppliedDateAsync(Guid workspaceId, Guid linkId, CancellationToken ct);
}

/// <summary>Ponta bancária importada e o seu rastro, para a lista de contrapartes.</summary>
public sealed record PluggyTransferPoolRow(ImportedFacts Facts, Guid PostingId, Guid? FinancialAccountId, string LinkDisplayName, PluggyPostingState State, PluggyPostingKind Kind,
    Guid? CounterpartPostingId, PluggyResolution? Resolution);

/// <summary>Transforma as transações importadas em movimentos do núcleo financeiro. Idempotente, serializado por orçamento.</summary>
public interface IPluggyPostingEngine
{
    Task<PluggyApplySummary> ApplyAsync(Guid workspaceId, Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Confirma manualmente que duas pontas (identificadas por seus rastros) formam uma transferência. Revalida tudo dentro da transação, com o orçamento travado:
    /// repetir é idempotente e duas confirmações concorrentes pela mesma ponta resultam em conflito controlado para a perdedora.
    /// </summary>
    Task<PluggyManualResult> ConfirmTransferAsync(Guid workspaceId, Guid userId, Guid postingId, Guid counterpartPostingId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Rejeita a sugestão de par: o motor não volta a pareá-lo. Não altera nenhum efeito financeiro já aplicado.</summary>
    Task<PluggyManualResult> RejectTransferAsync(Guid workspaceId, Guid userId, Guid postingId, Guid counterpartPostingId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Desfaz uma transferência aplicada (a inferida ou a confirmada): cancela a transferência, preserva os movimentos reais e impede o motor de recriar o mesmo par.</summary>
    Task<PluggyManualResult> UndoTransferAsync(Guid workspaceId, Guid userId, Guid postingId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Prévia do reprocessamento: roda o motor numa transação sempre desfeita e compara antes e depois. Nunca persiste.</summary>
    Task<PluggyRecoveryPreview> PreviewAsync(Guid workspaceId, Guid userId, DateTimeOffset now, PluggyRecoveryFilter filter, CancellationToken ct);
}
