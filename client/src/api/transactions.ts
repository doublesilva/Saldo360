import type { CategoryType } from './categories'
import { CSRF_HEADER, getCsrfToken } from './csrf'

export const TRANSACTION_DESCRIPTION_MAX_LENGTH = 200
export const TRANSACTIONS_PAGE_SIZE = 20

export interface TransactionReference {
  id: string
  name: string
  isActive: boolean
}

/**
 * Lançamento único já realizado. amount é sempre positivo: o tipo diz o sentido (receita soma, despesa
 * subtrai). amount vem como texto decimal exato ("1234.56"): só é formatado, nunca convertido para number.
 */
export interface FinancialTransaction {
  id: string
  type: CategoryType
  amount: string
  /** "yyyy-MM-dd", sem horário. */
  transactionDate: string
  description: string
  account: TransactionReference
  category: TransactionReference
  isCancelled: boolean
  currency: string
  createdAt: string
  updatedAt: string | null
  cancelledAt: string | null
}

export interface TransactionPage {
  items: FinancialTransaction[]
  page: number
  pageSize: number
  totalCount: number
}

/** Corpo de criação/edição. amount vai como texto decimal exato ("1234.56"), nunca como number. */
export interface TransactionInput {
  accountId: string
  categoryId: string
  type: CategoryType
  description: string
  amount: string
  transactionDate: string
}

export interface TransactionFilters {
  from: string
  to: string
  accountId: string
  categoryId: string
  type: CategoryType | ''
  includeCancelled: boolean
}

export const emptyFilters: TransactionFilters = {
  from: '',
  to: '',
  accountId: '',
  categoryId: '',
  type: '',
  includeCancelled: false,
}

export type SaveTransactionResult =
  | { ok: true; transaction: FinancialTransaction }
  | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

function transactionsUrl(familyId: string, transactionId?: string): string {
  const base = `/api/families/${encodeURIComponent(familyId)}/transactions`
  return transactionId ? `${base}/${encodeURIComponent(transactionId)}` : base
}

export async function listTransactions(
  familyId: string,
  filters: TransactionFilters,
  page: number,
  signal?: AbortSignal,
): Promise<TransactionPage> {
  const query = new URLSearchParams({ page: String(page), pageSize: String(TRANSACTIONS_PAGE_SIZE) })
  if (filters.from) query.set('from', filters.from)
  if (filters.to) query.set('to', filters.to)
  if (filters.accountId) query.set('accountId', filters.accountId)
  if (filters.categoryId) query.set('categoryId', filters.categoryId)
  if (filters.type) query.set('type', filters.type)
  if (filters.includeCancelled) query.set('includeCancelled', 'true')

  const response = await fetch(`${transactionsUrl(familyId)}?${query}`, { signal, headers: jsonHeaders })
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    throw new Error(Object.values(problem.errors ?? {}).flat()[0] ?? 'Filtros inválidos.')
  }
  if (!response.ok) throw new Error(`Falha ao carregar os lançamentos (HTTP ${response.status}).`)
  return (await response.json()) as TransactionPage
}

async function write(method: 'POST' | 'PUT', url: string, body: unknown, expected: number): Promise<SaveTransactionResult> {
  const response = await fetch(url, {
    method,
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === expected) return { ok: true, transaction: (await response.json()) as FinancialTransaction }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  if (response.status === 409) {
    const problem = (await response.json().catch(() => ({}))) as { title?: string }
    return { ok: false, errors: { form: [problem.title ?? 'Lançamento cancelado não pode ser editado.'] } }
  }
  if (response.status === 404) throw new Error('Lançamento ou família não encontrado.')
  throw new Error(`Falha ao salvar o lançamento (HTTP ${response.status}).`)
}

export function createTransaction(familyId: string, input: TransactionInput): Promise<SaveTransactionResult> {
  return write('POST', transactionsUrl(familyId), input, 201)
}

export function updateTransaction(familyId: string, id: string, input: TransactionInput): Promise<SaveTransactionResult> {
  return write('PUT', transactionsUrl(familyId, id), input, 200)
}

/** Cancela sem excluir; repetir é seguro (idempotente). */
export function cancelTransaction(familyId: string, id: string): Promise<SaveTransactionResult> {
  return write('POST', `${transactionsUrl(familyId, id)}/cancel`, undefined, 200)
}
