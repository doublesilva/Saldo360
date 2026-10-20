import { CSRF_HEADER, getCsrfToken } from './csrf'
import { ApiRequestError, requestJson } from './apiErrors'

export interface FinancialInvoiceReconciliation {
  id: string
  workspaceId: string
  plannedTransactionId: string
  creditCardInvoiceId: string
  createdAt: string
  removedAt: string | null
}

export interface InvoiceReconciliationInput {
  plannedTransactionId: string
  creditCardInvoiceId: string
}

const headers = { Accept: 'application/json' }

function base(workspaceId: string): string {
  return `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/invoice-reconciliations`
}

export async function listInvoiceReconciliations(workspaceId: string, signal?: AbortSignal): Promise<FinancialInvoiceReconciliation[]> {
  const response = await fetch(`${base(workspaceId)}/`, { signal, headers })
  if (!response.ok) throw new Error(`Falha ao carregar as conciliações (HTTP ${response.status}).`)
  return await response.json() as FinancialInvoiceReconciliation[]
}

export type CreateReconciliationResult =
  | { ok: true; reconciliation: FinancialInvoiceReconciliation }
  | { ok: false; errors: Record<string, string[]> }

export async function createInvoiceReconciliation(
  workspaceId: string,
  input: InvoiceReconciliationInput,
): Promise<CreateReconciliationResult> {
  try {
    const reconciliation = await requestJson<FinancialInvoiceReconciliation>(`${base(workspaceId)}/`, {
      method: 'POST', headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify(input),
    }, { resultUncertainOnTimeout: true, fallback: 'Falha ao criar a conciliação.' })
    return { ok: true, reconciliation }
  } catch (error) {
    if (error instanceof ApiRequestError && (error.status === 400 || error.status === 409 || error.status === 422)) return { ok: false, errors: Object.keys(error.fieldErrors).length > 0 ? error.fieldErrors : { general: [error.message] } }
    throw error
  }
}

export async function removeInvoiceReconciliation(workspaceId: string, id: string): Promise<{ ok: true } | { ok: false; message: string }> {
  try {
    await requestJson<void>(`${base(workspaceId)}/${encodeURIComponent(id)}`, { method: 'DELETE', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }, { resultUncertainOnTimeout: true, fallback: 'Falha ao desfazer a conciliação.' })
    return { ok: true }
  } catch (error) {
    if (error instanceof ApiRequestError && error.status === 404) return { ok: false, message: 'Conciliação não encontrada.' }
    throw error
  }
}
