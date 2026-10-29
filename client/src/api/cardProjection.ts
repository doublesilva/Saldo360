import { ApiRequestError, requestJson } from './apiErrors'

/**
 * T09.4: obrigações dos cartões. Todos os valores vêm do backend como texto decimal exato ("1234.56"): aqui só se formata, nunca se soma nem se converte.
 * Fechamento (cycle), pagamento (paymentState) e conciliação (reconciliation) são dimensões independentes.
 */
export type InvoiceCycle = 'Open' | 'Closed'
export type InvoicePaymentState = 'Unpaid' | 'Partial' | 'Paid' | 'Overpaid'
export type ObligationBasis = 'BankTotal' | 'ProjectedOpen' | 'ProjectedFuture'

export interface PayerInfo { accountId: string; name: string; scope: 'PF' | 'PJ' | null; isPrimary: boolean }
export interface CardCoverage { complete: boolean; reasons: string[] }
export interface InvoiceComponents { composition: string; pendingKnown: string; knownInstallments: string; estimated: string }

export interface CardInvoiceView {
  invoiceId: string | null
  competence: string
  cycleStart: string
  closing: string
  due: string
  cycle: InvoiceCycle
  phase: 'Closed' | 'Open' | 'Future'
  basis: ObligationBasis
  provisional: boolean
  awaitingBankBill: boolean
  bankTotal: string | null
  bankTotalSource: string | null
  projectedTotal: string
  obligation: string
  paid: string
  remaining: string
  paymentState: InvoicePaymentState
  reconciliation: string
  reconciliationReason: string | null
  difference: string | null
  bankReportedPaid: string | null
  components: InvoiceComponents
  flags: string[]
  absorbedItems: number
  unprojectedInstallments: number
}

export interface CardOverviewItem {
  cardId: string
  name: string
  issuer: string
  lastFour: string | null
  scope: 'PF' | 'PJ' | null
  origin: 'Manual' | 'Pluggy'
  creditLimit: string
  availableLimit: string
  limitSource: 'Bank' | 'Saldo360'
  payer: PayerInfo | null
  sync: { status: string; lastSyncedAt: string | null }
  schedule: { closingDay: number; dueDay: number; source: string; strong: boolean }
  currentInvoice: CardInvoiceView | null
  nextDue: CardInvoiceView | null
  overdueRemaining: string
  invoices: CardInvoiceView[]
  coverage: CardCoverage
}

export interface InvoiceOverview {
  competence: string
  today: string
  cards: CardOverviewItem[]
  obligation: string
  paid: string
  remaining: string
  coverage: CardCoverage
}

export interface MonthlyCard { cardId: string; name: string; scope: 'PF' | 'PJ' | null; payerAccountId: string | null; obligation: string; paid: string; remaining: string; confirmed: string; projected: string; estimated: string }
export interface MonthlyPayer { accountId: string | null; name: string | null; scope: 'PF' | 'PJ' | null; obligation: string; paid: string; remaining: string; cardIds: string[] }
export interface MonthlyProjection {
  month: string
  confirmedInvoices: string
  openInvoicesProjected: string
  futureInstallments: string
  estimatedCommitments: string
  knownCommitments: string
  estimatedIncluded: string
  obligation: string
  paid: string
  remaining: string
  cards: number
  coverage: CardCoverage
  byCard: MonthlyCard[]
  byPayer: MonthlyPayer[]
}
export interface ObligationProjection { from: string; months: number; today: string; carryover: string; items: MonthlyProjection[]; coverage: CardCoverage }

export type ChainStatus = 'Complete' | 'Estimated' | 'Ambiguous' | 'Interrupted' | 'InsufficientEvidence' | 'GapDetected'
export type InstallmentOrigin = 'InInvoice' | 'Known' | 'Estimated'
export interface ProjectedInstallment { number: number; total: number; amount: string; closing: string; due: string; origin: InstallmentOrigin; absorbed: boolean }
export interface InstallmentChain { key: string; description: string; total: number; amount: string; status: ChainStatus; reason: string | null; estimated: number; unprojected: number; installments: ProjectedInstallment[] }
export interface CardInstallments { cardId: string; name: string; today: string; chains: InstallmentChain[]; coverage: CardCoverage }

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/credit-cards`
const get = <T>(url: string, signal?: AbortSignal) => requestJson<T>(url, { signal, headers: { Accept: 'application/json' } })

export const getInvoiceOverview = (workspaceId: string, competence: string, signal?: AbortSignal) =>
  get<InvoiceOverview>(`${base(workspaceId)}/invoice-overview?${new URLSearchParams({ competence })}`, signal)

export const getObligationProjection = (workspaceId: string, from: string, months: number, signal?: AbortSignal) =>
  get<ObligationProjection>(`${base(workspaceId)}/obligation-projection?${new URLSearchParams({ from, months: String(months) })}`, signal)

export const getProjectedInstallments = (workspaceId: string, cardId: string, signal?: AbortSignal) =>
  get<CardInstallments>(`${base(workspaceId)}/${encodeURIComponent(cardId)}/projected-installments`, signal)

export { ApiRequestError }

export const cycleLabels: Record<InvoiceCycle, string> = { Open: 'Aberta', Closed: 'Fechada pelo banco' }
export const paymentStateLabels: Record<InvoicePaymentState, string> = { Unpaid: 'Não paga', Partial: 'Parcialmente paga', Paid: 'Paga', Overpaid: 'Pagamento excedente' }
export const basisLabels: Record<ObligationBasis, string> = {
  BankTotal: 'Total do banco (confirmado)', ProjectedOpen: 'Projeção da fatura aberta (provisória)', ProjectedFuture: 'Projeção futura (provisória)',
}
export const originLabels: Record<InstallmentOrigin, string> = { InInvoice: 'Na fatura', Known: 'Informada pelo banco', Estimated: 'Estimada' }
export const chainStatusLabels: Record<ChainStatus, string> = {
  Complete: 'Completa', Estimated: 'Com parcelas estimadas', Ambiguous: 'Ambígua — revisar', Interrupted: 'Série interrompida', InsufficientEvidence: 'Sem evidência para estimar', GapDetected: 'Parcela passada ausente',
}
export const syncLabels: Record<string, string> = { Active: 'Sincronizado', ConsentExpired: 'Consentimento expirado', Attention: 'Atenção necessária', None: 'Sem integração' }
export const coverageReasonLabels: Record<string, string> = {
  AwaitingBankBill: 'O fechamento passou e o banco ainda não informou a fatura.',
  UnprojectedInstallments: 'Há parcelas restantes sem evidência suficiente para projetar.',
  AmbiguousInstallments: 'Compras parceladas ambíguas ficaram de fora das estimativas.',
  WeakSchedule: 'O ciclo do cartão foi deduzido sem um histórico consistente de faturas.',
  MissingPayerAccount: 'Há cartão sem conta pagadora definida.',
  KnownWithoutDueDate: 'Há obrigação conhecida sem data de vencimento.',
  SyncAttention: 'A sincronização de algum cartão precisa de atenção.',
  NoBankHistory: 'Sem faturas do banco para algum cartão.',
}
export const coverageText = (reasons: string[]): string[] => reasons.map(reason => coverageReasonLabels[reason] ?? reason)
