import type { FinancialDashboardResponse } from '../api/dashboard'

export const dashboard: FinancialDashboardResponse = {
  competence: '2026-10', months: 6,
  position: { realizedBalance: '5000.00', accounts: [], consideredAccounts: 1, negativeBalance: '0.00', allocatedToGoals: '0.00', unallocatedRealizedBalance: '5000.00' },
  cashFlow: { realizedIncome: '5000.00', realizedExpense: '2000.00', pendingIncome: '1500.00', pendingExpense: '800.00' },
  obligations: { pendingIncome: '1500.00', pendingExpense: '800.00', creditCardOutstanding: '300.00', debtOutstanding: '0.00', pendingItems: 2 },
  budgets: { targetCount: 2, planned: '3000.00', committed: '800.00', realized: '2000.00', exceededCount: 0, attentionCount: 1 },
  creditCards: { count: 1, outstanding: '300.00', overdueInvoices: 0, futureInvoices: 1, hasUnallocatedObligations: false, availableLimit: '9700.00' },
  debts: { activeContracts: 0, overdueInstallments: 0, installmentsDueInHorizon: '0.00', declaredOutstandingBalance: '0.00', unallocatedObligations: 0 },
  goals: { active: 1, completed: 0, paused: 0, allocated: '500.00', unallocated: '4500.00', coverageComplete: true },
  monthlyProjection: [{ competence: '2026-10', closingBalance: '5500.00', realizedIncome: '5000.00', realizedExpense: '2000.00', pendingIncome: '1500.00', pendingExpense: '800.00', creditCardOutstanding: '300.00', projectionComplete: true }],
  coverage: { complete: true, hasUnallocatedCreditCardObligations: false, hasUnallocatedDebtObligations: false, reason: null },
  indicators: { realizedBalance: '5000.00', projectedBalance: '5500.00', projectedVariation: '500.00', projectedVariationReference: 'realized', realizedNet: '3000.00', pendingNet: '700.00', knownNet: '3700.00', commitment: '1100.00', unallocatedAvailability: '4500.00', trend: 'Improving', nextCommitment: { source: 'PlannedTransaction', id: 'p1', date: '2026-10-20', amount: '800.00', description: 'Internet', accountId: 'a1' }, capacityBeforeNextIncome: { status: 'Unknown', nextIncomeDate: null, nextIncomeAmount: null, projectedBalanceBeforeIncome: null, minimumProjectedBalance: null } },
  alerts: [{ code: 'budget-attention', severity: 'Warning', title: 'Orçamento próximo do limite', description: 'A categoria está acima de 85%.', competence: '2026-10', amount: '800.00', sourceId: 'b1', context: null, referenceDate: '2026-10-01' }],
  asOf: '2026-10-16T12:00:00Z',
}

export function dashboardResponse(input: RequestInfo | URL, value = dashboard): Response {
  const url = String(input)
  if (url.includes('/financial-dashboard')) return Response.json(value)
  if (url.includes('/credit-cards?')) return Response.json([])
  if (url.includes('/financial-calendar/balances')) return Response.json({ from: `${value.competence}-01`, through: `${value.competence}-31`, accountId: null, openingBalance: null, unavailableReason: 'OpeningBalanceNotConfigured', days: [], risk: { minimumProjectedBalance: null, firstNegativeProjectedDate: null, endingProjectedBalance: null } })
  if (url.includes('/financial-calendar?')) return Response.json({ items: value.indicators.nextCommitment ? [{ ...value.indicators.nextCommitment, source: 'PlannedTransaction', type: 'Expense', status: 'Overdue', currency: 'BRL', account: null, category: null, plannedTransactionId: 'p1' }] : [], summary: value.cashFlow })
  return new Response('{}', { status: 404 })
}
