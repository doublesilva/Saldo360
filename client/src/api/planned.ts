import type { CategoryType } from './categories'
import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { FinancialTransaction, TransactionReference } from './transactions'

export const PLANNED_DESCRIPTION_MAX_LENGTH = 200
export const PLANNED_PAGE_SIZE = 20

/**
 * Situação derivada pela API: Cancelled se cancelada; Realized se há lançamento efetivo ativo vinculado;
 * senão Pending. Cancelar o lançamento efetivo volta a previsão a Pending.
 */
export type PlannedStatus = 'Pending' | 'Realized' | 'Cancelled'

/** Lançamento efetivo ativo: valor e data efetivos, separados dos previstos. amount em texto exato. */
export interface PlannedRealization {
  transactionId: string
  amount: string
  transactionDate: string
}

/**
 * Receita ou despesa prevista única: valor esperado para dueDate. Não entra no saldo até ser realizada (o que cria
 * um lançamento efetivo vinculado). amount e dueDate são sempre os PREVISTOS, em texto exato ("1234.56"): só são
 * formatados, nunca convertidos para number. "Vencida" não vem da API: é derivado da data na exibição.
 */
export interface PlannedTransaction {
  id: string
  type: CategoryType
  amount: string
  /** Data prevista, "yyyy-MM-dd", sem horário. Pode ser futura. */
  dueDate: string
  description: string
  account: TransactionReference
  category: TransactionReference
  status: PlannedStatus
  realization: PlannedRealization | null
  isCancelled: boolean
  currency: string
  createdAt: string
  updatedAt: string | null
  cancelledAt: string | null
}

export interface PlannedPage {
  items: PlannedTransaction[]
  page: number
  pageSize: number
  totalCount: number
}

/** Corpo de criação/edição. amount vai como texto decimal exato ("1234.56"). */
export interface PlannedInput {
  accountId: string
  categoryId: string
  type: CategoryType
  description: string
  amount: string
  dueDate: string
}

/** O período (from/to) se aplica à data prevista. Por padrão só pendentes. */
export interface PlannedFilters {
  from: string
  to: string
  accountId: string
  categoryId: string
  type: CategoryType | ''
  includeRealized: boolean
  includeCancelled: boolean
}

export const emptyPlannedFilters: PlannedFilters = {
  from: '',
  to: '',
  accountId: '',
  categoryId: '',
  type: '',
  includeRealized: false,
  includeCancelled: false,
}

/** Corpo da realização: só valor e data efetivos (texto exato e "yyyy-MM-dd"). */
export interface RealizeInput {
  amount: string
  transactionDate: string
}

export type RealizeResult =
  | { ok: true; transaction: FinancialTransaction; created: boolean }
  | { ok: false; errors: Record<string, string[]> }

export type SavePlannedResult = { ok: true; planned: PlannedTransaction } | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

function plannedUrl(familyId: string, plannedId?: string): string {
  const base = `/api/families/${encodeURIComponent(familyId)}/planned-transactions`
  return plannedId ? `${base}/${encodeURIComponent(plannedId)}` : base
}

/** Previsão PENDENTE cuja data prevista já passou (antes de hoje em America/Sao_Paulo). Só exibição. */
export function isOverdue(planned: PlannedTransaction, today: string): boolean {
  return planned.status === 'Pending' && planned.dueDate < today
}

export async function listPlanned(
  familyId: string,
  filters: PlannedFilters,
  page: number,
  signal?: AbortSignal,
): Promise<PlannedPage> {
  const query = new URLSearchParams({ page: String(page), pageSize: String(PLANNED_PAGE_SIZE) })
  if (filters.from) query.set('from', filters.from)
  if (filters.to) query.set('to', filters.to)
  if (filters.accountId) query.set('accountId', filters.accountId)
  if (filters.categoryId) query.set('categoryId', filters.categoryId)
  if (filters.type) query.set('type', filters.type)
  if (filters.includeRealized) query.set('includeRealized', 'true')
  if (filters.includeCancelled) query.set('includeCancelled', 'true')

  const response = await fetch(`${plannedUrl(familyId)}?${query}`, { signal, headers: jsonHeaders })
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    throw new Error(Object.values(problem.errors ?? {}).flat()[0] ?? 'Filtros inválidos.')
  }
  if (!response.ok) throw new Error(`Falha ao carregar as previsões (HTTP ${response.status}).`)
  return (await response.json()) as PlannedPage
}

async function write(method: 'POST' | 'PUT', url: string, body: unknown, expected: number): Promise<SavePlannedResult> {
  const response = await fetch(url, {
    method,
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === expected) return { ok: true, planned: (await response.json()) as PlannedTransaction }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  if (response.status === 409) {
    const problem = (await response.json().catch(() => ({}))) as { title?: string }
    return { ok: false, errors: { form: [problem.title ?? 'Previsão cancelada não pode ser editada.'] } }
  }
  if (response.status === 404) throw new Error('Previsão ou família não encontrada.')
  throw new Error(`Falha ao salvar a previsão (HTTP ${response.status}).`)
}

export function createPlanned(familyId: string, input: PlannedInput): Promise<SavePlannedResult> {
  return write('POST', plannedUrl(familyId), input, 201)
}

export function updatePlanned(familyId: string, id: string, input: PlannedInput): Promise<SavePlannedResult> {
  return write('PUT', plannedUrl(familyId, id), input, 200)
}

/** Cancela sem excluir; repetir é seguro (idempotente). */
export function cancelPlanned(familyId: string, id: string): Promise<SavePlannedResult> {
  return write('POST', `${plannedUrl(familyId, id)}/cancel`, undefined, 200)
}

/**
 * Realiza a previsão: cria um lançamento efetivo vinculado com o valor e a data informados. 201 = criado; 200 =
 * repetição com os mesmos dados (devolve o existente). 409 (cancelada ou já realizada com outros dados) e 400
 * viram erros para exibir.
 */
export async function realizePlanned(familyId: string, id: string, input: RealizeInput): Promise<RealizeResult> {
  const response = await fetch(`${plannedUrl(familyId, id)}/realize`, {
    method: 'POST',
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: JSON.stringify(input),
  })

  if (response.status === 201 || response.status === 200) {
    return { ok: true, transaction: (await response.json()) as FinancialTransaction, created: response.status === 201 }
  }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  if (response.status === 409) {
    const problem = (await response.json().catch(() => ({}))) as { title?: string }
    return { ok: false, errors: { form: [problem.title ?? 'Não foi possível realizar a previsão.'] } }
  }
  if (response.status === 404) throw new Error('Previsão ou família não encontrada.')
  throw new Error(`Falha ao realizar a previsão (HTTP ${response.status}).`)
}
