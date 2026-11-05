import { requestJson } from './apiErrors'
import type { PlanActualScopeChoice } from './planActual'

/**
 * R01: blocos da Visão Geral que o servidor compõe (contas com saldo, compromissos da Agenda, metas, orçamento do mês, últimos lançamentos e fila de
 * classificação). Todo valor monetário chega como texto decimal exato e nada é somado ou arredondado no navegador. Cada bloco tem o seu estado: "Unavailable"
 * significa que aquele bloco falhou (nunca zero) e os demais continuam válidos.
 */
export type BlockState = 'Complete' | 'Partial' | 'Unavailable'

export interface OverviewAccount {
  accountId: string; name: string; scope: 'PF' | 'PJ'; type: string; isPrimary: boolean; inScope: boolean
  balanceAvailable: boolean; balance: string | null; unavailableReason: string | null
}
export interface OverviewAccounts { state: BlockState; asOf: string; items: OverviewAccount[] }

export interface OverviewCommitment { id: string; source: string; description: string; date: string; amount: string; accountName: string | null; daysFromToday: number }
export interface OverviewCommitmentGroup { count: number; total: string; items: OverviewCommitment[] }
/** Próxima fatura de um cartão como obrigação de CAIXA pelo vencimento. `status`: Confirmed (total do banco), Provisional (projeção) ou AwaitingBankBill. */
export interface OverviewCardPayment { cardId: string; cardName: string; cardScope: string | null; competence: string; due: string; daysFromToday: number; obligation: string; paid: string; remaining: string; status: 'Confirmed' | 'Provisional' | 'AwaitingBankBill'; paymentState: string }
export interface OverviewCardPayments { state: BlockState; total: string; overdueRemaining: string; overdueCards: number; items: OverviewCardPayment[] }
export interface OverviewCommitments { state: BlockState; asOf: string; horizonThrough: string; horizonDays: number; overdue: OverviewCommitmentGroup; upcoming: OverviewCommitmentGroup; cardPayments?: OverviewCardPayments | null }

export interface OverviewGoal { goalId: string; name: string; priority: string; target: string; allocated: string; remaining: string; progressPercent: string; targetDate: string | null; funded: boolean }
export interface OverviewGoals { state: BlockState; activeCount: number; completedCount: number; items: OverviewGoal[] }

export type BudgetStatus = 'WithinBudget' | 'ApproachingLimit' | 'LimitReached' | 'Exceeded'
/**
 * Uma meta do mês (R03). `group` = Category, FixedExpenses ou AllCards. Consumido = efetivo + cartão lançado; `pending` = previsto adicional. As perspectivas
 * contam os mesmos gastos e nunca são somadas num total global. `reasons` explica dados incompletos (EstimatedCompetence, UndefinedNature, NoCards).
 */
export interface OverviewBudgetTarget {
  targetId: string; group: 'Category' | 'FixedExpenses' | 'AllCards'; categoryId: string | null; name: string; planned: string; consumed: string; pending: string; projected: string
  available: string; availableProjected: string; exceeded: string; percent: string | null; status: BudgetStatus; projectedStatus: BudgetStatus; state: BlockState; reasons: string[]
}
/** Resumo só das metas de categoria (não se sobrepõem, então a soma é válida). */
export interface OverviewBudgetCategories { count: number; planned: string | null; consumed: string | null; exceededCount: number; approachingCount: number; targets: OverviewBudgetTarget[] }
export interface OverviewBudget {
  state: BlockState; hasBudget: boolean; budgetId: string | null; competence: string
  fixed: OverviewBudgetTarget | null; cards: OverviewBudgetTarget | null; categories: OverviewBudgetCategories
}

export interface OverviewRecentItem { id: string; type: 'Income' | 'Expense'; amount: string; date: string; description: string; categoryName: string | null; accountName: string; awaitingClassification: boolean }
export interface OverviewRecent { state: BlockState; from: string; through: string; totalCount: number; items: OverviewRecentItem[] }

export interface OverviewClassification { state: BlockState; needsReview: number; notEvaluated: number }

export interface OverviewTransferLine { direction: 'Received' | 'Sent' | 'Between'; counterpart: string; counterpart2: string | null; amount: string; count: number }
export interface OverviewTransfers {
  state: BlockState; received: string; sent: string; net: string; count: number; items: OverviewTransferLine[]
  externalIncome?: string | null; externalExpenses?: string | null; totalInflows?: string | null; totalOutflows?: string | null; available?: string | null; internalMoved?: string | null
  refunds?: string | null; cashExpenses?: string | null
}

/** Fluxo de caixa (aditivo): ponte de saldo, posição bancária e conciliação. Todos os valores vêm calculados do servidor. */
export interface OverviewCashAccountIssue { accountId: string; name: string; reason: string }
export interface OverviewCashTransfer { id: string; date: string; description: string; source: string; destination: string; amount: string; relation: 'Inbound' | 'Outbound' | 'Internal'; effect: string }
export interface OverviewBankPosition {
  state: 'Complete' | 'Partial' | 'Unavailable'; total: string | null; observedAt: string | null
  accounts: { accountId: string; name: string; amount: string; kind: string; observedAt: string; asOfDate: string; reliability: string }[]; missing: OverviewCashAccountIssue[]
}
export interface OverviewReconciliation { status: 'Verified' | 'Divergent' | 'NotVerifiable'; bank: string | null; calculated: string | null; difference: string | null; cutoffDate: string | null; reasons: string[]; accounts: OverviewCashAccountIssue[] }
export type RecoveryBand = 'Exceeded' | 'Met' | 'Attention' | 'Critical' | 'NoTarget'
/** Recuperação do cheque especial (aditivo; só quando a conta abre a competência com saldo negativo). Tudo vem calculado do servidor. */
export interface OverviewRecovery {
  openingUsed: string; target: string | null; targetState: 'Provisional' | 'Fixed' | 'Pending' | 'NotRegistered'; targetFactor: string | null; capacity: string | null
  targetFixedAt: string | null; fixDate: string; legacyTarget: boolean; pendingReason: string | null
  currentBalance: string; currentAsOf: string; recoveryCurrent: string
  projectedClosing: string | null; projectedRecovery: string | null; projectedAchievementPercent: string | null; band: RecoveryBand
  finalRecovery: string | null; finalAchievementPercent: string | null
}
export interface OverviewCashFlow {
  state: 'Complete' | 'Partial'; competence: string; from: string; through: string; inProgress: boolean; accountCount: number
  opening: string | null; openingMissing: OverviewCashAccountIssue[]
  inflows: { total: string; credits: string; refunds: string | null; transfersReceived: string }
  outflows: { total: string; debits: string; transfersSent: string }
  closing: string | null; identityHolds: boolean | null; snapshotConsistent: boolean
  bankPosition: OverviewBankPosition; reconciliation: OverviewReconciliation; internalMoved: string; transfers: OverviewCashTransfer[]
  byCompetence: { classifiedIncome: string | null; expenses: string | null; difference: string | null }
  recovery?: OverviewRecovery | null
}

/** T11.1: decisão financeira (aditivo; null quando indisponível, servidores antigos não enviam). Tudo calculado no servidor; valores são texto decimal exato, null = indisponível. */
export type DecisionPeriod = 'Current' | 'Future' | 'Closed'
export type CoverageState = 'Covered' | 'CoveredWithCredit' | 'Insufficient' | 'Unavailable'
export interface DecisionContext {
  competence: string; currentCompetence: string; period: DecisionPeriod; scopeKind: 'primary' | 'all' | 'account'; accountId: string | null; accountName: string | null; accountScope: string | null
  referenceDate: string; bankPositionDate: string | null; bankObservedAt: string | null; lastSyncedAt: string | null
  accounts: { accountId: string; name: string; scope: string; isPrimary: boolean }[]; bankBalance: string | null; overdraftUsed: string | null
}
export interface DecisionCommitments {
  period: DecisionPeriod; reliability: 'Complete' | 'Preliminary'; total: string; overdue: string; inCompetence: string; beforeCompetence: string; paidInMonth: string | null
  byNature: { nature: string; overdue: string; inCompetence: string; beforeCompetence: string; total: string; count: number }[]
  byDueDate: { date: string; amount: string }[]; caveats: string[]
}
export interface DecisionAccountCoverage {
  accountId: string; name: string; scope: string; state: CoverageState; reliability: string; guaranteedState: CoverageState; dependsOnInferredIncome: boolean
  minimumBalance: string | null; minimumDate: string | null; firstNegativeDate: string | null; firstBeyondLimitDate: string | null; limit: string | null
  creditNeeded: string | null; deficit: string | null; unavailableReason: string | null; caveats: string[]; projectedClosing: string | null
}
export interface DecisionCoverage {
  state: CoverageState; reliability: string; period: DecisionPeriod; minimumBalance: string | null; firstNegativeDate: string | null; firstBeyondLimitDate: string | null; deficit: string | null
  obligationsConsidered: string; expectedInflows: { date: string; amount: string; kind: 'Income' | 'Transfer'; certainty: 'Registered' | 'Inferred' }[]
  caveats: string[]; unavailableReason: string | null; accounts: DecisionAccountCoverage[]; projectedClosing: string | null
}
export interface DecisionSafeMargin {
  state: 'Calculated' | 'Preliminary' | 'Unavailable'; amount: string | null; scope: 'CashSpend'; referenceDate: string; bindingConstraint: string | null
  constraints: { kind: string; applied: boolean; slack: string | null; binding: boolean; reason: string | null }[]; reasons: string[]; notes: string[]
}
export interface DecisionRecovery {
  state: 'Provisional' | 'Fixed' | 'Pending' | 'NotRegistered' | 'NotApplicable' | 'Unavailable'; reason: string | null; openingUsed: string | null; target: string | null
  targetFixedAt: string | null; legacyTarget: boolean; effectiveRecovery: string | null; projectedRecovery: string | null; gapToTarget: string | null; expectedClosingUsed: string | null; band: RecoveryBand | null
}
export interface FinancialDecision {
  state: 'Complete' | 'Partial'; context: DecisionContext; commitments: DecisionCommitments; coverage: DecisionCoverage; safeMargin: DecisionSafeMargin; recovery: DecisionRecovery
  snapshotConsistent: boolean; notes: string[]
}

export interface DashboardOverview {
  competence: string; asOf: string; scopeKind: 'primary' | 'all' | 'account'; scopeAccountId: string | null
  accounts: OverviewAccounts; commitments: OverviewCommitments; goals: OverviewGoals; budget: OverviewBudget; recent: OverviewRecent; classification: OverviewClassification
  /** Aditivo: servidores antigos não enviam. */
  transfers?: OverviewTransfers | null
  /** Aditivo: fluxo de caixa (null quando indisponível; servidores antigos não enviam). */
  cashFlow?: OverviewCashFlow | null
  /** Aditivo (T11.1): compromissos restantes, cobertura, margem segura e meta, todos da MESMA leitura. */
  decision?: FinancialDecision | null
}

export function getDashboardOverview(workspaceId: string, competence: string, scope: PlanActualScopeChoice, signal?: AbortSignal): Promise<DashboardOverview> {
  const query = new URLSearchParams({ competence, scope: scope.kind })
  if (scope.kind === 'account' && scope.accountId) query.set('accountId', scope.accountId)
  return requestJson<DashboardOverview>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-dashboard/overview?${query}`,
    { signal, headers: { Accept: 'application/json' } }, { fallback: 'Falha ao carregar a Visão Geral.' }).then(data => {
    if (!data?.accounts || !data.commitments || !data.goals || !data.budget || !data.recent || !data.classification) throw new Error('Resposta inesperada da Visão Geral.')
    return data
  })
}
