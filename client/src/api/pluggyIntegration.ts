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

export interface BillReconciliation {
  closingDate: string | null; dueDate: string | null; bankTotal: string | null; saldo360Total: string | null; difference: string | null
  status: 'Official' | 'OfficialDiverges' | 'Open' | 'Mismatch' | 'NoInvoice'
}
export interface CardFigures {
  creditLimit: string | null; usedLimit: string | null; availableLimit: string | null; observedBalanceDue: string | null; observedAvailableLimit: string | null
  bills: BillReconciliation[]
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
export interface ReviewItem {
  postingId: string; importedTransactionId: string; linkId: string; linkDisplayName: string; linkKind: LinkKind; state: string; reason: string | null
  date: string | null; amount: string | null; direction: 'Income' | 'Expense' | null; description: string; pluggyCategory: string | null; pluggyStatus: string
  operationType: string | null; allowedResolutions: Resolution[]; resolution: Resolution | null
}
export interface ReviewPage { items: ReviewItem[]; total: number; page: number; pageSize: number; counts: PostingCounts }
export interface ApplySummary { created: number; updated: number; waiting: number; needsReview: number; covered: number; transfersPaired: number; billPaymentsPaired: number; outOfRange: number; unchanged: number }

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

export const reprocessImported = (workspaceId: string) =>
  post<ApplySummary>(`${base(workspaceId)}/reprocess`, {}, 'Falha ao reprocessar as transações importadas.')

export const listReview = (workspaceId: string, page = 1, signal?: AbortSignal) =>
  get<ReviewPage>(`${base(workspaceId)}/review?page=${page}&pageSize=25`, 'Falha ao carregar os movimentos em revisão.', signal)

export const resolveReview = (workspaceId: string, postingId: string, resolution: Resolution) =>
  post<ReviewItem>(`${base(workspaceId)}/review/${encodeURIComponent(postingId)}/resolve`, { resolution }, 'Falha ao registrar a decisão.')
