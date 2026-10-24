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
export type ServiceBillConfirmation = { occurrenceId: string; competence: string; projectedAmount: string; confirmedAmount: string; difference: string; confirmedAt: string }
export type ServiceBillConfirmationResult = ServiceBillConfirmation & { status: 'Confirmed' | 'AlreadyConfirmed' | 'Conflict'; documentId: string; recurrenceId: string; warnings: string[]; accountReferenceMasked?: string | null; serviceReferenceMasked?: string | null }
export type ServiceAccountBindingResult = { status: 'Created' | 'AlreadyExists' | 'Conflict'; bindingId: string | null; documentType: FinancialDocumentType | ''; accountReferenceMasked: string | null; serviceReferenceMasked: string | null }
export type FinancialDocument = {
  id: string; source: FinancialDocumentSource; originalFileName: string; receivedAt: string; status: FinancialDocumentStatus
  parser: string | null; competence: string | null; creditCardId: string | null; failureCode: string | null; updatedAt: string
  documentType: FinancialDocumentType | null; classificationConfidence: 'High' | 'Medium' | 'Low' | null; classificationSource: 'Deterministic' | 'Agent' | 'Unknown' | null
  classificationFailureCode: string | null; classifiedAt: string | null
  serviceBillConfirmation?: ServiceBillConfirmation | null
}
export type InboxMutation<T> = { ok: true; value: T } | { ok: false; errors: Record<string, string[]>; status: number }
export type StatementCategory = { key: string; itemIndex: number; categoryId: string | null; categoryName: string; isProposal: boolean; confidence: string; source: string }
export type StatementAnalysis = { schemaVersion: string; cardId: string; status: string; failureCode: string | null; passwordResolutionCode?: string | null; consolidationBlockReason?: string | null; categories: StatementCategory[]; extraction: { mode: string; primarySource: string; assessment: string; validationStatus?: string | null; documentWarnings?: string[] | null; attempts: number; agentError: string | null; comparison?: StatementAnalysisComparison | null; primary: { header: { officialTotal: string | null }; items: { originalDescription: string; normalizedDescription: string | null; transactionDate: string | null; amount: string; type: string; nature: string; section: string; includedInCurrentTotal: boolean }[]; reconciliation: { status: string; officialTotal: string | null; componentsTotal: string | null; difference: string | null } } | null } | null }
export type StatementAnalysisResult = { status: string; analysis: StatementAnalysis | null }
export type StatementConsolidationResult = { status: string; invoiceId: string | null; version: number | null; officialTotal: string | null; failureCode: string | null }
export type StatementProcessingResult = { analysis: StatementAnalysisResult; consolidation: StatementConsolidationResult }
export function processStatement(workspaceId: string, id: string, cardId: string, password?: string) { const form = new FormData(); form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<StatementProcessingResult>(`${base(workspaceId, id)}/statement-processing`, form) }
export function consolidateStatement(workspaceId: string, id: string, cardId: string) { const form = new FormData(); form.append('creditCardId', cardId); return postForm<StatementConsolidationResult>(`${base(workspaceId, id)}/statement-consolidation`, form) }
export type StatementAnalysisComparison = { itemsOnlyInParser: string[]; itemsOnlyInAgent: string[] }
export async function getStatementAnalysis(workspaceId: string, id: string, signal?: AbortSignal) { const response = await fetch(`${base(workspaceId, id)}/statement-analysis`, { signal, headers: { Accept: 'application/json' } }); if (response.status === 404) return null; if (!response.ok) throw new Error('Não foi possível consultar a análise.'); return await response.json() as StatementAnalysisResult }
export function analyzeStatement(workspaceId: string, id: string, cardId: string, password?: string) { const form = new FormData(); form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<StatementAnalysisResult>(`${base(workspaceId, id)}/statement-analysis`, form) }
export async function correctStatementCategory(workspaceId: string, id: string, key: string, categoryId: string): Promise<InboxMutation<StatementAnalysisResult>> { const response = await fetch(`${base(workspaceId, id)}/statement-analysis/items/${encodeURIComponent(key)}/category`, { method: 'PUT', headers: { 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify({ categoryId }) }); if (response.ok) return { ok: true, value: await response.json() as StatementAnalysisResult }; return { ok: false, errors: await errors(response), status: response.status } }

const base = (workspaceId: string, id?: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-documents/inbox${id ? `/${encodeURIComponent(id)}` : ''}`
async function errors(response: Response) { const body = await response.json().catch(() => ({})) as { errors?: Record<string, string[]>; message?: string }; return body.errors ?? { general: [body.message ?? `Falha na API (HTTP ${response.status}).`] } }
async function postForm<T>(url: string, form: FormData): Promise<InboxMutation<T>> {
  const response = await fetch(url, { method: 'POST', body: form, headers: { Accept: 'application/json', [CSRF_HEADER]: await getCsrfToken() } })
  if (response.ok) return { ok: true, value: await response.json() as T }
  return { ok: false, errors: await errors(response), status: response.status }
}
export async function listFinancialDocuments(workspaceId: string, signal?: AbortSignal) { const response = await fetch(base(workspaceId), { signal, headers: { Accept: 'application/json' } }); if (!response.ok) throw new Error(`Falha ao carregar documentos (HTTP ${response.status}).`); return await response.json() as FinancialDocument[] }
export async function getFinancialDocument(workspaceId: string, id: string, signal?: AbortSignal) { const response = await fetch(base(workspaceId, id), { signal, headers: { Accept: 'application/json' } }); if (!response.ok) throw new Error(`Falha ao carregar documento (HTTP ${response.status}).`); return await response.json() as FinancialDocument }
export function uploadFinancialDocument(workspaceId: string, file: File, cardId?: string, password?: string) { const form = new FormData(); form.append('file', file); if (cardId) form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<FinancialDocument>(base(workspaceId), form) }
export function reanalyzeFinancialDocument(workspaceId: string, id: string, cardId?: string, password?: string) { const form = new FormData(); if (cardId) form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<FinancialDocument>(`${base(workspaceId, id)}/reanalyze`, form) }
export function reviewFinancialDocument(workspaceId: string, id: string, cardId: string, password?: string) { const form = new FormData(); form.append('creditCardId', cardId); if (password) form.append('password', password); return postForm<PdfPreReconciliation>(`${base(workspaceId, id)}/pre-reconcile`, form) }
export function previewServiceBill(workspaceId: string, id: string, password?: string) { const form = new FormData(); if (password) form.append('password', password); return postForm<ServiceBillPreview>(`${base(workspaceId, id)}/service-bill-preview`, form) }
export function previewServiceBillMatch(workspaceId: string, id: string, password?: string) { const form = new FormData(); if (password) form.append('password', password); return postForm<ServiceBillRecurringMatchPreview>(`${base(workspaceId, id)}/service-bill-match-preview`, form) }
export function confirmServiceBill(workspaceId: string, id: string, password?: string) { const form = new FormData(); if (password) form.append('password', password); return postForm<ServiceBillConfirmationResult>(`${base(workspaceId, id)}/service-bill-confirm`, form) }
export function createServiceAccountBinding(workspaceId: string, id: string, password?: string) { const form = new FormData(); if (password) form.append('password', password); return postForm<ServiceAccountBindingResult>(`${base(workspaceId, id)}/service-account-binding`, form) }
export async function getServiceAccountBinding(workspaceId: string, id: string, signal?: AbortSignal) { const response = await fetch(`${base(workspaceId, id)}/service-account-binding`, { signal, headers: { Accept: 'application/json' } }); if (response.status === 404) return null; if (!response.ok) throw new Error(`Falha ao carregar vínculo (HTTP ${response.status}).`); return await response.json() as ServiceAccountBindingResult }
export function confirmFinancialDocument(workspaceId: string, id: string, cardId: string, documentId: string, categories: PdfCategoryDecision[], password?: string) { const form = new FormData(); form.append('creditCardId', cardId); form.append('confirm', 'true'); form.append('documentId', documentId); form.append('categories', JSON.stringify(categories)); if (password) form.append('password', password); return postForm<PdfConfirmationResult>(`${base(workspaceId, id)}/confirm`, form) }
export async function ignoreFinancialDocument(workspaceId: string, id: string): Promise<InboxMutation<FinancialDocument>> { const response = await fetch(`${base(workspaceId, id)}/ignore`, { method: 'POST', headers: { Accept: 'application/json', [CSRF_HEADER]: await getCsrfToken() } }); if (response.ok) return { ok: true, value: await response.json() as FinancialDocument }; return { ok: false, errors: await errors(response), status: response.status } }
