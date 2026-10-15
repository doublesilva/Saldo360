import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { FinancialCategory } from './categories'

export interface FinancialBudgetTarget {
  id: string
  name: string
  plannedAmount: string
  groupingType: 'Category'
  category: Pick<FinancialCategory, 'id' | 'name' | 'isActive'>
  isArchived: boolean
  createdAt: string
  updatedAt: string
  archivedAt: string | null
}

export interface FinancialBudget {
  id: string
  competence: string
  description: string | null
  isArchived: boolean
  totalPlannedAmount: string
  targets: FinancialBudgetTarget[]
  createdAt: string
  updatedAt: string
  archivedAt: string | null
}

export interface FinancialBudgetExecutionAmounts {
  planned: string
  realized: string
  committed: string
  projectedConsumption: string
  available: string
  exceededAmount: string
  consumptionPercentage: string | null
}

export type FinancialBudgetAlertStatus = 'WithinBudget' | 'ApproachingLimit' | 'LimitReached' | 'Exceeded'

export interface FinancialBudgetTargetExecution {
  targetId: string
  categoryId: string
  planned: string
  realized: string
  committed: string
  projectedConsumption: string
  available: string
  exceededAmount: string
  consumptionPercentage: string | null
  status: FinancialBudgetAlertStatus
  alertMessage: string
}

export interface FinancialBudgetExecutionSummary extends FinancialBudgetExecutionAmounts {
  status: FinancialBudgetAlertStatus
  alertMessage: string
  approachingCount: number
  reachedCount: number
  exceededCount: number
}

export interface FinancialBudgetExecution {
  budgetId: string
  competence: string
  total: FinancialBudgetExecutionSummary
  targets: FinancialBudgetTargetExecution[]
}

export type BudgetSaveResult =
  | { ok: true; budget: FinancialBudget }
  | { ok: false; errors: Record<string, string[]>; conflict?: boolean }

const jsonHeaders = { Accept: 'application/json' }

function budgetsUrl(familyId: string, budgetId?: string): string {
  const base = `/api/families/${encodeURIComponent(familyId)}/budgets`
  return budgetId ? `${base}/${encodeURIComponent(budgetId)}` : base
}

async function readProblem(response: Response): Promise<Record<string, string[]>> {
  const body = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]>; title?: string }
  return body.errors ?? { general: [body.title ?? `Falha na API (HTTP ${response.status}).`] }
}

export async function listBudgets(familyId: string, competence: string, signal?: AbortSignal): Promise<FinancialBudget[]> {
  const response = await fetch(`${budgetsUrl(familyId)}?competence=${encodeURIComponent(competence)}`, {
    signal,
    headers: jsonHeaders,
  })
  if (!response.ok) throw new Error(`Falha ao carregar o orçamento (HTTP ${response.status}).`)
  return (await response.json()) as FinancialBudget[]
}

export async function getBudget(familyId: string, budgetId: string, signal?: AbortSignal): Promise<FinancialBudget> {
  const response = await fetch(budgetsUrl(familyId, budgetId), { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar o orçamento (HTTP ${response.status}).`)
  return (await response.json()) as FinancialBudget
}

export async function getBudgetExecution(
  familyId: string,
  budgetId: string,
  signal?: AbortSignal,
): Promise<FinancialBudgetExecution> {
  const response = await fetch(`${budgetsUrl(familyId, budgetId)}/execution`, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar a execução do orçamento (HTTP ${response.status}).`)
  return (await response.json()) as FinancialBudgetExecution
}

async function writeBudget(method: 'POST' | 'PUT', url: string, body: unknown, expected: number): Promise<BudgetSaveResult> {
  const response = await fetch(url, {
    method,
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: JSON.stringify(body),
  })
  if (response.status === expected) return { ok: true, budget: (await response.json()) as FinancialBudget }
  if (response.status === 400) return { ok: false, errors: await readProblem(response) }
  if (response.status === 409) return { ok: false, errors: await readProblem(response), conflict: true }
  if (response.status === 404) throw new Error('Orçamento ou família não encontrada.')
  throw new Error(`Falha ao salvar o orçamento (HTTP ${response.status}).`)
}

export function createBudget(familyId: string, competence: string, description?: string): Promise<BudgetSaveResult> {
  return writeBudget('POST', budgetsUrl(familyId), { competence, description: description || null }, 201)
}

export function updateBudget(familyId: string, budgetId: string, description: string): Promise<BudgetSaveResult> {
  return writeBudget('PUT', budgetsUrl(familyId, budgetId), { description }, 200)
}

export type TargetSaveResult = BudgetSaveResult

export function addBudgetTarget(
  familyId: string,
  budgetId: string,
  categoryId: string,
  plannedAmount: string,
  name: string,
): Promise<TargetSaveResult> {
  return writeBudget('POST', `${budgetsUrl(familyId, budgetId)}/targets`, {
    categoryId,
    plannedAmount,
    name,
    groupingType: 'Category',
  }, 201)
}

export function updateBudgetTarget(
  familyId: string,
  budgetId: string,
  targetId: string,
  name: string,
  plannedAmount: string,
): Promise<TargetSaveResult> {
  return writeBudget('PUT', `${budgetsUrl(familyId, budgetId)}/targets/${encodeURIComponent(targetId)}`, {
    name,
    plannedAmount,
  }, 200)
}

async function archive(url: string, what: string): Promise<{ ok: true } | { ok: false; message: string }> {
  const response = await fetch(url, {
    method: 'DELETE',
    headers: { ...jsonHeaders, [CSRF_HEADER]: await getCsrfToken() },
  })
  if (response.status === 204) return { ok: true }
  if (response.status === 409) return { ok: false, message: (await readProblem(response)).general?.[0] ?? `Não foi possível arquivar ${what}.` }
  if (response.status === 404) return { ok: false, message: `${what} não encontrado.` }
  return { ok: false, message: `Falha ao arquivar ${what} (HTTP ${response.status}).` }
}

export function archiveBudget(familyId: string, budgetId: string) {
  return archive(budgetsUrl(familyId, budgetId), 'o orçamento')
}

export function archiveBudgetTarget(familyId: string, budgetId: string, targetId: string) {
  return archive(`${budgetsUrl(familyId, budgetId)}/targets/${encodeURIComponent(targetId)}`, 'a meta')
}
