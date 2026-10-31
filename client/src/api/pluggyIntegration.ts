import { requestJson } from './apiErrors'
import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { PluggyConnection } from './pluggyPoc'

/**
 * Integração das contas Pluggy ao cadastro financeiro (T09.3). Valores monetários chegam como texto decimal exato: só são
 * formatados no navegador, nunca somados nem convertidos para number. Nenhuma credencial passa por aqui.
 */
export type LinkKind = 'Bank' | 'Card'
export type LinkStatus = 'Active' | 'ConsentExpired' | 'Attention'
export type LinkMode = 'CreateNew' | 'LinkExisting'

export interface CardSuggestion { closingDay: number | null; dueDay: number | null; creditLimit: string | null; availableCreditLimit: string | null; brand: string | null }

export interface AvailableAccount {
  pluggyAccountId: string; kind: LinkKind | 'Unsupported'; displayName: string; maskedNumber: string | null; pluggyType: string | null; pluggySubtype: string | null
  suggestedAccountType: 'Checking' | 'Savings' | null; subtypeRecognized: boolean; balance: string | null; currency: string | null; card: CardSuggestion | null
  linkedLinkId: string | null; linkable: boolean; notLinkableReason: 'UnsupportedType' | 'AlreadyLinked' | null
}

export interface ObservedFigures {
  balance: string | null; observedAt: string | null; overdraftContractedLimit: string | null; overdraftUsedLimit: string | null
  creditLimit: string | null; availableCreditLimit: string | null
}
export interface PostingCounts { applied: number; waiting: number; needsReview: number; covered: number; ignored: number }

export interface AccountLink {
  id: string; connectionId: string; origin: 'Pluggy'; kind: LinkKind; financialAccountId: string | null; creditCardId: string | null; displayName: string
  institutionName: string | null; pluggyType: string | null; pluggySubtype: string | null; status: LinkStatus; importFrom: string | null; linkedAt: string
  lastSyncedAt: string | null; observed: ObservedFigures; postings: PostingCounts
}

/**
 * Fatura do banco em três dimensões independentes (T09.3.2): ciclo (fechada pela instituição quando a data de fechamento informada já chegou),
 * pagamento e conciliação (total bancário × o que foi importado). O total do banco é preservado como veio; a diferença só é informada.
 */
export interface BillReconciliation {
  closingDate: string | null; dueDate: string | null; bankTotal: string | null; saldo360Total: string | null; difference: string | null
  cycle: 'Open' | 'Closed'; payment: 'Unpaid' | 'PartiallyPaid' | 'Paid'; reconciliation: 'NotVerified' | 'Reconciled' | 'Divergent' | 'Incomplete'
  reconciliationReason: string | null; paidAmount: string | null; bankReportedPaid: string | null; financeCharges: string | null; minimumPayment: string | null
}
export interface CardFigures {
  creditLimit: string | null; usedLimit: string | null; availableLimit: string | null; observedBalanceDue: string | null; observedAvailableLimit: string | null
  bills: BillReconciliation[]
  /** Dia habitual cadastrado e o sugerido pelo histórico de faturas do banco (só quando consistente). Alterar exige confirmação. */
  currentClosingDay?: number | null; currentDueDay?: number | null; suggestedClosingDay?: number | null; suggestedDueDay?: number | null
  closingScheduleReason?: string | null; dueScheduleReason?: string | null
}
export interface Reconciliation {
  observedBalance: string | null; observedAt: string | null; calculatedBalance: string | null; calculatedAsOf: string | null; difference: string | null
  calculatedUnavailableReason: string | null; reviewCount: number; waitingCount: number; suggestedOpeningBalance: string | null
  suggestedOpeningBalanceDate: string | null; suggestionUnavailableReason: string | null; card: CardFigures | null; notes: string[]
}
export interface AccountLinkDetail { link: AccountLink; reconciliation: Reconciliation }

export interface LinkRequest {
  connectionId: string; pluggyAccountId: string; mode: LinkMode; existingAccountId?: string | null; existingCardId?: string | null; name?: string
  financialScope?: 'PF' | 'PJ'; accountType?: string | null; importFrom?: string | null; closingDay?: number | null; dueDay?: number | null
  creditLimit?: string | null; paymentAccountId?: string | null
}

export type Resolution = 'AsIncome' | 'AsExpense' | 'AsCardPurchase' | 'Ignore'
/** Possível contraparte de uma transferência. Só `canConfirm` permite confirmar; `blockedReason` diz por quê não (ex.: DifferentDate). */
export interface TransferCandidate {
  postingId: string; linkId: string; linkDisplayName: string; date: string; amount: string; direction: 'Income' | 'Expense'; description: string
  operationType: string | null; accounted: boolean; sameDay: boolean; canConfirm: boolean; blockedReason: string | null
}
export interface ReviewItem {
  postingId: string; importedTransactionId: string; linkId: string; linkDisplayName: string; linkKind: LinkKind; state: string; reason: string | null
  date: string | null; amount: string | null; direction: 'Income' | 'Expense' | null; description: string; pluggyCategory: string | null; pluggyStatus: string
  operationType: string | null; allowedResolutions: Resolution[]; resolution: Resolution | null
  /** Já contabilizado (efeito nos totais e saldos) x ainda fora dos totais. Independe da categoria. */
  accounted?: boolean; impact?: 'InTotals' | 'OutOfTotals'
  /** `Evidence` = revisão financeira de evidência não aplicada; `Link` = revisão do vínculo entre movimentos (transferência). */
  reviewKind?: 'Evidence' | 'Link' | 'Refund'; linkReviewReason?: string | null; linkOrigin?: 'Inferred' | 'UserConfirmed' | null; linkRuleVersion?: string | null
  counterpartPostingId?: string | null; canUndoLink?: boolean; candidates?: TransferCandidate[]
  /** Possível devolução: despesas que o crédito poderia ter devolvido (o usuário escolhe; a descrição nunca vincula sozinha). */
  refundCandidates?: RefundCandidate[]
}
export interface RefundCandidate {
  transactionId: string; date: string; amount: string; description: string; accountName: string; categoryPath: string | null; refundable: string; canConfirm: boolean; blockedReason: string | null
}
export interface ReviewPage { items: ReviewItem[]; total: number; page: number; pageSize: number; counts: PostingCounts; accountedInReview?: number }

export interface LinkActionResult { outcome: 'Done' | 'AlreadyDone'; summary: ApplySummary | null }

/** Prévia do reprocessamento (nunca persiste): interpretação atual x proposta e o efeito esperado em receitas, despesas, transferências e saldo. */
export interface Interpretation { state: string; kind: string; reason: string | null; effect: string | null }
export interface RecoveryItem {
  postingId: string | null; importedTransactionId: string; linkId: string; linkDisplayName: string; date: string | null; amount: string | null
  direction: 'Income' | 'Expense' | null; description: string; operationType: string | null; pluggyStatus: string; current: Interpretation; proposed: Interpretation
  outcome: 'Unchanged' | 'WillChange' | 'NeedsDecision'; note: string | null
}
export interface Figures { income: string; expense: string; transfersIn: string; transfersOut: string; net: string }
export interface RecoveryPreview {
  generatedAt: string; wouldApply: ApplySummary; accounts: { linkId: string; linkDisplayName: string; before: Figures; after: Figures }[]
  totalBefore: Figures; totalAfter: Figures; items: RecoveryItem[]; itemsTotal: number; willChange: number; needsDecision: number; persisted: false
}
export interface ApplySummary {
  created: number; updated: number; waiting: number; needsReview: number; covered: number; transfersPaired: number; billPaymentsPaired: number; outOfRange: number; unchanged: number
  purchasesRehomed?: number; cardCreditsApplied?: number; invoicesClosedByInstitution?: number; staleInvoicesRemoved?: number; billsEvaluated?: number; balanceAnchorsUpdated?: number
}

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/pluggy-integration`
const get = <T,>(url: string, fallback: string, signal?: AbortSignal) =>
  requestJson<T>(url, { headers: { Accept: 'application/json' }, signal }, { fallback, timeoutMs: 60_000 })
const post = async <T,>(url: string, body: unknown, fallback: string) =>
  requestJson<T>(url, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: JSON.stringify(body ?? {}),
  }, { fallback, timeoutMs: 60_000 })

/** Falso quando a integração Pluggy não está habilitada para este usuário/orçamento (a API responde 404). */
export async function isPluggyIntegrationAvailable(workspaceId: string, signal?: AbortSignal): Promise<boolean> {
  const response = await fetch(`${base(workspaceId)}/status`, { headers: { Accept: 'application/json' }, signal })
  return response.ok
}

export const listIntegrationConnections = (workspaceId: string, signal?: AbortSignal) =>
  get<PluggyConnection[]>(`${base(workspaceId)}/connections`, 'Falha ao carregar as conexões.', signal)

export const listAvailableAccounts = (workspaceId: string, connectionId: string, signal?: AbortSignal) =>
  get<AvailableAccount[]>(`${base(workspaceId)}/connections/${encodeURIComponent(connectionId)}/accounts`, 'Falha ao consultar as contas na Pluggy.', signal)

export const listLinks = (workspaceId: string, signal?: AbortSignal) =>
  get<AccountLink[]>(`${base(workspaceId)}/links`, 'Falha ao carregar as integrações.', signal)

export const getLinkDetail = (workspaceId: string, linkId: string, signal?: AbortSignal) =>
  get<AccountLinkDetail>(`${base(workspaceId)}/links/${encodeURIComponent(linkId)}`, 'Falha ao carregar a integração.', signal)

export const linkAccount = (workspaceId: string, request: LinkRequest) =>
  post<AccountLinkDetail>(`${base(workspaceId)}/links`, request, 'Falha ao vincular a conta.')

/** O valor sugerido é recalculado pela API; o navegador só confirma. */
export const applyOpeningBalanceSuggestion = (workspaceId: string, linkId: string, replaceExisting: boolean) =>
  post<AccountLinkDetail>(`${base(workspaceId)}/links/${encodeURIComponent(linkId)}/opening-balance-suggestion`, { confirm: true, replaceExisting }, 'Falha ao aplicar o saldo inicial sugerido.')

/** O dia sugerido é recalculado pela API a partir das faturas do banco; o navegador só confirma. */
export const applyCardScheduleSuggestion = (workspaceId: string, linkId: string) =>
  post<AccountLinkDetail>(`${base(workspaceId)}/links/${encodeURIComponent(linkId)}/card-schedule-suggestion`, { confirm: true }, 'Falha ao aplicar o fechamento e o vencimento sugeridos.')

export const reprocessImported = (workspaceId: string) =>
  post<ApplySummary>(`${base(workspaceId)}/reprocess`, {}, 'Falha ao reprocessar as transações importadas.')

export const listReview = (workspaceId: string, page = 1, signal?: AbortSignal) =>
  get<ReviewPage>(`${base(workspaceId)}/review?page=${page}&pageSize=25`, 'Falha ao carregar os movimentos em revisão.', signal)

/** Confirma, rejeita ou desfaz o vínculo entre duas pontas. Todas as validações ficam no backend (mesmo dia, contas, operação, disponibilidade). */
export const confirmTransfer = (workspaceId: string, postingId: string, counterpartPostingId: string) =>
  post<LinkActionResult>(`${base(workspaceId)}/review/${encodeURIComponent(postingId)}/transfer/confirm`, { counterpartPostingId }, 'Falha ao confirmar a transferência.')

export const rejectTransfer = (workspaceId: string, postingId: string, counterpartPostingId: string) =>
  post<LinkActionResult>(`${base(workspaceId)}/review/${encodeURIComponent(postingId)}/transfer/reject`, { counterpartPostingId }, 'Falha ao rejeitar a sugestão.')

/** Vincula o crédito à despesa devolvida (entrada de caixa que não é receita e abate o consumo no mês do crédito) ou desfaz a devolução. */
export const confirmRefund = (workspaceId: string, postingId: string, originalTransactionId: string) =>
  post<LinkActionResult>(`${base(workspaceId)}/review/${encodeURIComponent(postingId)}/refund/confirm`, { originalTransactionId }, 'Falha ao vincular a devolução.')

export const undoRefund = (workspaceId: string, postingId: string) =>
  post<LinkActionResult>(`${base(workspaceId)}/review/${encodeURIComponent(postingId)}/refund/undo`, {}, 'Falha ao desfazer a devolução.')

export const undoTransfer = (workspaceId: string, postingId: string) =>
  post<LinkActionResult>(`${base(workspaceId)}/review/${encodeURIComponent(postingId)}/transfer/undo`, {}, 'Falha ao desfazer o vínculo da transferência.')

export const previewRecovery = (workspaceId: string, filter: { amount?: string; from?: string; to?: string } = {}) =>
  post<RecoveryPreview>(`${base(workspaceId)}/recovery/preview`, {
    ...(filter.amount ? { amount: Number(filter.amount.replace(',', '.')) } : {}), ...(filter.from ? { from: filter.from } : {}), ...(filter.to ? { to: filter.to } : {}),
  }, 'Falha ao gerar a prévia.')

export const resolveReview = (workspaceId: string, postingId: string, resolution: Resolution) =>
  post<ReviewItem>(`${base(workspaceId)}/review/${encodeURIComponent(postingId)}/resolve`, { resolution }, 'Falha ao registrar a decisão.')

/**
 * Categorização dos lançamentos importados (T09.3.1). A etapa local (regras do domínio e categoria da Pluggy) é gratuita e não envia nada;
 * a etapa de IA exige confirmação explícita e só recebe descrições saneadas. Todos os números vêm da API.
 */
export interface CategorizationCounts { total: number; manual: number; pluggy: number; rule: number; ai: number; needsReview: number; notEvaluated: number }
export interface CategorizationOverview { counts: CategorizationCounts; ai: { available: boolean; model: string | null; reason: string | null } }
export interface CategorizationResult {
  considered: number; manual: number; pluggy: number; rule: number; ai: number; unresolved: number; categoriesCreated: number; skippedChanged: number
  aiItemsSent: number; aiCalls: number; inputTokens: number; outputTokens: number; estimatedCostUsd: number; aiStoppedBecause: string | null
  aiFailure: string | null; model: string | null; counts: CategorizationCounts
}
export interface CategorizationRequest { useAi: boolean; confirmExternal: boolean; force?: boolean }

export const getCategorizationOverview = (workspaceId: string, signal?: AbortSignal) =>
  get<CategorizationOverview>(`${base(workspaceId)}/categorization`, 'Falha ao carregar a categorização.', signal)

export const runCategorization = (workspaceId: string, request: CategorizationRequest) =>
  post<CategorizationResult>(`${base(workspaceId)}/categorization/run`, request, 'Falha ao categorizar os lançamentos.')
