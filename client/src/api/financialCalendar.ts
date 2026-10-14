import type { FinancialAccount } from './accounts'
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

/** Contas ativas e históricas podem aparecer na agenda e, portanto, no filtro. */
export type FinancialCalendarAccount = Pick<FinancialAccount, 'id' | 'name' | 'isActive'>
