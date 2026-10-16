import { CSRF_HEADER, getCsrfToken } from './csrf'

export type RecurrenceType = 'Income' | 'Expense'
export type RecurrenceFrequency = 'Weekly' | 'Monthly' | 'Yearly'
export type RecurrenceStatus = 'Active' | 'Paused' | 'Ended' | 'Archived'
export type PlannedStatus = 'Pending' | 'Realized' | 'Cancelled'

export interface FinancialRecurrence {
  id: string; familyId: string; accountId: string; categoryId: string; type: RecurrenceType; description: string
  amount: string; isEstimated: boolean; frequency: RecurrenceFrequency; interval: number; startDate: string
  endDate: string | null; occurrenceCount: number | null; dayOfMonth: number; status: RecurrenceStatus
  currency: string; createdAt: string; updatedAt: string; archivedAt: string | null
}
export interface RecurrenceOccurrence { recurrenceId: string; sequence: number; date: string; amount: string; description: string; type: RecurrenceType; isEstimated: boolean; accountId: string; categoryId: string }
export interface RecurrenceMaterialization { occurrenceNumber: number; scheduledDate: string; plannedTransactionId: string | null; plannedDate: string | null; amount: string | null; status: PlannedStatus | null }
export interface MaterializationResult { recurrenceId: string; createdCount: number; existingCount: number; items: Array<{ occurrenceNumber: number; scheduledDate: string; plannedTransactionId: string | null; plannedDate: string | null; amount: string | null; status: PlannedStatus | null; result: string }> }
export interface RecurrenceInput { accountId: string; categoryId: string; type: RecurrenceType; description: string; amount: string; isEstimated: boolean; frequency: RecurrenceFrequency; interval: number; startDate: string; endDate: string | null; occurrenceCount: number | null; dayOfMonth: number | null }

const headers = { Accept: 'application/json' }
const base = (familyId: string, id?: string) => `/api/families/${encodeURIComponent(familyId)}/recurrences${id ? `/${encodeURIComponent(id)}` : ''}`
async function problem(response: Response): Promise<Record<string, string[]>> { const body = await response.json().catch(() => ({})) as { errors?: Record<string, string[]>; title?: string }; return body.errors ?? { general: [body.title ?? `Falha na operação (HTTP ${response.status}).`] } }
async function mutate<T>(url: string, method: string, body?: unknown): Promise<T> { const response = await fetch(url, { method, headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: body === undefined ? undefined : JSON.stringify(body) }); if (!response.ok) { const errors = await problem(response); throw Object.assign(new Error(Object.values(errors).flat()[0] ?? 'Falha na operação.'), { errors, status: response.status }) } return await response.json() as T }

export async function listRecurrences(familyId: string, includeArchived = true, signal?: AbortSignal) { const response = await fetch(`${base(familyId)}?includeArchived=${includeArchived}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar recorrências (HTTP ${response.status}).`); return await response.json() as FinancialRecurrence[] }
export async function createRecurrence(familyId: string, input: RecurrenceInput) { return mutate<FinancialRecurrence>(base(familyId), 'POST', input) }
export async function updateRecurrence(familyId: string, id: string, input: RecurrenceInput) { return mutate<FinancialRecurrence>(base(familyId, id), 'PUT', input) }
export async function changeRecurrence(familyId: string, id: string, action: 'pause' | 'resume' | 'end') { return mutate<FinancialRecurrence>(`${base(familyId, id)}/${action}`, 'POST') }
export async function archiveRecurrence(familyId: string, id: string) { const response = await fetch(base(familyId, id), { method: 'DELETE', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }); if (!response.ok) throw new Error((await problem(response)).general?.[0] ?? `Falha ao arquivar (HTTP ${response.status}).`); return true }
export async function listOccurrences(familyId: string, id: string, from: string, through: string, signal?: AbortSignal) { const response = await fetch(`${base(familyId, id)}/occurrences?from=${encodeURIComponent(from)}&through=${encodeURIComponent(through)}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar ocorrências (HTTP ${response.status}).`); return await response.json() as RecurrenceOccurrence[] }
export async function listMaterializations(familyId: string, id: string, from: string, through: string, signal?: AbortSignal) { const response = await fetch(`${base(familyId, id)}/materializations?from=${encodeURIComponent(from)}&through=${encodeURIComponent(through)}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar materializações (HTTP ${response.status}).`); return await response.json() as RecurrenceMaterialization[] }
export async function materializeRecurrence(familyId: string, id: string, from: string, through: string) { return mutate<MaterializationResult>(`${base(familyId, id)}/materialize`, 'POST', { from, through }) }
