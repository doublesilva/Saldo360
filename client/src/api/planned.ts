import type { CategoryType } from './categories'
import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { TransactionReference } from './transactions'

export const PLANNED_DESCRIPTION_MAX_LENGTH = 200
export const PLANNED_PAGE_SIZE = 20

/**
 * Receita ou despesa prevista única: valor esperado para dueDate. Separada do realizado: não gera lançamento e
 * não entra no saldo. amount vem como texto decimal exato ("1234.56"): só é formatado, nunca convertido para
 * number. "Vencida" não vem da API: é derivado da data na exibição.
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

/** O período (from/to) se aplica à data prevista. */
export interface PlannedFilters {
  from: string
  to: string
  accountId: string
  categoryId: string
  type: CategoryType | ''
  includeCancelled: boolean
}

export const emptyPlannedFilters: PlannedFilters = {
  from: '',
  to: '',
  accountId: '',
  categoryId: '',
  type: '',
  includeCancelled: false,
}

export type SavePlannedResult = { ok: true; planned: PlannedTransaction } | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

function plannedUrl(familyId: string, plannedId?: string): string {
  const base = `/api/families/${encodeURIComponent(familyId)}/planned-transactions`
  return plannedId ? `${base}/${encodeURIComponent(plannedId)}` : base
}

/** Previsão pendente cuja data prevista já passou (antes de hoje em America/Sao_Paulo). Só exibição. */
export function isOverdue(planned: PlannedTransaction, today: string): boolean {
  return !planned.isCancelled && planned.dueDate < today
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
