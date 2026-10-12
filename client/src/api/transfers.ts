import type { FinancialScope } from './accounts'
import { CSRF_HEADER, getCsrfToken } from './csrf'

export const TRANSFER_DESCRIPTION_MAX_LENGTH = 200
export const TRANSFERS_PAGE_SIZE = 20

export interface TransferAccount {
  id: string
  name: string
  financialScope: FinancialScope
  isActive: boolean
}

/**
 * Transferência já realizada entre contas da família: um único registro com origem e destino, sem categoria.
 * amount vem como texto decimal exato ("500.00"): só é formatado, nunca convertido para number.
 */
export interface FinancialTransfer {
  id: string
  amount: string
  /** "yyyy-MM-dd", sem horário. */
  transferDate: string
  description: string
  sourceAccount: TransferAccount
  destinationAccount: TransferAccount
  isCancelled: boolean
  currency: string
  createdAt: string
  cancelledAt: string | null
}

export interface TransferPage {
  items: FinancialTransfer[]
  page: number
  pageSize: number
  totalCount: number
}

/** Corpo de criação. amount vai como texto decimal exato ("500.00"). Não há edição. */
export interface TransferInput {
  sourceAccountId: string
  destinationAccountId: string
  amount: string
  transferDate: string
  description: string
}

export interface TransferFilters {
  from: string
  to: string
  /** Origem ou destino. */
  accountId: string
  includeCancelled: boolean
}

export const emptyTransferFilters: TransferFilters = { from: '', to: '', accountId: '', includeCancelled: false }

export type SaveTransferResult =
  | { ok: true; transfer: FinancialTransfer }
  | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

function transfersUrl(familyId: string, transferId?: string): string {
  const base = `/api/families/${encodeURIComponent(familyId)}/transfers`
  return transferId ? `${base}/${encodeURIComponent(transferId)}` : base
}

export async function listTransfers(
  familyId: string,
  filters: TransferFilters,
  page: number,
  signal?: AbortSignal,
): Promise<TransferPage> {
  const query = new URLSearchParams({ page: String(page), pageSize: String(TRANSFERS_PAGE_SIZE) })
  if (filters.from) query.set('from', filters.from)
  if (filters.to) query.set('to', filters.to)
  if (filters.accountId) query.set('accountId', filters.accountId)
  if (filters.includeCancelled) query.set('includeCancelled', 'true')

  const response = await fetch(`${transfersUrl(familyId)}?${query}`, { signal, headers: jsonHeaders })
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    throw new Error(Object.values(problem.errors ?? {}).flat()[0] ?? 'Filtros inválidos.')
  }
  if (!response.ok) throw new Error(`Falha ao carregar as transferências (HTTP ${response.status}).`)
  return (await response.json()) as TransferPage
}

async function post(url: string, body: unknown, expected: number): Promise<SaveTransferResult> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === expected) return { ok: true, transfer: (await response.json()) as FinancialTransfer }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  if (response.status === 404) throw new Error('Transferência ou família não encontrada.')
  throw new Error(`Falha ao salvar a transferência (HTTP ${response.status}).`)
}

export function createTransfer(familyId: string, input: TransferInput): Promise<SaveTransferResult> {
  return post(transfersUrl(familyId), input, 201)
}

/** Cancela sem excluir; repetir é seguro (idempotente). */
export function cancelTransfer(familyId: string, transferId: string): Promise<SaveTransferResult> {
  return post(`${transfersUrl(familyId, transferId)}/cancel`, undefined, 200)
}
