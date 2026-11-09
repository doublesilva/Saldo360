import { requestJson } from './apiErrors'

/**
 * T13.1: lançamentos unificados de uma competência (transações, previsões, recorrências, dívidas, transferências e cartões), uma linha por movimento lógico. Tudo vem do servidor
 * (filtros, ordem, paginação, tipo, situação e ações permitidas); valores são texto decimal exato e nada é somado no navegador.
 */
export type EntrySource = 'Transaction' | 'Planned' | 'Recurrence' | 'Debt' | 'Transfer' | 'CardPurchase' | 'CardAdjustment' | 'CardProjected'
export type EntryType = 'Income' | 'Expense' | 'Transfer' | 'Payment'
export type EntryStatus = 'Realized' | 'Pending' | 'Overdue' | 'NeedsReview' | 'Cancelled'
export type EntryAction = 'ViewDetails' | 'Edit' | 'Cancel' | 'Recategorize' | 'ChangeNature' | 'UndoRefund' | 'Realize' | 'CancelPlannedTransfer' | 'OpenCards' | 'OpenDebts' | 'OpenTransfers' | 'OpenRecurrences' | string

export interface EntryReference { id: string | null; name: string; scope?: string | null }
export interface EntryCategory { id: string | null; name: string; path: string | null }

export interface FinancialEntry {
  id: string; source: EntrySource; competence: string; date: string; dateKind: 'Effective' | 'Due' | 'Invoice' | 'Execution'; description: string
  type: EntryType; direction: 'In' | 'Out' | 'Neutral'; status: EntryStatus; amount: string
  account: EntryReference | null; counterpart: EntryReference | null; card: EntryReference | null; category: EntryCategory | null; nature: string | null; origin: string
  budgetImpact: 'Consumes' | 'Reduces' | 'Planned' | 'None'; cashImpact: 'Immediate' | 'AtInvoicePayment' | 'Pending' | 'None'
  transactionId: string | null; plannedTransactionId: string | null; transferId: string | null; plannedTransferId: string | null; recurrenceId: string | null; invoiceCompetence: string | null
  actions: EntryAction[]; flags: string[]
  /** Estágio financeiro (movimento efetivo, compra lançada no cartão, parcela prevista, estimativa, pagamento realizado…): nunca indica que a fatura foi paga. */
  stage?: string
  /** Grupo da linha quando a consulta pede agrupamento; vem do servidor. */
  groupKey?: string | null
  installment?: { originalDescription: string; number: number | null; total: number | null; originalNumber: number | null; originalTotal: number | null; inconsistent: boolean }
}

/**
 * Resumo de um grupo (cartão ou conta) ou do conjunto filtrado inteiro, calculado no servidor em decimal sobre TODOS os lançamentos filtrados (nunca só a página). A soma é das linhas, não o total
 * oficial de uma fatura. Transferências são contadas e nunca somadas; pagamentos de fatura são caixa, não consumo; financiamento de fatura é obrigação sem novo consumo; cancelados ficam fora das somas.
 */
export interface EntryGroupSummary {
  key: string; kind: 'Card' | 'Account' | 'None' | 'All'; id: string | null; name: string; scope: string | null
  count: number; activeCount: number; cancelledCount: number; transferCount: number
  outflow: string; inflow: string; paymentsOut: string; budgetConsumed: string; budgetReduced: string; budgetNet: string; budgetPlanned: string; invoiceFinancing: string
  /** Página em que aparece o primeiro item do grupo. */
  startsOnPage: number
}

export interface FinancialEntriesPage {
  items: FinancialEntry[]; page: number; pageSize: number; totalCount: number; competence: string
  groups?: EntryGroupSummary[]; totals?: EntryGroupSummary; groupBy?: EntriesGroupBy
}

export type EntriesScope = 'all' | 'pf' | 'pj'
export type EntriesGroupBy = 'card' | 'account' | 'none'
/** Valor especial do filtro de cartão: somente movimentos que não são de cartão. */
export const NO_CARD = 'none'
export interface EntriesFilters {
  competence: string; scope: EntriesScope; accountId: string; cardId: string; groupBy: EntriesGroupBy; categoryId: string; type: EntryType | ''; status: EntryStatus | 'OverduePrevious' | ''; source: EntrySource | ''
  nature: 'Fixed' | 'Variable' | 'Undefined' | ''; search: string; includeCancelled: boolean; sort: 'date' | 'amount' | 'description'; direction: 'asc' | 'desc'
}

export const ENTRIES_PAGE_SIZE = 25

export function listFinancialEntries(workspaceId: string, filters: EntriesFilters, page: number, signal?: AbortSignal): Promise<FinancialEntriesPage> {
  const query = new URLSearchParams({ competence: filters.competence, scope: filters.scope, sort: filters.sort, direction: filters.direction, page: String(page), pageSize: String(ENTRIES_PAGE_SIZE) })
  for (const key of ['accountId', 'categoryId', 'type', 'status', 'source', 'nature'] as const) if (filters[key]) query.set(key, filters[key])
  if (filters.cardId === NO_CARD) query.set('withoutCard', 'true'); else if (filters.cardId) query.set('cardId', filters.cardId)
  query.set('groupBy', filters.groupBy)
  if (filters.search.trim()) query.set('search', filters.search.trim())
  if (filters.includeCancelled) query.set('includeCancelled', 'true')
  return requestJson<FinancialEntriesPage>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-entries?${query}`,
    { signal, headers: { Accept: 'application/json' } }, { fallback: 'Falha ao carregar os lançamentos.' })
}
