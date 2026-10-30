import { CSRF_HEADER, getCsrfToken } from './csrf'
import { requestJson } from './apiErrors'
import type { PlanActualScope, PlanCards, PlanCoverage, PlanIndicator } from './planActual'

/**
 * T10.2: planejamento mensal. Todo valor chega do backend como texto decimal exato e nenhuma fórmula financeira (totais, dias úteis, valor por hora,
 * risco, cobertura) roda no navegador: aqui só há tipos e chamadas. O plano é META do orçamento, nunca uma movimentação.
 */
export type PlanLineKind = 'Income' | 'Expense' | 'Transfer'
export type PlanLineNature = 'Recurring' | 'Fixed' | 'Variable' | 'Extraordinary' | 'Reserve'
export type PlanScopeKind = 'primary' | 'all' | 'account'
export interface PlanScopeChoice { kind: PlanScopeKind; accountId?: string }

export interface PlanHourly { hourlyRate: string; hoursPerDay: string; billableDays: number; businessDays: number; exceedsCalendar: boolean }
export interface PlanLine {
  id: string; competence: string; kind: PlanLineKind; nature: PlanLineNature | null; description: string
  accountId: string | null; accountName: string | null; accountScope: string | null; accountActive: boolean
  destinationAccountId: string | null; destinationAccountName: string | null; categoryId: string | null; categoryName: string | null
  originalAmount: string; amount: string; difference: string; revised: boolean; expectedDate: string | null; hourly: PlanHourly | null; updatedAt: string
}
export interface LimitExecution { realized: string; committed: string; cardCommitted: string; projected: string; available: string; exceeded: string; percent: string | null; status: string }
export interface CategoryLimit {
  targetId: string; categoryId: string; categoryName: string; amount: string; originalAmount: string | null; revised: boolean
  itemizedAmount: string; itemizedAboveLimit: boolean; execution: LimitExecution | null
}
export interface PlanTotals {
  income: string | null; incomeLines: number; expense: string | null; expenseLines: number; reserve: string; reserveLines: number; result: string | null
  limitCount: number; limitsNotAttributable: boolean; transferCount: number; transfersOut: string; transfersIn: string; incomeSource: string | null; expenseSource: string | null
}
export interface PlanPendency { code: string; count: number }
export interface MonthlyExecution { income: PlanIndicator; consumption: PlanIndicator; cards: PlanCards; coverage: PlanCoverage; snapshotConsistent: boolean }
export interface MonthlyPlan {
  competence: string; asOf: string; periodKind: 'Past' | 'Current' | 'Future'; scope: PlanActualScope; hasPlan: boolean; planned: PlanTotals
  lines: PlanLine[]; categoryLimits: CategoryLimit[]; execution: MonthlyExecution; businessDays: number; pendencies: PlanPendency[]
}
export interface PlanHistoryEntry {
  id: string; competence: string; subject: 'Line' | 'CategoryLimit'; subjectId: string; action: 'Created' | 'Revised' | 'Removed' | 'Restored' | 'Copied'
  label: string; previousAmount: string | null; newAmount: string | null; note: string | null; userId: string; at: string
}
export interface NonWorkingDay { id: string; date: string; kind: 'Holiday' | 'NonWorking'; description: string; weekend: boolean }
export interface WorkingDays { competence: string; calendarDays: number; weekdays: number; nonWorkingWeekdays: number; businessDays: number; nonWorkingDays: NonWorkingDay[] }
export interface HourlyIncomePreview { competence: string; hourlyRate: string; hoursPerDay: string; billableDays: number; businessDays: number; amount: string }
export interface CopyResult { fromCompetence: string; toCompetence: string; linesCopied: number; linesSkipped: number; limitsCopied: number; limitsSkipped: number; recalculatedHourlyLines: string[] }

export type RiskLevel = 'High' | 'Medium' | 'Unknown' | 'Low'
export interface PlanMonth {
  competence: string; periodKind: 'Past' | 'Current' | 'Future'; plannedIncome: string | null; plannedIncomeSource: string | null; projectedIncome: string; realizedIncome: string
  plannedExpense: string | null; plannedExpenseSource: string | null; projectedExpense: string; realizedExpense: string; plannedReserve: string | null
  plannedResult: string | null; projectedResult: string; realizedResult: string; knownObligations: string; closingBalance: string | null; minimumProjectedBalance: string | null
  firstNegativeProjectedDate: string | null; balanceStatus: string; risk: RiskLevel; riskReasons: string[]
  coverage: { dataComplete: boolean; obligationsCoverage: 'Covered' | 'Shortfall' | 'Unknown'; reasons: string[] }
}
export interface SixMonthPlan {
  from: string; through: string; asOf: string; scope: PlanActualScope; months: PlanMonth[]
  summary: { highestRiskMonths: string[]; monthsWithoutPlan: number; monthsWithRisk: number; plannedResult: string | null; projectedResult: string }
}

/** Corpo de uma linha. Na receita por hora, informe valor/hora, horas por dia e dias faturáveis (o valor é calculado pelo backend) e omita `amount`. */
export interface PlanLineInput {
  competence?: string; kind: PlanLineKind; nature: PlanLineNature | null; description: string; accountId: string | null; destinationAccountId?: string | null
  categoryId: string | null; amount?: string | null; expectedDate: string | null; hourlyRate?: string | null; hoursPerDay?: string | null; billableDays?: number | null; note?: string | null
}

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/monthly-plan`
const read = { headers: { Accept: 'application/json' } }
async function write<T>(method: 'POST' | 'PUT' | 'DELETE', url: string, body: unknown, fallback: string): Promise<T> {
  return requestJson<T>(url, {
    method, headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), [CSRF_HEADER]: await getCsrfToken() },
    body: body === undefined ? undefined : JSON.stringify(body),
  }, { fallback })
}

export function getMonthlyPlan(workspaceId: string, competence: string, scope: PlanScopeChoice, signal?: AbortSignal): Promise<MonthlyPlan> {
  const query = new URLSearchParams({ competence, scope: scope.kind })
  if (scope.kind === 'account' && scope.accountId) query.set('accountId', scope.accountId)
  return requestJson<MonthlyPlan>(`${base(workspaceId)}?${query}`, { ...read, signal }, { fallback: 'Falha ao carregar o planejamento mensal.' }).then(data => {
    if (!data?.scope || !data.planned || !Array.isArray(data.lines) || !data.execution) throw new Error('Resposta inesperada do planejamento mensal.')
    return data
  })
}

export function getSixMonthPlan(workspaceId: string, from: string, scope: PlanScopeChoice, signal?: AbortSignal): Promise<SixMonthPlan> {
  const query = new URLSearchParams({ from, scope: scope.kind })
  if (scope.kind === 'account' && scope.accountId) query.set('accountId', scope.accountId)
  return requestJson<SixMonthPlan>(`${base(workspaceId)}/six-months?${query}`, { ...read, signal }, { fallback: 'Falha ao carregar o planejamento de seis meses.' }).then(data => {
    if (!data?.scope || !Array.isArray(data.months) || !data.summary) throw new Error('Resposta inesperada do planejamento de seis meses.')
    return data
  })
}

export const createPlanLine = (workspaceId: string, input: PlanLineInput) => write<PlanLine>('POST', `${base(workspaceId)}/lines`, input, 'Falha ao salvar a linha do planejamento.')
export const revisePlanLine = (workspaceId: string, id: string, input: PlanLineInput) => write<PlanLine>('PUT', `${base(workspaceId)}/lines/${encodeURIComponent(id)}`, input, 'Falha ao revisar a linha do planejamento.')
export const removePlanLine = (workspaceId: string, id: string, note?: string) =>
  write<PlanLine>('DELETE', `${base(workspaceId)}/lines/${encodeURIComponent(id)}${note ? `?note=${encodeURIComponent(note)}` : ''}`, undefined, 'Falha ao remover a linha do planejamento.')
export const setCategoryLimit = (workspaceId: string, competence: string, categoryId: string, amount: string, note?: string) =>
  write<CategoryLimit>('PUT', `${base(workspaceId)}/category-limits`, { competence, categoryId, amount, note: note || null }, 'Falha ao salvar o limite da categoria.')
export const removeCategoryLimit = (workspaceId: string, competence: string, categoryId: string, note?: string) =>
  write<CategoryLimit>('DELETE', `${base(workspaceId)}/category-limits/${encodeURIComponent(categoryId)}?competence=${encodeURIComponent(competence)}${note ? `&note=${encodeURIComponent(note)}` : ''}`, undefined, 'Falha ao remover o limite da categoria.')
export const copyMonthlyPlan = (workspaceId: string, fromCompetence: string, toCompetence: string) =>
  write<CopyResult>('POST', `${base(workspaceId)}/copy`, { fromCompetence, toCompetence, confirm: true }, 'Falha ao copiar o planejamento.')

export function getPlanHistory(workspaceId: string, competence: string, subjectId?: string, signal?: AbortSignal): Promise<PlanHistoryEntry[]> {
  const query = new URLSearchParams({ competence })
  if (subjectId) query.set('subjectId', subjectId)
  return requestJson<PlanHistoryEntry[]>(`${base(workspaceId)}/history?${query}`, { ...read, signal }, { fallback: 'Falha ao carregar o histórico do planejamento.' })
}

export const getWorkingDays = (workspaceId: string, competence: string, signal?: AbortSignal) =>
  requestJson<WorkingDays>(`${base(workspaceId)}/working-days?competence=${encodeURIComponent(competence)}`, { ...read, signal }, { fallback: 'Falha ao carregar o calendário de dias úteis.' })

/** Valor esperado de uma receita por hora, calculado pelo backend (nada é gravado). */
export function previewHourlyIncome(workspaceId: string, competence: string, hourlyRate: string, hoursPerDay: string, billableDays: number, signal?: AbortSignal) {
  const query = new URLSearchParams({ competence, hourlyRate, hoursPerDay, billableDays: String(billableDays) })
  return requestJson<HourlyIncomePreview>(`${base(workspaceId)}/hourly-income?${query}`, { ...read, signal }, { fallback: 'Falha ao calcular a receita por hora.' })
}

export const addNonWorkingDay = (workspaceId: string, date: string, kind: NonWorkingDay['kind'], description: string) =>
  write<NonWorkingDay>('POST', `${base(workspaceId)}/non-working-days`, { date, kind, description }, 'Falha ao cadastrar o dia.')
export const removeNonWorkingDay = (workspaceId: string, id: string, competence: string) =>
  write<WorkingDays>('DELETE', `${base(workspaceId)}/non-working-days/${encodeURIComponent(id)}?competence=${encodeURIComponent(competence)}`, undefined, 'Falha ao remover o dia.')
