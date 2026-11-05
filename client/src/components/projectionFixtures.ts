import type { FinancialProjection, FoodProvision, ProjectionMonth, ProjectionScenarioName } from '../api/projection'

/** Retratos sintéticos da projeção financeira (Entrega 3): saldos negativos e positivos, tetos excedidos e dados incompletos. */
export const projectionMonth = (competence: string, scenario: ProjectionScenarioName, over: Partial<ProjectionMonth> = {}): ProjectionMonth => ({
  competence, periodKind: 'Future',
  income: { source: 'Forecast', workedCompetence: '2026-09', businessDays: 21, billableDays: 21, hoursPerDay: '8', hourlyRate: '100.00', expectedGross: '16800.00', paymentDate: `${competence}-10`, realizedIncome: '0.00', adjusted: false },
  pj: { opening: '1000.00', pendingIncome: '16800.00', pendingExpense: '500.00', taxReserve: '1000.00', taxObligationRegistered: false, operationalReserve: '200.00', closing: '17300.00', available: '16100.00', suggestedTransfer: null },
  transfers: { plannedPjToPf: '0.00', realizedPjToPf: '0.00', autoProjectedPjToPf: '6000.00', dependsOnInferredIncome: false },
  pf: {
    opening: '-1000.00', entries: '500.00', outsideCardsExpenses: '2000.00', outsideCardsComplement: scenario === 'Ceilings' ? '1500.00' : '0.00', cardConsumption: '3000.00',
    cardConsumptionComplement: scenario === 'Ceilings' ? '2000.00' : '500.00', cardPaymentsInCash: '2500.00', transfersIn: '6000.00', result: scenario === 'Ceilings' ? '-500.00' : '1500.00',
    closing: scenario === 'Ceilings' ? '-1500.00' : '500.00',
  },
  budget: { cardTarget: '5000.00', cardProjected: scenario === 'Ceilings' ? '5000.00' : '3500.00', cardMargin: scenario === 'Ceilings' ? '0.00' : '1500.00', cardExcess: '0.00', outsideTarget: '7500.00', outsideProjected: scenario === 'Ceilings' ? '7500.00' : '2000.00', outsideMargin: scenario === 'Ceilings' ? '0.00' : '5500.00', outsideExcess: '0.00' },
  overdraft: { limit: '3000.00', usedStart: '1000.00', usedEnd: scenario === 'Ceilings' ? '1500.00' : '0.00', available: scenario === 'Ceilings' ? '1500.00' : '3000.00', monthlyRecovery: scenario === 'Ceilings' ? '-500.00' : '1000.00', cumulativeRecovery: scenario === 'Ceilings' ? '-500.00' : '1000.00', positiveBalance: scenario === 'Ceilings' ? '0.00' : '500.00' },
  complete: true, quality: [], ...over,
})

const competences = (from: string, count: number) => Array.from({ length: count }, (_, index) => {
  const [year, month] = from.split('-').map(Number)
  const date = new Date(Date.UTC(year, month - 1 + index, 1))
  return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}`
})

export function projection(from = '2026-10', count = 6, tweak?: (competence: string, scenario: ProjectionScenarioName, index: number) => Partial<ProjectionMonth>): FinancialProjection {
  const build = (scenario: ProjectionScenarioName) => competences(from, count).map((competence, index) => projectionMonth(competence, scenario, tweak?.(competence, scenario, index)))
  return { from, monthCount: count, referenceDate: '2026-10-16', pjAccountId: 'pj', pfAccountId: 'pf', scenarios: [{ name: 'Current', months: build('Current') }, { name: 'Ceilings', months: build('Ceilings') }], notes: [] }
}

export const foodProvision: FoodProvision = { monthlyAmount: '2000.00', amountConfigured: false, categoryIds: ['cat-f'], categoriesInferred: true, monthAmounts: {} }
