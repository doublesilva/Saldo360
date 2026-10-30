import type { DashboardOverview } from '../../api/overview'

/** Retrato sintético da Visão Geral (R01): conta principal PF, uma conta sem saldo, compromissos vencidos e próximos, metas e orçamento com um excesso. */
export const overview: DashboardOverview = {
  competence: '2026-10', asOf: '2026-10-16', scopeKind: 'primary', scopeAccountId: 'pf',
  accounts: {
    state: 'Complete', asOf: '2026-10-16',
    items: [
      { accountId: 'pf', name: 'Conta A', scope: 'PF', type: 'Checking', isPrimary: true, inScope: true, balanceAvailable: true, balance: '3000.00', unavailableReason: null },
      { accountId: 'pj', name: 'Conta B', scope: 'PJ', type: 'Checking', isPrimary: false, inScope: false, balanceAvailable: true, balance: '-150.25', unavailableReason: null },
      { accountId: 'res', name: 'Reserva', scope: 'PF', type: 'Savings', isPrimary: false, inScope: false, balanceAvailable: false, balance: null, unavailableReason: 'OpeningBalanceNotConfigured' },
    ],
  },
  commitments: {
    state: 'Complete', asOf: '2026-10-16', horizonThrough: '2026-11-15', horizonDays: 30,
    overdue: { count: 1, total: '120.40', items: [{ id: 'c1', source: 'PlannedTransaction', description: 'Internet', date: '2026-10-10', amount: '120.40', accountName: 'Conta A', daysFromToday: -6 }] },
    upcoming: {
      count: 7, total: '900.07',
      items: [
        { id: 'c2', source: 'PlannedTransaction', description: 'Aluguel', date: '2026-10-16', amount: '700.00', accountName: 'Conta A', daysFromToday: 0 },
        { id: 'c3', source: 'DebtInstallment', description: 'Financiamento - parcela 4', date: '2026-11-05', amount: '200.07', accountName: null, daysFromToday: 20 },
      ],
    },
  },
  goals: { state: 'Complete', activeCount: 1, completedCount: 0, items: [{ goalId: 'g1', name: 'Reserva de emergência', priority: 'High', target: '10000.00', allocated: '2500.00', progressPercent: '25.0', targetDate: '2027-06-30', funded: false }] },
  budget: {
    state: 'Complete', hasBudget: true, budgetId: 'b1', planned: '1600.00', projected: '1500.00', realized: '1200.00', exceededCount: 1, approachingCount: 0,
    targets: [
      { targetId: 't1', categoryId: 'cat-h', categoryName: 'Moradia', planned: '1000.00', realized: '900.00', committed: '300.00', projected: '1200.00', available: '0.00', exceeded: '200.00', percent: '120.0', status: 'Exceeded' },
      { targetId: 't2', categoryId: 'cat-f', categoryName: 'Alimentação', planned: '600.00', realized: '300.00', committed: '0.00', projected: '300.00', available: '300.00', exceeded: '0.00', percent: '50.0', status: 'WithinBudget' },
    ],
  },
  recent: {
    state: 'Complete', from: '2026-10-01', through: '2026-10-16', totalCount: 9,
    items: [
      { id: 'r1', type: 'Expense', amount: '45.90', date: '2026-10-15', description: 'Mercado', categoryName: 'Alimentação', accountName: 'Conta A', awaitingClassification: false },
      { id: 'r2', type: 'Income', amount: '2000.00', date: '2026-10-05', description: 'Salário', categoryName: null, accountName: 'Conta A', awaitingClassification: true },
    ],
  },
  classification: { state: 'Complete', needsReview: 2, notEvaluated: 1 },
}
