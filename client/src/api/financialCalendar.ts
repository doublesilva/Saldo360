import type { BalanceUnavailableReason, FinancialAccount } from './accounts'
import type { CategoryType } from './categories'
import type { TransactionReference } from './transactions'

export type FinancialCalendarSource = 'Transaction' | 'PlannedTransaction'
export type FinancialCalendarStatus = 'Realized' | 'Pending' | 'Overdue'

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
  account: TransactionReference
  category: TransactionReference | null
  currency: string
  plannedTransactionId: string | null
}

/** Totais calculados pela API. A UI apenas os formata. */
export interface FinancialCalendarSummary {
  realizedIncome: string
  realizedExpense: string
  pendingIncome: string
  pendingExpense: string
  overdueIncome: string
  overdueExpense: string
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
  from: string
  through: string
  accountId: string | null
  openingBalance: string | null
  unavailableReason: BalanceUnavailableReason | null
  days: FinancialCalendarBalanceDay[]
  risk: FinancialCalendarBalanceRisk
}

const jsonHeaders = { Accept: 'application/json' }

export async function getFinancialCalendar(
  familyId: string,
  from: string,
  through: string,
  filters: FinancialCalendarFilters,
  signal?: AbortSignal,
): Promise<FinancialCalendarResponse> {
  const query = new URLSearchParams({ from, through })
  if (filters.accountId) query.set('accountId', filters.accountId)
  if (filters.type) query.set('type', filters.type)

  const response = await fetch(`/api/families/${encodeURIComponent(familyId)}/financial-calendar?${query}`, {
    signal,
    headers: jsonHeaders,
  })
  if (!response.ok) throw new Error(`Falha ao carregar a agenda financeira (HTTP ${response.status}).`)

  return (await response.json()) as FinancialCalendarResponse
}

export async function getFinancialCalendarBalances(
  familyId: string,
  from: string,
  through: string,
  accountId?: string,
  signal?: AbortSignal,
): Promise<FinancialCalendarBalanceResponse> {
  const query = new URLSearchParams({ from, through })
  if (accountId) query.set('accountId', accountId)
  const response = await fetch(
    `/api/families/${encodeURIComponent(familyId)}/financial-calendar/balances?${query}`,
    { signal, headers: jsonHeaders },
  )
  if (!response.ok) throw new Error(`Falha ao carregar a evolução do saldo (HTTP ${response.status}).`)
  return (await response.json()) as FinancialCalendarBalanceResponse
}

/** Contas ativas e históricas podem aparecer na agenda e, portanto, no filtro. */
export type FinancialCalendarAccount = Pick<FinancialAccount, 'id' | 'name' | 'isActive'>
