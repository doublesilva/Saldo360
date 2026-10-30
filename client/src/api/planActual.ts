import { requestJson } from './apiErrors'

/**
 * T10.1: Dashboard Planejado × Projetado × Realizado. Todo valor chega do backend como texto decimal exato e nenhuma fórmula financeira roda no
 * navegador: aqui só há tipos e chamadas. "NotDefined" (planejado) é diferente de zero.
 */
export type PlanActualScopeKind = 'primary' | 'all' | 'account'
export type PlanActualState = 'Complete' | 'Partial' | 'Unavailable'

export interface ScopeAccount { accountId: string; name: string; scope: 'PF' | 'PJ'; isPrimary: boolean; isActive: boolean }
export interface PlanActualScope {
  requested: PlanActualScopeKind; kind: PlanActualScopeKind; accountId: string | null; accountName: string | null; accountScope: string | null
  primaryAccountId: string | null; primaryAccountName: string | null; primaryMissing: boolean; accounts: ScopeAccount[]
}
export interface PlanComponent { code: string; amount: string }
export interface PlanIndicator {
  state: PlanActualState; plannedState: 'Defined' | 'NotDefined'; planned: string | null; projected: string; realized: string; pending: string
  realizedDifference: string | null; projectedDifference: string | null; executionPercent: string | null; projectedPercent: string | null
  plannedSource: string | null; components: PlanComponent[]; reasons: string[]
}
export interface PlanExpenses { primaryPerspective: 'Consumption' | 'CashFlow'; consumption: PlanIndicator; cashFlow: PlanIndicator }
export interface OverdraftAccount {
  accountId: string; name: string; state: string; contractedLimit: string | null; usedLatest: string; observedAt: string | null; observedAsOf: string | null
  usedAtStart: string | null; startAsOf: string | null; startApproximate: boolean; effectiveRecovery: string | null; direction: 'Recovered' | 'Increased' | 'Stable' | null
  fullyRecovered: boolean; projectedUsedAtEnd: string | null; projectedRecovery: string | null; projectedRecoveryFromNow: string | null
  projectedBalanceAtEnd: string | null; exceedsLimit: boolean; reasons: string[]
}
export interface PlanOverdraft {
  eligible: boolean; state: string; usedAtStart: string | null; usedLatest: string | null; effectiveRecovery: string | null; projectedUsedAtEnd: string | null
  projectedRecovery: string | null; projectedRecoveryFromNow: string | null; contractedLimit: string | null; periodKind: string; accounts: OverdraftAccount[]; reasons: string[]
}
export interface CardTargetCard { cardId: string; name: string; scope: string | null; payerAccountId: string | null; confirmed: string; pendingKnown: string; estimated: string; projected: string }
export interface PlanCards {
  applicable: boolean; state: PlanActualState; plannedState: 'Defined' | 'NotDefined'; target: string | null; targetSource: string | null; targetPartial: boolean
  confirmed: string; pendingKnown: string; estimated: string; projected: string; remaining: string | null; excessProjected: string | null; excessConfirmed: string | null
  confirmedPercent: string | null; projectedPercent: string | null; financeCharges: string; cards: CardTargetCard[]; payerAccountIds: string[]; reasons: string[]
}
export interface PlanSituation {
  state: PlanActualState; realizedBalance: string | null; partialRealizedBalance: string | null; consideredAccounts: number; unavailableAccounts: number
  projectedClosing: string | null; minimumProjected: string | null; minimumProjectedDate: string | null; pendingExpense: string; pendingIncome: string; carryoverExpense: string; carryoverIncome: string
  negativeRiskDate: string | null; latestBankUpdate: string | null; reasons: string[]
}
export interface CoverageAccount {
  accountId: string; name: string; reconciliationStatus: string | null; reviewCount: number; reviewInMonth: number; referenceReliability: string | null
  balanceAvailable: boolean; partialPeriod: boolean
}
export interface PlanCoverage {
  complete: boolean; reviewCount: number; reviewInMonth: number; cardReviewCount: number; reviewAffectsConfidence: boolean; partialPeriod: boolean
  accounts: CoverageAccount[]; reasons: string[]
}
export interface SyncConnectionSummary { connectionId: string; label: string | null; status: string; lastSuccessAt: string | null; lastAttemptAt: string | null; nextAttemptAt: string | null; consentExpiringSoon: boolean }
export interface PlanSync {
  autoEnabled: boolean; autoIntervalHours: number; oldestSuccessAt: string | null; latestSuccessAt: string | null; sameMoment: boolean; stale: boolean; state: string
  nextAttemptAt: string | null; connections: SyncConnectionSummary[]
}
export interface PlanActualDashboard {
  competence: string; asOf: string; periodKind: 'Past' | 'Current' | 'Future'; scope: PlanActualScope; income: PlanIndicator; expenses: PlanExpenses
  overdraft: PlanOverdraft; cards: PlanCards; situation: PlanSituation; coverage: PlanCoverage; sync: PlanSync | null; snapshotConsistent: boolean
}

export interface PlanActualScopeChoice { kind: PlanActualScopeKind; accountId?: string }

export function getPlanActual(workspaceId: string, competence: string, scope: PlanActualScopeChoice, signal?: AbortSignal): Promise<PlanActualDashboard> {
  const query = new URLSearchParams({ competence, scope: scope.kind })
  if (scope.kind === 'account' && scope.accountId) query.set('accountId', scope.accountId)
  return requestJson<PlanActualDashboard>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-dashboard/plan-actual?${query}`,
    { signal, headers: { Accept: 'application/json' } }, { fallback: 'Falha ao carregar o planejado × realizado.' }).then(data => {
    // Resposta fora do contrato (proxy, versão antiga da API): erro claro em vez de um painel quebrado.
    if (!data?.scope || !data.income || !data.expenses || !data.cards || !data.situation || !data.coverage) throw new Error('Resposta inesperada do planejado × realizado.')
    return data
  })
}
