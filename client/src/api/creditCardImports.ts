import { CSRF_HEADER, getCsrfToken } from './csrf'

export type CsvPreviewRow = { rowNumber: number; transactionDate: string | null; description: string; amount: string | null; installmentNumber: number | null; installmentCount: number | null; externalId: string | null; category: string | null; competence: string | null; status: 'Valid' | 'Warning' | 'Invalid'; errors: string[]; warnings: string[] }
export type CsvPreview = { totalRows: number; validRows: number; warningRows: number; invalidRows: number; ignoredRows: number; mapping: Record<string, string | null>; rows: CsvPreviewRow[] }
export type PdfPreviewItem = { rowNumber: number; page: number; sourceText: string; transactionDate: string | null; description: string; amount: string | null; type: string; installmentNumber: number | null; installmentCount: number | null; competence: string | null; status: string; errors: string[]; warnings: string[] }
export type PdfPreview = { fileName: string; pageCount: number; parser: string; statement: { issuer: string | null; cardLastDigits: string | null; competence: string | null; closingDate: string | null; dueDate: string | null; totalAmount: string | null }; summary: { recognized: number; warnings: number; invalid: number; unknown: number }; items: PdfPreviewItem[]; warnings: string[] }
export type ImportResult = { importId: string; status: string; importedCount: number; items: { rowNumber: number; status: string; purchaseId: string | null; errors: string[] }[] }
export type InstallmentMatch = { installmentId: string; invoiceId: string; description: string; amount: string; installmentNumber: number; installmentCount: number; competence: string; transactionDate: string; descriptionSimilar: boolean }
export type ImportedInstallment = { id: string; importId: string; rowNumber: number; invoiceId: string; linkedInstallmentId: string | null; transactionDate: string; description: string; amount: string; installmentNumber: number; installmentCount: number; externalId: string | null; categoryId: string | null; isHistorical: boolean; status: string; createdAt: string; cancelledAt: string | null }
export type ImportError = { errors?: Record<string, string[]>; message?: string }
const headers = { Accept: 'application/json' }
const base = (familyId: string, cardId: string) => `/api/families/${encodeURIComponent(familyId)}/credit-cards/${encodeURIComponent(cardId)}/imports`
async function errorOf(response: Response): Promise<Record<string, string[]>> { const body = await response.json().catch(() => ({})) as ImportError; return body.errors ?? { general: [body.message ?? `Falha na API (HTTP ${response.status}).`] } }
export async function previewCsv(familyId: string, cardId: string, file: File, mapping: Record<string, string>, signal?: AbortSignal) {
  const form = new FormData(); form.append('file', file); Object.entries(mapping).forEach(([key, value]) => { if (value) form.append(key, value) })
  const response = await fetch(`${base(familyId, cardId)}/preview`, { method: 'POST', body: form, signal, headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } })
  if (response.status === 200) return { ok: true as const, value: await response.json() as CsvPreview }; return { ok: false as const, errors: await errorOf(response) }
}
export async function previewPdf(familyId: string, cardId: string, file: File, options: { issuerProfile?: string; competence?: string; dateFormat?: string }, signal?: AbortSignal) {
  const form = new FormData(); form.append('file', file); Object.entries(options).forEach(([key, value]) => { if (value) form.append(key, value) })
  const response = await fetch(`${base(familyId, cardId)}/pdf/preview`, { method: 'POST', body: form, signal, headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } })
  if (response.status === 200) return { ok: true as const, value: await response.json() as PdfPreview }; return { ok: false as const, errors: await errorOf(response) }
}
async function jsonMutation<T>(url: string, body: unknown): Promise<{ ok: true; value: T } | { ok: false; errors: Record<string, string[]> }> { const response = await fetch(url, { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify(body) }); if (response.status === 200) return { ok: true, value: await response.json() as T }; return { ok: false, errors: await errorOf(response) } }
export function confirmCsv(familyId: string, cardId: string, body: unknown) { return jsonMutation<ImportResult>(`${base(familyId, cardId)}/confirm`, body) }
export function findInstallmentMatches(familyId: string, cardId: string, body: unknown) { return jsonMutation<InstallmentMatch[]>(`${base(familyId, cardId)}/installments/matches`, body) }
export function linkImportedInstallment(familyId: string, cardId: string, body: unknown) { return jsonMutation<ImportedInstallment>(`${base(familyId, cardId)}/installments/link`, body) }
export function createHistoricalInstallment(familyId: string, cardId: string, body: unknown) { return jsonMutation<ImportedInstallment>(`${base(familyId, cardId)}/installments/historical`, body) }
export async function listImportedInstallments(familyId: string, cardId: string, signal?: AbortSignal) { const response = await fetch(`${base(familyId, cardId)}/installments/`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar histórico (HTTP ${response.status}).`); return await response.json() as ImportedInstallment[] }
export async function cancelImportedInstallment(familyId: string, cardId: string, id: string) { const response = await fetch(`${base(familyId, cardId)}/installments/${id}`, { method: 'DELETE', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }); if (response.status === 204) return { ok: true as const }; return { ok: false as const, errors: await errorOf(response) } }
