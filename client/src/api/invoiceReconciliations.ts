import { CSRF_HEADER, getCsrfToken } from './csrf'

export interface FinancialInvoiceReconciliation {
  id: string
  familyId: string
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

function base(familyId: string): string {
  return `/api/families/${encodeURIComponent(familyId)}/invoice-reconciliations`
}

async function readErrors(response: Response): Promise<Record<string, string[]>> {
  const body = await response.json().catch(() => ({})) as { errors?: Record<string, string[]>; message?: string }
  return body.errors ?? { general: [body.message ?? `Falha na API (HTTP ${response.status}).`] }
}

export async function listInvoiceReconciliations(familyId: string, signal?: AbortSignal): Promise<FinancialInvoiceReconciliation[]> {
  const response = await fetch(`${base(familyId)}/`, { signal, headers })
  if (!response.ok) throw new Error(`Falha ao carregar as conciliações (HTTP ${response.status}).`)
  return await response.json() as FinancialInvoiceReconciliation[]
}

export type CreateReconciliationResult =
  | { ok: true; reconciliation: FinancialInvoiceReconciliation }
  | { ok: false; errors: Record<string, string[]> }

export async function createInvoiceReconciliation(
  familyId: string,
  input: InvoiceReconciliationInput,
): Promise<CreateReconciliationResult> {
  const response = await fetch(`${base(familyId)}/`, {
    method: 'POST',
    headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: JSON.stringify(input),
  })
  if (response.status === 201) return { ok: true, reconciliation: await response.json() as FinancialInvoiceReconciliation }
  if (response.status === 400 || response.status === 409 || response.status === 422) return { ok: false, errors: await readErrors(response) }
  if (response.status === 404) throw new Error('Família, previsão ou fatura não encontrada.')
  throw new Error(`Falha ao criar a conciliação (HTTP ${response.status}).`)
}

export async function removeInvoiceReconciliation(familyId: string, id: string): Promise<{ ok: true } | { ok: false; message: string }> {
  const response = await fetch(`${base(familyId)}/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() },
  })
  if (response.status === 204) return { ok: true }
  if (response.status === 404) return { ok: false, message: 'Conciliação não encontrada ou já removida.' }
  throw new Error(`Falha ao desfazer a conciliação (HTTP ${response.status}).`)
}
