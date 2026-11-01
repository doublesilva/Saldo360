import type { BalanceUnavailableReason, FinancialAccount } from './accounts'
import type { CategoryType } from './categories'
import type { TransactionReference } from './transactions'

export type FinancialCalendarSource = 'Transaction' | 'PlannedTransaction' | 'DebtInstallment' | 'Transfer' | 'RecurrenceOccurrence'
export type FinancialCalendarStatus = 'Realized' | 'Pending' | 'Overdue' | 'NeedsReview'

export interface FinancialCalendarItem {
  id: string
  source: FinancialCalendarSource
  status: FinancialCalendarStatus
  type: CategoryType
  /** Texto decimal exato, nunca converter para number. */
  amount: string
  /** Data efetiva para Transaction e prevista para PlannedTransaction. */
  date: string
  description: string
  account: TransactionReference | null
  category: TransactionReference | null
  currency: string
  plannedTransactionId: string | null
  allowedActions?: ('Realize' | 'Edit' | 'Cancel' | 'UndoRealization' | 'ViewOrigin' | 'CancelPlannedTransfer')[]
  /** Transferência prevista que originou o item (source 'Transfer', pendente/vencido). */
  plannedTransferId?: string | null
  origin?: string
  recurrenceId?: string | null
  occurrenceNumber?: number | null
  scheduledDate?: string | null
  /** Despesa fixa: Planned (sem pagamento), Reconciled (pagamento vinculado) ou NeedsReview (a revisar). */
  reconciliationStatus?: 'Planned' | 'Reconciled' | 'NeedsReview' | null
  /** Valor/data efetivos do pagamento vinculado e id da movimentação (nunca somados ao previsto). */
  actualAmount?: string | null
  actualDate?: string | null
  transactionId?: string | null
}

/** Totais calculados pela API. A UI apenas os formata. */
export interface FinancialCalendarSummary {
  realizedIncome: string
  realizedExpense: string
  pendingIncome: string
  pendingExpense: string
  overdueIncome: string
  overdueExpense: string
  /** Devoluções bancárias do período: não são receita e já estão abatidas de realizedExpense. */
  realizedRefunds?: string
  /** Transferências do período, sempre à parte de receitas/despesas (aditivo). */
  transfersReceived?: string
  transfersSent?: string
  transfersInternal?: string
  /** Total de entradas/saídas de caixa realizadas, somados no servidor (mesma definição de Contas e Visão Geral; sem previstos). */
  totalInflows?: string
  totalOutflows?: string
}

export interface FinancialCalendarResponse {
  from: string
  through: string
  items: FinancialCalendarItem[]
  summary: FinancialCalendarSummary
}

export interface FinancialCalendarFilters {
  accountId?: string
  type?: CategoryType
}

export type FinancialCalendarBalanceKind = 'Realized' | 'Projected'

export interface FinancialCalendarBalanceDay {
  date: string
  kind: FinancialCalendarBalanceKind
  income: string | null
  expense: string | null
  transferIn: string | null
  transferOut: string | null
  closingBalance: string | null
  hasOverduePending: boolean
  isNegative: boolean
  /** Coordenada vertical de 0 a 1000 calculada pela API; não é valor monetário. */
  chartPosition: number | null
  unavailableReason: BalanceUnavailableReason | null
}

export interface FinancialCalendarBalanceRisk {
  minimumProjectedBalance: string | null
  firstNegativeProjectedDate: string | null
  endingProjectedBalance: string | null
}

export interface FinancialCalendarBalanceResponse {
  creditCardProjectionComplete?: boolean
  debtProjectionComplete?: boolean
  from: string
  through: string
  accountId: string | null
  openingBalance: string | null
  unavailableReason: BalanceUnavailableReason | null
  days: FinancialCalendarBalanceDay[]
  risk: FinancialCalendarBalanceRisk
}

export interface FinancialCalendarMonthlyProjectionMonth {
  month: string
  realizedIncome: string
  realizedExpense: string
  pendingIncome: string
  pendingExpense: string
  overdueIncome: string
  overdueExpense: string
  openingBalance: string | null
  closingBalance: string | null
  minimumProjectedBalance: string | null
  firstNegativeProjectedDate: string | null
  unavailableReason: BalanceUnavailableReason | null
  creditCardOutstanding?: string | null
  cardPayments?: string
  creditCardProjectionComplete?: boolean
  debtProjectionComplete?: boolean
  fixedExpenses?: string | null
  budgetTarget?: string | null
  budgetConsumption?: string | null
  budgetStatus?: string
  periodKind?: 'Historical' | 'Current' | 'Future'
  status?: string
}

export interface FinancialCalendarMonthlyProjectionResponse {
  from: string
  months: FinancialCalendarMonthlyProjectionMonth[]
  through?: string
  summary?: { totalIncome: string; totalExpense: string; lowestClosingBalance: string | null; lowestBalanceMonth: string | null; monthsOverBudget: number; monthsWithNegativeBalance: number }
}

export async function getSixMonthPlanning(workspaceId: string, from: string, accountId?: string, signal?: AbortSignal): Promise<FinancialCalendarMonthlyProjectionResponse> {
  const query = new URLSearchParams({ from })
  if (accountId) query.set('accountId', accountId)
  const response = await fetch(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-planning/six-months?${query}`, { signal, headers: { Accept: 'application/json' } })
  if (!response.ok) throw new Error(`Falha ao carregar o planejamento financeiro (HTTP ${response.status}).`)
  return response.json()
}

const jsonHeaders = { Accept: 'application/json' }

export async function getFinancialCalendar(
  workspaceId: string,
  from: string,
  through: string,
  filters: FinancialCalendarFilters,
  signal?: AbortSignal,
): Promise<FinancialCalendarResponse> {
  const query = new URLSearchParams({ from, through, includeTransfers: 'true' })
  if (filters.accountId) query.set('accountId', filters.accountId)
  if (filters.type) query.set('type', filters.type)

  const response = await fetch(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-calendar?${query}`, {
    signal,
    headers: jsonHeaders,
  })
  if (!response.ok) throw new Error(`Falha ao carregar a agenda financeira (HTTP ${response.status}).`)

  return (await response.json()) as FinancialCalendarResponse
}

export async function getFinancialCalendarBalances(
  workspaceId: string,
  from: string,
  through: string,
  accountId?: string,
  signal?: AbortSignal,
): Promise<FinancialCalendarBalanceResponse> {
  const query = new URLSearchParams({ from, through })
  if (accountId) query.set('accountId', accountId)
  const response = await fetch(
    `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-calendar/balances?${query}`,
    { signal, headers: jsonHeaders },
  )
  if (!response.ok) throw new Error(`Falha ao carregar a evolução do saldo (HTTP ${response.status}).`)
  return (await response.json()) as FinancialCalendarBalanceResponse
}

export async function getFinancialCalendarMonthlyProjection(
  workspaceId: string,
  from: string,
  months = 6,
  accountId?: string,
  signal?: AbortSignal,
): Promise<FinancialCalendarMonthlyProjectionResponse> {
  const query = new URLSearchParams({ from, months: String(months) })
  if (accountId) query.set('accountId', accountId)
  const response = await fetch(
    `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-calendar/monthly-projection?${query}`,
    { signal, headers: jsonHeaders },
  )
  if (!response.ok) throw new Error(`Falha ao carregar o planejamento financeiro (HTTP ${response.status}).`)
  return (await response.json()) as FinancialCalendarMonthlyProjectionResponse
}

/** Contas ativas e históricas podem aparecer na agenda e, portanto, no filtro. */
export type FinancialCalendarAccount = Pick<FinancialAccount, 'id' | 'name' | 'isActive'>
