import { requestJson } from './apiErrors'

/**
 * T09.5: saldo informado pelo banco × saldo calculado pelo Saldo360, por conta integrada. Valores como texto decimal exato: aqui só se formata.
 * A diferença é informada e classificada, nunca ajustada.
 */
export type ReconciliationStatus = 'NoDivergence' | 'DivergenceFound' | 'IncompleteHistory' | 'MovementsInReview' | 'InsufficientBankReference'

export interface BankReference {
  amount: string
  kind: 'CurrentBalance' | 'AvailableBalance' | 'Unknown'
  observedAt: string
  asOfDate: string
  source: string
  reliability: 'Reliable' | 'Provisional' | 'Unreliable'
  reliabilityReason: string | null
  overdraft: { contractedLimit: string | null; usedLimit: string | null; note: string } | null
}

export interface AccountReconciliation {
  accountId: string
  name: string
  scope: 'PF' | 'PJ'
  isActive: boolean
  bank: BankReference | null
  calculated: { amount: string | null; asOf: string; openingSource: 'Manual' | 'BalanceAnchor' | 'None'; calculableFrom: string | null; unavailableReason: string | null }
  difference: string | null
  status: ReconciliationStatus
  statusReason: string | null
  explainedByReview: string | null
  unexplained: string | null
  lastSyncedAt: string | null
  linkStatus: string
  coverage: { importedFrom: string | null; importedThrough: string | null; bankSideFrom: string | null; ledgerFrom: string | null; quality: string; postedMovements: number; reason: string | null }
  review: { count: number; netAmount: string; firstDate: string | null; lastDate: string | null; beforeLedgerStart: number; insideLedgerRange: number; pending: number }
  consistency: { checked: number; matching: number; note: string }
}

export interface ScopeSummary { scope: string; accounts: number; bank: string | null; calculated: string | null; difference: string | null; complete: boolean; reasons: string[] }
export interface ReconciliationOverview { today: string; accounts: AccountReconciliation[]; byScope: ScopeSummary[]; consolidated: ScopeSummary }

export interface MonthBalance { month: string; ledgerOpening: string | null; ledgerReason: string | null; bankOpening: string | null; bankReason: string | null; difference: string | null; reviewInMonth: number; reviewNetInMonth: string; blockingReview?: number }
export interface ObservationCheck { asOf: string; observedAt: string; observed: string; ledger: string | null; ledgerDifference: string | null; bankSide: string | null; bankSideDifference: string | null; status: string }
export interface ReconciliationPeriod { accountId: string; name: string; scope: string; from: string; through: string; months: MonthBalance[]; observations: ObservationCheck[] }

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/accounts`
const get = <T>(url: string, signal?: AbortSignal) => requestJson<T>(url, { signal, headers: { Accept: 'application/json' } })

export const getReconciliationOverview = (workspaceId: string, signal?: AbortSignal) => get<ReconciliationOverview>(`${base(workspaceId)}/balance-reconciliation`, signal)

export const getReconciliationPeriod = (workspaceId: string, accountId: string, from: string, through: string, signal?: AbortSignal) =>
  get<ReconciliationPeriod>(`${base(workspaceId)}/${encodeURIComponent(accountId)}/balance-reconciliation?${new URLSearchParams({ from, through })}`, signal)

export const statusLabels: Record<ReconciliationStatus, string> = {
  NoDivergence: 'Sem divergência',
  DivergenceFound: 'Divergência identificada',
  IncompleteHistory: 'Histórico incompleto',
  MovementsInReview: 'Movimentações em revisão',
  InsufficientBankReference: 'Referência bancária insuficiente',
}

export const statusExplanations: Record<string, string> = {
  DifferenceExplainedByReview: 'A diferença é exatamente a soma das movimentações em revisão.',
  ReviewLimitsCalculableHistory: 'Há movimentações em revisão mais antigas: o histórico calculável começa depois delas. O saldo atual confere com o banco.',
  UnexplainedDifference: 'Há diferença que nenhuma movimentação em revisão explica. Nada foi ajustado.',
  PartiallyExplainedByReview: 'Parte da diferença vem de movimentações em revisão; o restante não tem explicação. Nada foi ajustado.',
  CalculatedBalanceUnavailable: 'Não há saldo calculável para comparar.',
  LimitedByBankData: 'O histórico importado não cobre o período.',
  None: 'Ainda não há saldo calculável.',
  NoBankObservation: 'O banco ainda não informou o saldo desta conta.',
  BalanceNatureAmbiguous: 'O saldo informado é incoerente com o cheque especial em uso; a natureza do saldo é incerta.',
  BalanceNatureUnknown: 'A natureza do saldo (contábil ou disponível) não foi informada.',
  ConnectionNotActive: 'A conexão com o banco não está ativa.',
  NeverSyncedCompletely: 'A conta ainda não teve uma sincronização completa.',
  NoImportedHistory: 'Não há movimentações importadas.',
  BalancePrecision: 'O saldo informado tem casas decimais além dos centavos.',
}

export const kindLabels: Record<string, string> = { CurrentBalance: 'Saldo contábil (atual)', AvailableBalance: 'Saldo disponível', Unknown: 'Natureza do saldo desconhecida' }
export const reliabilityLabels: Record<string, string> = { Reliable: 'Confiável', Provisional: 'Provisória', Unreliable: 'Não confiável' }
export const monthReasonLabels: Record<string, string> = {
  BeforeBankHistory: 'Antes do histórico importado', BeforeCalculableStart: 'Antes do histórico calculável', AfterBankReference: 'Depois da observação do banco',
  NoImportedHistory: 'Sem movimentações importadas', NoBankObservation: 'Sem saldo do banco', OpeningBalanceNotConfigured: 'Sem saldo inicial',
}
export const observationStatusLabels: Record<string, string> = {
  Match: 'Confere', LedgerDifference: 'Difere do calculado', ImportedHistoryChanged: 'Histórico importado mudou depois', NotCheckable: 'Sem como conferir', LedgerUnavailable: 'Sem saldo calculável',
}
