import { CSRF_HEADER, getCsrfToken } from './csrf'

export type CreditCardStatus = 'Active' | 'Archived'
export type PurchaseStatus = 'Active' | 'Cancelled'
export type InstallmentStatus = 'Pending' | 'Cancelled'
export type InvoiceStatus = 'Open' | 'Closed'
export type InvoicePaymentStatus = 'Unpaid' | 'PartiallyPaid' | 'Paid'
export type PaymentStatus = 'Active' | 'Cancelled'

export interface FinancialCreditCard {
  id: string; name: string; issuer: string; holderUserId: string | null; creditLimit: string
  closingDay: number; dueDay: number; defaultPaymentAccountId: string | null; isArchived: boolean
  createdAt: string; updatedAt: string; archivedAt: string | null
}
export interface CreditCardInput { name: string; issuer: string; holderUserId: string | null; creditLimit: string; closingDay: number; dueDay: number; defaultPaymentAccountId: string | null }
export interface Installment { id: string; number: number; amount: string; status: InstallmentStatus }
export interface CreditCardPurchase { id: string; creditCardId: string; categoryId: string; description: string; purchaseDate: string; totalAmount: string; installmentCount: number; status: PurchaseStatus; installments: Installment[] }
export interface InvoiceInstallment { installmentId: string; purchaseId: string; purchaseDescription: string; installmentNumber: number; installmentCount: number; amount: string }
export interface CreditCardInvoice { id: string; creditCardId: string; competence: string; cycleStartDate: string; closingDate: string; dueDate: string; status: InvoiceStatus; totalAmount: string; paidAmount: string; remainingAmount: string; paymentStatus: InvoicePaymentStatus; installments: InvoiceInstallment[]; installmentCount: number }
export interface InvoicePayment { id: string; invoiceId: string; financialAccountId: string; transactionId: string; paymentDate: string; amount: string; status: PaymentStatus; createdAt: string; cancelledAt: string | null }
export interface CreditCardLimit { creditLimit: string; usedLimit: string; availableLimit: string; overLimitAmount: string }
export interface InvoiceProjectionMonth { competence: string; cycleStartDate: string; closingDate: string; dueDate: string; status: InvoiceStatus; totalAmount: string; installmentCount: number }
export type CreditCardProjection = CreditCardInvoice[]
export type SaveResult<T> = { ok: true; value: T } | { ok: false; errors: Record<string, string[]> }
const headers = { Accept: 'application/json' }
const base = (familyId: string, cardId?: string) => `/api/families/${encodeURIComponent(familyId)}/credit-cards${cardId ? `/${encodeURIComponent(cardId)}` : ''}`
async function readError(response: Response): Promise<Record<string, string[]>> { const body = await response.json().catch(() => ({})) as { errors?: Record<string, string[]>; message?: string }; return body.errors ?? { general: [body.message ?? `Falha na API (HTTP ${response.status}).`] } }
async function mutate<T>(url: string, method: string, body: unknown, expected: number): Promise<SaveResult<T>> {
  const response = await fetch(url, { method, headers: { ...headers, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify(body) })
  if (response.status === expected) return { ok: true, value: await response.json() as T }
  if (response.status === 400 || response.status === 409 || response.status === 422) return { ok: false, errors: await readError(response) }
  throw new Error(`Falha na API (HTTP ${response.status}).`)
}
export async function listCreditCards(familyId: string, includeArchived = true, signal?: AbortSignal) { const response = await fetch(`${base(familyId)}?includeArchived=${includeArchived}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar cartões (HTTP ${response.status}).`); return await response.json() as FinancialCreditCard[] }
export async function getCreditCard(familyId: string, cardId: string, signal?: AbortSignal) { const response = await fetch(base(familyId, cardId), { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar cartão (HTTP ${response.status}).`); return await response.json() as FinancialCreditCard }
export function createCreditCard(familyId: string, input: CreditCardInput) { return mutate<FinancialCreditCard>(base(familyId), 'POST', input, 201) }
export function updateCreditCard(familyId: string, cardId: string, input: CreditCardInput) { return mutate<FinancialCreditCard>(base(familyId, cardId), 'PUT', input, 200) }
export async function archiveCreditCard(familyId: string, cardId: string) { const response = await fetch(base(familyId, cardId), { method: 'DELETE', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }); if (response.status === 204) return { ok: true as const }; throw new Error(`Falha ao arquivar cartão (HTTP ${response.status}).`) }
const child = (familyId: string, cardId: string, path: string) => `${base(familyId, cardId)}/${path}`
export async function listPurchases(familyId: string, cardId: string, signal?: AbortSignal) { const response = await fetch(child(familyId, cardId, 'purchases'), { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar compras (HTTP ${response.status}).`); return await response.json() as CreditCardPurchase[] }
export function createPurchase(familyId: string, cardId: string, input: { description: string; purchaseDate: string; totalAmount: string; installmentCount: number; categoryId: string }) { return mutate<CreditCardPurchase>(child(familyId, cardId, 'purchases'), 'POST', input, 201) }
export async function cancelPurchase(familyId: string, cardId: string, purchaseId: string) { const response = await fetch(`${child(familyId, cardId, 'purchases')}/${purchaseId}/cancel`, { method: 'POST', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }); if (response.status === 204) return { ok: true as const }; throw new Error(`Falha ao cancelar compra (HTTP ${response.status}).`) }
export async function listInvoices(familyId: string, cardId: string, from: string, through: string, signal?: AbortSignal) { const query = new URLSearchParams({ from, through }); const response = await fetch(`${child(familyId, cardId, 'invoices')}?${query}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar faturas (HTTP ${response.status}).`); return await response.json() as CreditCardInvoice[] }
export async function getInvoice(familyId: string, cardId: string, invoiceId: string, signal?: AbortSignal) { const response = await fetch(`${child(familyId, cardId, 'invoices')}/${invoiceId}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar fatura (HTTP ${response.status}).`); return await response.json() as CreditCardInvoice }
export async function listPayments(familyId: string, cardId: string, invoiceId: string, signal?: AbortSignal) { const response = await fetch(`${child(familyId, cardId, 'invoices')}/${invoiceId}/payments`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar pagamentos (HTTP ${response.status}).`); return await response.json() as InvoicePayment[] }
export function createPayment(familyId: string, cardId: string, invoiceId: string, input: { financialAccountId: string; paymentDate: string; amount: string }) { return mutate<InvoicePayment>(`${child(familyId, cardId, 'invoices')}/${invoiceId}/payments`, 'POST', input, 201) }
export async function cancelPayment(familyId: string, cardId: string, invoiceId: string, paymentId: string) { const response = await fetch(`${child(familyId, cardId, 'invoices')}/${invoiceId}/payments/${paymentId}/cancel`, { method: 'POST', headers: { ...headers, [CSRF_HEADER]: await getCsrfToken() } }); if (!response.ok) throw new Error(`Falha ao cancelar pagamento (HTTP ${response.status}).`); return await response.json() as InvoicePayment }
export async function getCreditCardLimit(familyId: string, cardId: string, signal?: AbortSignal) { const response = await fetch(`${base(familyId, cardId)}/limit`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar limite (HTTP ${response.status}).`); return await response.json() as CreditCardLimit }
export async function getCreditCardProjection(familyId: string, cardId: string, from: string, months = 6, signal?: AbortSignal) { const query = new URLSearchParams({ from, months: String(months) }); const response = await fetch(`${base(familyId, cardId)}/invoice-projection?${query}`, { signal, headers }); if (!response.ok) throw new Error(`Falha ao carregar projeção (HTTP ${response.status}).`); return await response.json() as CreditCardProjection }
