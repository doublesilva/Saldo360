import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { PdfCategoryDecision, PdfConfirmationResult, PdfPreReconciliation } from './creditCardImports'

export type FinancialDocumentStatus = 'Received' | 'Analyzing' | 'ReadyForReview' | 'ReadyToConfirm' | 'NeedsReview' | 'Conflict' | 'Imported' | 'Failed' | 'Ignored'
export type FinancialDocumentSource = 'Manual' | 'Email'
export type FinancialDocumentType = 'CreditCardStatement' | 'ElectricityBill' | 'WaterBill' | 'TelecomBill' | 'BillingDocument' | 'Unknown'
export type ServiceBillPreview = { result: { documentType: FinancialDocumentType; issuer: string | null; competence: string | null; dueDate: string | null; totalAmount: string | null; accountReference: string | null; serviceReference: string | null; confidence: string; extractionSource: string; failureCode: string | null }; assessment: 'Reliable' | 'NeedsReview' | 'Rejected' }
export type ServiceBillRecurringCandidate = { occurrenceId: string; recurringTemplateId: string; description: string; competence: string; projectedAmount: string; dueDate: string; evidence: string[] }
export type ServiceBillRecurringMatchPreview = {
  extraction: ServiceBillPreview
  match: {
    documentId: string; documentType: FinancialDocumentType; competence: string | null; extractedAmount: string | null
    matchStatus: 'Matched' | 'NotFound' | 'Ambiguous' | 'NeedsReview' | 'Rejected'
    matchedOccurrenceId: string | null; matchedRecurringTemplateId: string | null
    candidates: ServiceBillRecurringCandidate[]; reasons: string[]; confidence: 'High' | 'Medium' | 'Low'
  }
}
export type FinancialDocument = {
  id: string; source: FinancialDocumentSource; originalFileName: string; receivedAt: string; status: FinancialDocumentStatus
  parser: string | null; competence: string | null; creditCardId: string | null; failureCode: string | null; updatedAt: string
  documentType: FinancialDocumentType | null; classificationConfidence: 'High' | 'Medium' | 'Low' | null; classificationSource: 'Deterministic' | 'Agent' | 'Unknown' | null
  classificationFailureCode: string | null; classifiedAt: string | null
}
export type InboxMutation<T> = { ok: true; value: T } | { ok: false; errors: Record<string, string[]>; status: number }

const base = (familyId: string, id?: string) => `/api/families/${encodeURIComponent(familyId)}/financial-documents/inbox${id ? `/${encodeURIComponent(id)}` : ''}`
async function errors(response: Response) { const body = await response.json().catch(() => ({})) as { errors?: Record<string, string[]>; message?: string }; return body.errors ?? { general: [body.message ?? `Falha na API (HTTP ${response.status}).`] } }
async function postForm<T>(url: string, form: FormData): Promise<InboxMutation<T>> {
  const response = await fetch(url, { method: 'POST', body: form, headers: { Accept: 'application/json', [CSRF_HEADER]: await getCsrfToken() } })
  if (response.ok) return { ok: true, value: await response.json() as T }
  return { ok: false, errors: await errors(response), status: response.status }
}
export async function listFinancialDocuments(familyId: string, signal?: AbortSignal) { const response = await fetch(base(familyId), { signal, headers: { Accept: 'application/json' } }); if (!response.ok) throw new Error(`Falha ao carregar documentos (HTTP ${response.status}).`); return await response.json() as FinancialDocument[] }
export function uploadFinancialDocument(familyId: string, file: File, cardId?: string, password?: string) { const form = new FormData(); form.append('file', file); if (cardId) form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<FinancialDocument>(base(familyId), form) }
export function reanalyzeFinancialDocument(familyId: string, id: string, cardId?: string, password?: string) { const form = new FormData(); if (cardId) form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<FinancialDocument>(`${base(familyId, id)}/reanalyze`, form) }
export function reviewFinancialDocument(familyId: string, id: string, cardId: string, password?: string) { const form = new FormData(); form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<PdfPreReconciliation>(`${base(familyId, id)}/pre-reconcile`, form) }
export function previewServiceBill(familyId: string, id: string, password?: string) { const form = new FormData(); if (password) form.append('password', password); return postForm<ServiceBillPreview>(`${base(familyId, id)}/service-bill-preview`, form) }
export function previewServiceBillMatch(familyId: string, id: string, password?: string) { const form = new FormData(); if (password) form.append('password', password); return postForm<ServiceBillRecurringMatchPreview>(`${base(familyId, id)}/service-bill-match-preview`, form) }
export function confirmFinancialDocument(familyId: string, id: string, cardId: string, documentId: string, categories: PdfCategoryDecision[], password?: string) { const form = new FormData(); form.append('creditCardId', cardId); form.append('confirm', 'true'); form.append('documentId', documentId); form.append('categories', JSON.stringify(categories)); if (password) form.append('password', password); return postForm<PdfConfirmationResult>(`${base(familyId, id)}/confirm`, form) }
export async function ignoreFinancialDocument(familyId: string, id: string): Promise<InboxMutation<FinancialDocument>> { const response = await fetch(`${base(familyId, id)}/ignore`, { method: 'POST', headers: { Accept: 'application/json', [CSRF_HEADER]: await getCsrfToken() } }); if (response.ok) return { ok: true, value: await response.json() as FinancialDocument }; return { ok: false, errors: await errors(response), status: response.status } }
