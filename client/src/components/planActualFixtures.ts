import type { PlanActualDashboard, PlanIndicator } from '../api/planActual'

const indicator = (overrides: Partial<PlanIndicator> = {}): PlanIndicator => ({
  state: 'Complete', plannedState: 'Defined', planned: '2500.00', projected: '2450.00', realized: '450.00', pending: '2000.00', realizedDifference: '-2050.00',
  projectedDifference: '-50.00', executionPercent: '18.0', projectedPercent: '98.0', plannedSource: 'PlannedTransactions',
  components: [{ code: 'PendingPlanned', amount: '2000.00' }], reasons: [], ...overrides,
})

/** Retrato sintético do Dashboard Planejado × Projetado × Realizado: conta principal, nenhuma ressalva. Os testes sobrescrevem só o que importa. */
export const planActual: PlanActualDashboard = {
  competence: '2026-10', asOf: '2026-10-16', periodKind: 'Current',
  scope: {
    requested: 'primary', kind: 'primary', accountId: 'pf', accountName: 'Conta A', accountScope: 'PF', primaryAccountId: 'pf', primaryAccountName: 'Conta A', primaryMissing: false,
    accounts: [{ accountId: 'pf', name: 'Conta A', scope: 'PF', isPrimary: true, isActive: true }, { accountId: 'pj', name: 'Conta B', scope: 'PJ', isPrimary: false, isActive: true }],
  },
  income: indicator(),
  expenses: {
    primaryPerspective: 'Consumption',
    consumption: indicator({ plannedState: 'NotDefined', planned: null, realizedDifference: null, projectedDifference: null, executionPercent: null, projectedPercent: null, plannedSource: null, realized: '1200.00', projected: '1500.00', pending: '300.00', reasons: ['PlanNotAttributable'] }),
    cashFlow: indicator({ planned: '900.00', realized: '700.00', projected: '1000.00', pending: '300.00', realizedDifference: '-200.00', projectedDifference: '100.00', executionPercent: '77.8', projectedPercent: '111.1' }),
  },
  overdraft: { eligible: false, state: 'Unavailable', usedAtStart: null, usedLatest: null, effectiveRecovery: null, projectedUsedAtEnd: null, projectedRecovery: null, projectedRecoveryFromNow: null, contractedLimit: null, periodKind: 'Current', accounts: [], reasons: [] },
  cards: {
    applicable: true, state: 'Complete', plannedState: 'Defined', target: '5000.00', targetSource: 'CardTarget', targetPartial: false, confirmed: '4300.00', pendingKnown: '500.00', estimated: '0.00', projected: '4800.00',
    remaining: '200.00', excessProjected: '0.00', excessConfirmed: '0.00', confirmedPercent: '86.0', projectedPercent: '96.0', financeCharges: '0.00',
    cards: [{ cardId: 'c1', name: 'Cartão Itaú', scope: 'PF', payerAccountId: 'pf', confirmed: '4300.00', pendingKnown: '500.00', estimated: '0.00', projected: '4800.00' }], payerAccountIds: ['pf'], reasons: [],
  },
  situation: {
    state: 'Complete', realizedBalance: '3000.00', partialRealizedBalance: '3000.00', consideredAccounts: 1, unavailableAccounts: 0, projectedClosing: '1200.00', minimumProjected: '900.00',
    minimumProjectedDate: '2026-10-25', pendingExpense: '300.00', pendingIncome: '0.00', carryoverExpense: '0.00', carryoverIncome: '0.00', negativeRiskDate: null, latestBankUpdate: '2026-10-16T09:00:00Z', reasons: [],
  },
  coverage: { complete: true, reviewCount: 0, reviewInMonth: 0, cardReviewCount: 0, reviewAffectsConfidence: false, partialPeriod: false, accounts: [], reasons: [] },
  sync: null, snapshotConsistent: true,
}

export const planActualIndicator = indicator
