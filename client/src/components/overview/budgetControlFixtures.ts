import type { BudgetControl, BudgetControlGroup } from '../../api/overview'

/** Retratos sintéticos do consumo do orçamento (T11.2), no formato exato do servidor. */
export const group = (key: BudgetControlGroup['key'], over: Partial<BudgetControlGroup> = {}): BudgetControlGroup => ({
  key, state: 'Complete', target: key === 'Cards' ? '5000.00' : '7500.00', realized: key === 'Cards' ? '2000.00' : '3000.00', committed: key === 'Cards' ? '500.00' : '1000.00',
  estimated: key === 'Cards' ? '1000.00' : '0.00', projected: key === 'Cards' ? '3500.00' : '4000.00', margin: key === 'Cards' ? '1500.00' : '3500.00', excess: '0.00', excessRealized: '0.00',
  realizedPercent: key === 'Cards' ? '40.0' : '40.0', projectedPercent: key === 'Cards' ? '70.0' : '53.3', status: 'WithinBudget', reasons: [], ...over,
})

export function budgetControl(over: Partial<BudgetControl> = {}): BudgetControl {
  return {
    state: 'Complete', competence: '2026-11', period: 'Current', scopeKind: 'all', targetsAttributable: true,
    total: { targetDefined: true, target: '12500.00', realized: '5000.00', committed: '1500.00', estimated: '1000.00', projected: '7500.00', margin: '5000.00', excess: '0.00', realizedPercent: '40.0', projectedPercent: '60.0', status: 'WithinBudget' },
    cards: group('Cards'), outsideCards: group('OutsideCards'),
    pressures: [
      { rank: 1, kind: 'OverdueExpenses', subject: null, amount: '900.00', amountKind: 'Overdue', referenceAmount: null, referenceKind: 'None', impact: 'Cash', quality: 'Confirmed', action: 'OpenAgenda', actionContext: null },
      { rank: 2, kind: 'CardsOverTarget', subject: null, amount: '400.00', amountKind: 'OverTarget', referenceAmount: '5000.00', referenceKind: 'CardsTarget', impact: 'Budget', quality: 'Projected', action: 'OpenCards', actionContext: null },
      { rank: 3, kind: 'CategoryOverTarget', subject: 'Lazer', amount: '250.00', amountKind: 'OverTarget', referenceAmount: '500.00', referenceKind: 'CategoryTarget', impact: 'Budget', quality: 'Confirmed', action: 'OpenBudget', actionContext: 'categories' },
      { rank: 4, kind: 'OverdraftChargesNotEstimable', subject: null, amount: null, amountKind: 'NotEstimable', referenceAmount: null, referenceKind: 'None', impact: 'NotEstimable', quality: 'NotEstimable', action: 'OpenAccounts', actionContext: null },
    ],
    recommendations: [
      { code: 'CardsOverTarget', action: 'OpenCards', actionContext: null }, { code: 'UncategorizedExpenses', action: 'OpenReview', actionContext: null },
      { code: 'IncomeEstimateUnconfirmed', action: 'OpenPlanning', actionContext: null },
    ],
    notes: [], ...over,
  }
}
