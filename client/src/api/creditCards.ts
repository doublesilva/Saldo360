import { CSRF_HEADER, getCsrfToken } from './csrf'
import { ApiRequestError, requestJson } from './apiErrors'
import type { StatementProcessingResult } from './financialDocuments'

export type CreditCardStatus = 'Active' | 'Archived'
export type PurchaseStatus = 'Active' | 'Cancelled'
export type InstallmentStatus = 'Active' | 'Cancelled'
export type InvoiceStatus = 'Open' | 'Closed'
export type InvoicePaymentStatus = 'Unpaid' | 'PartiallyPaid' | 'Paid'
export type PaymentStatus = 'Active' | 'Cancelled'

export interface FinancialCreditCard {
  gmailQuery?: string | null
  id: string; name: string; issuer: string; lastFourDigits: string | null; holderUserId: string | null; creditLimit: string
  closingDay: number; dueDay: number; defaultPaymentAccountId: string | null; isArchived: boolean
  createdAt: string; updatedAt: string; archivedAt: string | null
  /** 'Pluggy' quando o cartão nasceu de uma conexão; o escopo (PF/PJ) é o das contas bancárias da mesma conexão e nunca muda com a conta pagadora. */
  origin?: 'Manual' | 'Pluggy'; scope?: 'PF' | 'PJ' | null
}
export interface CreditCardInput { name: string; issuer: string; lastFourDigits: string | null; holderUserId: string | null; creditLimit: string; closingDay: number; dueDay: number; defaultPaymentAccountId: string | null; gmailQuery?: string | null }
export interface CardLatestEmailResult { status: string; cardId: string; documentId: string | null; receivedAt: string | null; fileName: string | null; inboxStatus: string | null; alreadyExisted: boolean; failureCode: string | null; processing?: StatementProcessingResult | null }
export async function searchLatestCardEmail(workspaceId: string, cardId: string): Promise<SaveResult<CardLatestEmailResult> & { status?: number }> {
  const response = await fetch(`${base(workspaceId, cardId)}/imports/email/latest`, { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: '{}' })
  if (response.ok) return { ok: true, value: await response.json() as CardLatestEmailResult }
  return { ok: false, errors: await readError(response), status: response.status }
}
export interface Installment { id: string; number: number; amount: string; status: InstallmentStatus }
export interface CreditCardPurchase { id: string; creditCardId: string; categoryId: string; description: string; purchaseDate: string; totalAmount: string; installmentCount: number; status: PurchaseStatus; installments: Installment[]; previouslyPaidInstallments?: number; firstPendingCompetence?: string | null }
export interface PurchaseInput { description: string; purchaseDate: string; totalAmount: string; installmentCount: number; categoryId: string; amountMode?: 'Total' | 'Installment'; previouslyPaidInstallments?: number; firstPendingCompetence?: string | null }
export interface PurchasePreview { totalAmount: string; installmentCount: number; previouslyPaidInstallments: number; remainingAmount: string; installments: { number: number; amount: string; competence: string; dueDate: string }[] }
export interface InvoiceInstallment { installmentId: string; purchaseId: string; purchaseDescription: string; installmentNumber: number | null; installmentCount: number | null; amount: string }
export interface CreditCardInvoice { id: string; creditCardId: string; competence: string; cycleStartDate: string; closingDate: string; dueDate: string; status: InvoiceStatus; totalAmount: string; paidAmount: string; remainingAmount: string; paymentStatus: InvoicePaymentStatus; installments: InvoiceInstallment[]; installmentCount: number }
export interface InvoicePayment { id: string; invoiceId: string; financialAccountId: string; transactionId: string; operationId: string; paymentDate: string; amount: string; status: PaymentStatus; createdAt: string; cancelledAt: string | null }
export interface CreditCardLimit { creditLimit: string; usedLimit: string; availableLimit: string; overLimitAmount: string }
export interface CardPortfolioItem { invoices?: CreditCardInvoice[]; isArchived: boolean; incompleteDetailsCount: number; paidPeriodAmount: string; outstandingPeriodAmount: string; unlinkedPeriodAmount: string; knownPeriodTotal: string; unassignedCompetenceCount: number; unlinkedObligations: { id: string; description: string; amount: string; competence: string | null; dueDate: string | null; number: number | null; count: number | null; sourceSystem: string | null; externalReference: string | null }[]; cardId: string; creditLimit: string; usedLimit: string; availableLimit: string; utilizationPercentage: string; invoiceTotal: string; unlinkedKnownCount: number; unlinkedKnownAmount: string }
export interface CardPortfolio { periodInvoiceTotal?: string; periodPaidTotal?: string; periodOutstandingTotal?: string; periodUnlinkedTotal?: string; cardCount: number; totalLimit: string; invoiceTotal: string; availableLimit: string; cards: CardPortfolioItem[] }
export interface InvoiceProjectionMonth { competence: string; cycleStartDate: string; closingDate: string; dueDate: string; status: InvoiceStatus; totalAmount: string; installmentCount: number }
export type CreditCardProjection = CreditCardInvoice[]
export type SaveResult<T> = { ok: true; value: T } | { ok: false; errors: Record<string, string[]> }
const headers = { Accept: 'application/json' }
const base = (workspaceId: string, cardId?: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/credit-cards${cardId ? `/${encodeURIComponent(cardId)}` : ''}`
async function readError(response: Response): Promise<Record<string, string[]>> { const body = await response.json().catch(() => ({})) as { errors?: Record<string, string[]>; message?: string }; return body.errors ?? { general: [body.message ?? `Falha na API (HTTP ${response.status}).`] } }
async function mutate<T>(url: string, method: string, body: unknown, expected: number): Promise<SaveResult<T>> {
  const response = await fetch(url, { method, headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify(body) })
  if (response.status === expected) return { ok: true, value: await response.json() as T }
  if (response.status === 400 || response.status === 409 || response.status === 422) return { ok: false, errors: await readError(response) }
  throw new Error(`Falha na API (HTTP ${response.status}).`)
}
export async function listCreditCards(workspaceId: string, includeArchived = true, signal?: AbortSignal) { const response = await fetch(`${base(workspaceId)}?includeArchived=${includeArchived}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar cartões (HTTP ${response.status}).`); return await response.json() as FinancialCreditCard[] }
export async function getCardPortfolio(workspaceId: string, competence: string, signal?: AbortSignal) { const response = await fetch(`${base(workspaceId)}/summary?${new URLSearchParams({ competence })}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar resumo (HTTP ${response.status}).`); const data = await response.json() as CardPortfolio; if (!data || !Array.isArray(data.cards) || !Number.isInteger(data.cardCount) || [data.totalLimit, data.invoiceTotal, data.availableLimit].some(v => typeof v !== 'string' || !/^-?\d+\.\d{2}$/.test(v))) throw new Error('Resumo de cartões inválido.'); return data }
export async function getCreditCard(workspaceId: string, cardId: string, signal?: AbortSignal) { const response = await fetch(base(workspaceId, cardId), { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar cartão (HTTP ${response.status}).`); return await response.json() as FinancialCreditCard }
export function createCreditCard(workspaceId: string, input: CreditCardInput) { return mutate<FinancialCreditCard>(base(workspaceId), 'POST', input, 201) }
export function updateCreditCard(workspaceId: string, cardId: string, input: CreditCardInput) { return mutate<FinancialCreditCard>(base(workspaceId, cardId), 'PUT', input, 200) }
export async function archiveCreditCard(workspaceId: string, cardId: string) { const response = await fetch(base(workspaceId, cardId), { method: 'DELETE', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }); if (response.status === 204) return { ok: true as const }; throw new Error(`Falha ao arquivar cartão (HTTP ${response.status}).`) }
const child = (workspaceId: string, cardId: string, path: string) => `${base(workspaceId, cardId)}/${path}`
export async function listPurchases(workspaceId: string, cardId: string, signal?: AbortSignal) { const response = await fetch(child(workspaceId, cardId, 'purchases'), { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar compras (HTTP ${response.status}).`); return await response.json() as CreditCardPurchase[] }
export function createPurchase(workspaceId: string, cardId: string, input: PurchaseInput) { return mutate<CreditCardPurchase>(child(workspaceId, cardId, 'purchases'), 'POST', input, 201) }
export function updatePurchase(workspaceId: string, cardId: string, purchaseId: string, input: PurchaseInput) { return mutate<CreditCardPurchase>(`${child(workspaceId, cardId, 'purchases')}/${encodeURIComponent(purchaseId)}`, 'PUT', input, 200) }
export function previewPurchase(workspaceId: string, cardId: string, input: PurchaseInput) { return mutate<PurchasePreview>(child(workspaceId, cardId, 'purchases/preview'), 'POST', input, 200) }
export async function cancelPurchase(workspaceId: string, cardId: string, purchaseId: string) { const response = await fetch(`${child(workspaceId, cardId, 'purchases')}/${purchaseId}/cancel`, { method: 'POST', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }); if (response.status === 204) return { ok: true as const }; throw new Error(`Falha ao cancelar compra (HTTP ${response.status}).`) }
export async function listInvoices(workspaceId: string, cardId: string, from: string, through: string, signal?: AbortSignal) { const query = new URLSearchParams({ from, through }); const response = await fetch(`${child(workspaceId, cardId, 'invoices')}?${query}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar faturas (HTTP ${response.status}).`); return await response.json() as CreditCardInvoice[] }
export async function getInvoice(workspaceId: string, cardId: string, invoiceId: string, signal?: AbortSignal) { const response = await fetch(`${child(workspaceId, cardId, 'invoices')}/${invoiceId}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar fatura (HTTP ${response.status}).`); return await response.json() as CreditCardInvoice }
export async function listPayments(workspaceId: string, cardId: string, invoiceId: string, signal?: AbortSignal) { const response = await fetch(`${child(workspaceId, cardId, 'invoices')}/${invoiceId}/payments`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar pagamentos (HTTP ${response.status}).`); return await response.json() as InvoicePayment[] }
export async function createPayment(workspaceId: string, cardId: string, invoiceId: string, input: { operationId: string; financialAccountId: string; paymentDate: string; amount: string }): Promise<SaveResult<InvoicePayment>> {
  try {
    const value = await requestJson<InvoicePayment>(`${child(workspaceId, cardId, 'invoices')}/${invoiceId}/payments`, { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify(input) }, { resultUncertainOnTimeout: true, fallback: 'Falha ao registrar o pagamento da fatura.' })
    return { ok: true, value }
  } catch (error) {
    if (error instanceof ApiRequestError && (error.kind === 'validation' || error.kind === 'conflict' || error.kind === 'business')) return { ok: false, errors: Object.keys(error.fieldErrors).length > 0 ? error.fieldErrors : { general: [error.message] } }
    throw error
  }
}
export async function cancelPayment(workspaceId: string, cardId: string, invoiceId: string, paymentId: string) { return await requestJson<InvoicePayment>(`${child(workspaceId, cardId, 'invoices')}/${invoiceId}/payments/${paymentId}/cancel`, { method: 'POST', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }, { resultUncertainOnTimeout: true, fallback: 'Falha ao cancelar o pagamento.' }) }
export async function getCreditCardLimit(workspaceId: string, cardId: string, signal?: AbortSignal) { const response = await fetch(`${base(workspaceId, cardId)}/limit`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar limite (HTTP ${response.status}).`); return await response.json() as CreditCardLimit }
export async function getCreditCardProjection(workspaceId: string, cardId: string, from: string, months = 6, signal?: AbortSignal) { const query = new URLSearchParams({ from, months: String(months) }); const response = await fetch(`${base(workspaceId, cardId)}/invoice-projection?${query}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar projeção (HTTP ${response.status}).`); return await response.json() as CreditCardProjection }
