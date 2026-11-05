import type { DecisionAccountCoverage, FinancialDecision } from '../../api/overview'

/** Retratos sintéticos da decisão financeira (T11.1), no formato exato do servidor. */
export const accountCoverage = (over: Partial<DecisionAccountCoverage> = {}): DecisionAccountCoverage => ({
  accountId: 'a1', name: 'Itaú PF', scope: 'PF', state: 'Covered', reliability: 'Complete', guaranteedState: 'Covered', dependsOnInferredIncome: false,
  minimumBalance: '400.00', minimumDate: '2026-11-20', firstNegativeDate: null, firstBeyondLimitDate: null, limit: '5000.00', creditNeeded: null, deficit: null,
  unavailableReason: null, caveats: [], projectedClosing: '900.00', ...over,
})

export function decision(over: Partial<FinancialDecision> = {}): FinancialDecision {
  return {
    state: 'Complete',
    context: {
      competence: '2026-11', currentCompetence: '2026-11', period: 'Current', scopeKind: 'account', accountId: 'a1', accountName: 'Itaú PF', accountScope: 'PF', referenceDate: '2026-11-05',
      bankPositionDate: '2026-11-04', bankObservedAt: '2026-11-04T12:00:00Z', lastSyncedAt: '2026-11-04T12:30:00Z', accounts: [{ accountId: 'a1', name: 'Itaú PF', scope: 'PF', isPrimary: true }],
      bankBalance: '-1000.00', overdraftUsed: '1000.00',
    },
    commitments: {
      period: 'Current', reliability: 'Complete', total: '600.00', overdue: '100.00', inCompetence: '500.00', beforeCompetence: '0.00', paidInMonth: null,
      byNature: [{ nature: 'Expenses', overdue: '100.00', inCompetence: '200.00', beforeCompetence: '0.00', total: '300.00', count: 2 }, { nature: 'CardInvoices', overdue: '0.00', inCompetence: '300.00', beforeCompetence: '0.00', total: '300.00', count: 1 }],
      byDueDate: [{ date: '2026-11-10', amount: '200.00' }, { date: '2026-11-28', amount: '300.00' }], caveats: [],
    },
    coverage: {
      state: 'Covered', reliability: 'Complete', period: 'Current', minimumBalance: '400.00', firstNegativeDate: null, firstBeyondLimitDate: null, deficit: null, obligationsConsidered: '600.00',
      expectedInflows: [{ date: '2026-11-25', amount: '400.00', kind: 'Income', certainty: 'Registered' }, { date: '2026-11-27', amount: '3000.00', kind: 'Income', certainty: 'Inferred' }],
      caveats: [], unavailableReason: null, accounts: [accountCoverage()], projectedClosing: '900.00',
    },
    safeMargin: {
      state: 'Calculated', amount: '400.00', scope: 'CashSpend', referenceDate: '2026-11-05', bindingConstraint: 'DailyCoverage',
      constraints: [
        { kind: 'DailyCoverage', applied: true, slack: '400.00', binding: true, reason: 'OwnFundsOnly' }, { kind: 'RecoveryTarget', applied: true, slack: '1500.00', binding: false, reason: 'Fixed' },
        { kind: 'MinimumReserve', applied: false, slack: null, binding: false, reason: 'NotConfigured' }, { kind: 'BudgetMargin', applied: false, slack: null, binding: false, reason: 'SpendNotCategorised' },
      ],
      reasons: [], notes: ['CardSpendNotCovered', 'InferredIncomeExcluded'],
    },
    recovery: {
      state: 'Fixed', reason: null, openingUsed: '15000.00', target: '2500.00', targetFixedAt: '2026-11-10T15:00:00Z', legacyTarget: false, effectiveRecovery: '500.00',
      projectedRecovery: '1000.00', gapToTarget: '-1500.00', expectedClosingUsed: '14000.00', band: 'Critical',
    },
    snapshotConsistent: true, notes: [], ...over,
  }
}
