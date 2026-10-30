import type { MonthlyPlan, PlanMonth, SixMonthPlan, WorkingDays } from '../api/monthlyPlan'
import { planActual, planActualIndicator } from './planActualFixtures'

/** Retrato sintético do planejamento mensal (T10.2): conta principal PF, uma receita, uma despesa revisada e um limite por categoria. */
export const monthlyPlan: MonthlyPlan = {
  competence: '2026-10', asOf: '2026-10-16', periodKind: 'Current', scope: planActual.scope, hasPlan: true,
  planned: {
    income: '7500.50', incomeLines: 1, expense: '2000.00', expenseLines: 1, reserve: '400.00', reserveLines: 1, result: '5500.50', limitCount: 1, limitsNotAttributable: false,
    transferCount: 0, transfersOut: '0.00', transfersIn: '0.00', incomeSource: 'MonthlyPlan', expenseSource: 'MonthlyPlan',
  },
  lines: [
    {
      id: 'l1', competence: '2026-10', kind: 'Income', nature: 'Recurring', description: 'Salário', accountId: 'pf', accountName: 'Conta A', accountScope: 'PF', accountActive: true,
      destinationAccountId: null, destinationAccountName: null, categoryId: null, categoryName: null, originalAmount: '7500.50', amount: '7500.50', difference: '0.00', revised: false,
      expectedDate: '2026-10-05', hourly: null, updatedAt: '2026-10-01T12:00:00Z',
    },
    {
      id: 'l2', competence: '2026-10', kind: 'Expense', nature: 'Fixed', description: 'Aluguel', accountId: 'pf', accountName: 'Conta A', accountScope: 'PF', accountActive: true,
      destinationAccountId: null, destinationAccountName: null, categoryId: 'cat-h', categoryName: 'Moradia', originalAmount: '1800.00', amount: '2000.00', difference: '200.00', revised: true,
      expectedDate: null, hourly: null, updatedAt: '2026-10-02T12:00:00Z',
    },
  ],
  categoryLimits: [{
    targetId: 't1', categoryId: 'cat-f', categoryName: 'Alimentação', amount: '2500.00', originalAmount: '2000.00', revised: true, itemizedAmount: '0.00', itemizedAboveLimit: false,
    execution: { realized: '600.00', committed: '100.00', cardCommitted: '0.00', projected: '700.00', available: '1800.00', exceeded: '0.00', percent: '28.00', status: 'WithinBudget' },
  }],
  execution: {
    income: planActualIndicator({ planned: '7500.50', plannedSource: 'MonthlyPlan', realized: '0.00', projected: '0.00', pending: '0.00', realizedDifference: '-7500.50', projectedDifference: '-7500.50', executionPercent: '0.0', projectedPercent: '0.0' }),
    consumption: planActualIndicator({ planned: '4500.00', plannedSource: 'MonthlyPlan', realized: '1200.00', projected: '1500.00', pending: '300.00', realizedDifference: '-3300.00', projectedDifference: '-3000.00', executionPercent: '26.7', projectedPercent: '33.3' }),
    cards: planActual.cards, coverage: planActual.coverage, snapshotConsistent: true,
  },
  businessDays: 22, pendencies: [],
}

/** Mês sem nenhuma linha nem limite: o planejado é "não definido", nunca zero. */
export const emptyMonthlyPlan: MonthlyPlan = {
  ...monthlyPlan, hasPlan: false, lines: [], categoryLimits: [],
  planned: { ...monthlyPlan.planned, income: null, incomeLines: 0, expense: null, expenseLines: 0, reserve: '0.00', reserveLines: 0, result: null, limitCount: 0, incomeSource: null, expenseSource: null },
  execution: { ...monthlyPlan.execution, income: planActualIndicator({ plannedState: 'NotDefined', planned: null, plannedSource: null, realizedDifference: null, projectedDifference: null, executionPercent: null, projectedPercent: null }), consumption: planActualIndicator({ plannedState: 'NotDefined', planned: null, plannedSource: null, realizedDifference: null, projectedDifference: null, executionPercent: null, projectedPercent: null }) },
  pendencies: [{ code: 'NoPlan', count: 1 }],
}

const month = (competence: string, overrides: Partial<PlanMonth> = {}): PlanMonth => ({
  competence, periodKind: 'Future', plannedIncome: null, plannedIncomeSource: null, projectedIncome: '0.00', realizedIncome: '0.00', plannedExpense: null, plannedExpenseSource: null,
  projectedExpense: '0.00', realizedExpense: '0.00', plannedReserve: null, plannedResult: null, projectedResult: '0.00', realizedResult: '0.00', knownObligations: '0.00',
  closingBalance: '1000.00', minimumProjectedBalance: '1000.00', firstNegativeProjectedDate: null, balanceStatus: 'WithinKnownCommitments', risk: 'Low', riskReasons: [],
  coverage: { dataComplete: true, obligationsCoverage: 'Covered', reasons: [] }, ...overrides,
})

export const sixMonths: SixMonthPlan = {
  from: '2026-10', through: '2027-03', asOf: '2026-10-16', scope: planActual.scope,
  months: [
    month('2026-10', { periodKind: 'Current', plannedIncome: '7500.50', plannedIncomeSource: 'MonthlyPlan', projectedIncome: '2000.00', realizedIncome: '2000.00', plannedExpense: '2000.00', plannedExpenseSource: 'MonthlyPlan', projectedExpense: '1500.00', realizedExpense: '1200.00', plannedResult: '5500.50', projectedResult: '500.00', realizedResult: '800.00', knownObligations: '300.00', closingBalance: '2400.00' }),
    month('2026-11'),
    month('2026-12', { plannedExpense: '800.00', plannedExpenseSource: 'MonthlyPlan', projectedExpense: '5000.00', projectedResult: '-5000.00', knownObligations: '5000.00', closingBalance: '-2100.00', minimumProjectedBalance: '-2100.00', firstNegativeProjectedDate: '2026-12-10', balanceStatus: 'NegativeProjectedBalance', risk: 'High', riskReasons: ['NegativeProjectedBalance', 'ProjectedExpenseAbovePlan'], coverage: { dataComplete: true, obligationsCoverage: 'Shortfall', reasons: [] } }),
    month('2027-01', { risk: 'Unknown', riskReasons: ['IncompleteCoverage'], coverage: { dataComplete: false, obligationsCoverage: 'Covered', reasons: ['CardCoverageIncomplete'] } }),
    month('2027-02'),
    month('2027-03', { closingBalance: null, minimumProjectedBalance: null, balanceStatus: 'Unavailable', risk: 'Unknown', riskReasons: ['BalanceUnavailable'], coverage: { dataComplete: true, obligationsCoverage: 'Unknown', reasons: [] } }),
  ],
  summary: { highestRiskMonths: ['2026-12'], monthsWithoutPlan: 4, monthsWithRisk: 1, plannedResult: null, projectedResult: '-4500.00' },
}

export const workingDays: WorkingDays = {
  competence: '2026-10', calendarDays: 31, weekdays: 22, nonWorkingWeekdays: 1, businessDays: 21,
  nonWorkingDays: [{ id: 'd1', date: '2026-10-12', kind: 'Holiday', description: 'Nossa Senhora Aparecida', weekend: false }],
}
