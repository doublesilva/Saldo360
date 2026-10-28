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
  /** null = sem categoria (ex.: pagamento de fatura ou movimento importado ainda sem categoria). */
  category: TransactionReference | null
  isCancelled: boolean
  currency: string
  createdAt: string
  updatedAt: string | null
  cancelledAt: string | null
  /** Previsão que este lançamento realiza (não editável; cancelar reabre a previsão). null = avulso. */
  plannedTransactionId: string | null
  /** 'PluggyImport' = importado do banco (T09.3): só a categoria pode ser corrigida. */
  origin?: 'Manual' | 'PlannedRealization' | 'CreditCardInvoicePayment' | 'DebtInstallmentPayment' | 'PluggyImport'
  /** Quem definiu a categoria de um lançamento importado: banco (Pluggy/Mapped), regra, IA ou o próprio usuário. */
  categorySource?: 'None' | 'Mapped' | 'User' | 'Pluggy' | 'Rule' | 'AI' | null
  /** A classificação automática não chegou a uma categoria segura: o usuário precisa decidir. */
  categoryNeedsReview?: boolean
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

function transactionsUrl(workspaceId: string, transactionId?: string): string {
  const base = `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/transactions`
  return transactionId ? `${base}/${encodeURIComponent(transactionId)}` : base
}

export async function listTransactions(
  workspaceId: string,
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

  const response = await fetch(`${transactionsUrl(workspaceId)}?${query}`, { signal, headers: jsonHeaders })
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
  if (response.status === 404) throw new Error('Lançamento ou orçamento não encontrado.')
  throw new Error(`Falha ao salvar o lançamento (HTTP ${response.status}).`)
}

export function createTransaction(workspaceId: string, input: TransactionInput): Promise<SaveTransactionResult> {
  return write('POST', transactionsUrl(workspaceId), input, 201)
}

export function updateTransaction(workspaceId: string, id: string, input: TransactionInput): Promise<SaveTransactionResult> {
  return write('PUT', transactionsUrl(workspaceId, id), input, 200)
}

/** Cancela sem excluir; repetir é seguro (idempotente). */
export function cancelTransaction(workspaceId: string, id: string): Promise<SaveTransactionResult> {
  return write('POST', `${transactionsUrl(workspaceId, id)}/cancel`, undefined, 200)
}

/** Um lançamento (inclusive cancelado), por exemplo o efetivo de uma previsão realizada. */
export async function getTransaction(workspaceId: string, id: string, signal?: AbortSignal): Promise<FinancialTransaction> {
  const response = await fetch(transactionsUrl(workspaceId, id), { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar o lançamento (HTTP ${response.status}).`)
  return (await response.json()) as FinancialTransaction
}

/** Corrige só a categoria de um lançamento importado (os dados de origem não mudam). categoryId null remove a categoria. */
export function recategorizeTransaction(workspaceId: string, id: string, categoryId: string | null): Promise<SaveTransactionResult> {
  return write('PUT', `${transactionsUrl(workspaceId, id)}/category`, { categoryId }, 200)
}
