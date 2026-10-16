import type { FinancialCalendarSource } from './financialCalendar'

export type DashboardTrend = 'Improving' | 'Stable' | 'Deteriorating' | 'Unknown'
export type DashboardAlertSeverity = 'Info' | 'Warning' | 'Critical'

export interface DashboardAccountBalance { accountId: string; name: string; realizedBalance: string | null; complete: boolean }
export interface DashboardPosition { realizedBalance: string | null; accounts: DashboardAccountBalance[]; consideredAccounts: number; negativeBalance: string; allocatedToGoals: string; unallocatedRealizedBalance: string | null }
export interface DashboardCashFlow { realizedIncome: string; realizedExpense: string; pendingIncome: string; pendingExpense: string }
export interface DashboardObligations { pendingIncome: string; pendingExpense: string; creditCardOutstanding: string; debtOutstanding: string; pendingItems: number }
export interface DashboardBudgets { targetCount: number; planned: string; committed: string; realized: string; exceededCount: number; attentionCount: number }
export interface DashboardCreditCards { count: number; outstanding: string; overdueInvoices: number; futureInvoices: number; hasUnallocatedObligations: boolean; availableLimit: string | null }
export interface DashboardDebts { activeContracts: number; overdueInstallments: number; installmentsDueInHorizon: string; declaredOutstandingBalance: string | null; unallocatedObligations: number }
export interface DashboardGoals { active: number; completed: number; paused: number; allocated: string; unallocated: string; coverageComplete: boolean }
export interface DashboardMonthlyProjection { competence: string; closingBalance: string | null; realizedIncome: string; realizedExpense: string; pendingIncome: string; pendingExpense: string; creditCardOutstanding: string; projectionComplete: boolean }
export interface DashboardCoverage { complete: boolean; hasUnallocatedCreditCardObligations: boolean; hasUnallocatedDebtObligations: boolean; reason: string | null }
export interface DashboardNextCommitment { source: FinancialCalendarSource; id: string; date: string; amount: string; description: string; accountId: string | null }
export interface DashboardCapacityBeforeIncome { status: string; nextIncomeDate: string | null; nextIncomeAmount: string | null; projectedBalanceBeforeIncome: string | null; minimumProjectedBalance: string | null }
export interface DashboardIndicators { realizedBalance: string | null; projectedBalance: string | null; projectedVariation: string | null; projectedVariationReference: string | null; realizedNet: string; pendingNet: string; knownNet: string; commitment: string; unallocatedAvailability: string | null; trend: DashboardTrend; nextCommitment: DashboardNextCommitment | null; capacityBeforeNextIncome: DashboardCapacityBeforeIncome }
export interface DashboardAlert { code: string; severity: DashboardAlertSeverity; title: string; description: string; competence: string | null; amount: string | null; sourceId: string | null; context: string | null; referenceDate: string }
export interface FinancialDashboardResponse { competence: string; months: number; position: DashboardPosition; cashFlow: DashboardCashFlow; obligations: DashboardObligations; budgets: DashboardBudgets; creditCards: DashboardCreditCards; debts: DashboardDebts; goals: DashboardGoals; monthlyProjection: DashboardMonthlyProjection[]; coverage: DashboardCoverage; indicators: DashboardIndicators; alerts: DashboardAlert[]; asOf: string }

export async function getFinancialDashboard(familyId: string, competence?: string, months = 6, signal?: AbortSignal): Promise<FinancialDashboardResponse> {
  const query = new URLSearchParams({ months: String(months) })
  if (competence) query.set('competence', competence)
  const response = await fetch(`/api/families/${encodeURIComponent(familyId)}/financial-dashboard?${query}`, { signal, headers: { Accept: 'application/json' } })
  if (!response.ok) throw new Error(`Falha ao carregar o painel financeiro (HTTP ${response.status}).`)
  return await response.json() as FinancialDashboardResponse
}
