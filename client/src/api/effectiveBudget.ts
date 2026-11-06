import { requestJson } from './apiErrors'
import { CSRF_HEADER, getCsrfToken } from './csrf'

/**
 * T12.1: orçamento mensal recorrente. Os tipos espelham `EffectiveBudgetService`. Valores são texto decimal exato; `null` = sem valor (nunca zero). A herança é resolvida no servidor: o
 * navegador só apresenta a origem de cada valor e envia a escolha do usuário ("somente este mês" ou "este mês e os próximos").
 */
export type BudgetRuleTarget = 'TotalExpenses' | 'AllCards' | 'FixedExpenses' | 'Category'
export type TargetOrigin = 'Inherited' | 'Customized' | 'Historical' | 'Undefined'
export type BudgetChangeMode = 'ThisMonth' | 'ThisAndNext'
export type BudgetChangeAction = 'Set' | 'Remove' | 'Restore'

export interface EffectiveTarget {
  id: string; target: BudgetRuleTarget; categoryId: string | null; name: string; amount: string | null; origin: TargetOrigin; removed: boolean; standardFrom: string | null
}
export interface EffectiveBudget {
  competence: string; budgetId: string | null; standardConfigured: boolean; outsideCardsTarget: string | null; targets: EffectiveTarget[]; warnings: string[]
}
export interface BudgetRuleHistoryItem {
  scope: 'FromCompetence' | 'OnlyCompetence'; target: BudgetRuleTarget; categoryId: string | null; categoryName: string | null; competence: string; revision: number
  action: BudgetChangeAction; amount: string | null; note: string | null; at: string
}
export interface ReferenceItem { target: BudgetRuleTarget; categoryId: string | null; name: string; amount: string }
export interface ReferencePreview { source: string; from: string; items: ReferenceItem[]; warnings: string[] }
export interface EffectiveExecutionTarget { targetId: string; groupingType: string; planned: string; consumed: string; available: string; exceededAmount: string }
export interface EffectiveExecution { targets: EffectiveExecutionTarget[] }

export interface BudgetChange {
  competence: string; target: BudgetRuleTarget; categoryId?: string | null; mode: BudgetChangeMode; action: BudgetChangeAction; amount?: string
}

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/budgets/effective`
const read = { headers: { Accept: 'application/json' } }

export const getEffectiveBudget = (workspaceId: string, competence: string, signal?: AbortSignal) =>
  requestJson<EffectiveBudget>(`${base(workspaceId)}?competence=${encodeURIComponent(competence)}`, { ...read, signal }, { fallback: 'Falha ao carregar o orçamento padrão.' }).then(data => {
    if (!data || !Array.isArray(data.targets) || !Array.isArray(data.warnings)) throw new Error('Resposta inesperada do orçamento padrão.')
    return data
  })

export const getEffectiveExecution = (workspaceId: string, competence: string, signal?: AbortSignal) =>
  requestJson<EffectiveExecution>(`${base(workspaceId)}/execution?competence=${encodeURIComponent(competence)}`, { ...read, signal }, { fallback: 'Falha ao carregar a execução do orçamento.' })

export const getBudgetRuleHistory = (workspaceId: string, signal?: AbortSignal) =>
  requestJson<BudgetRuleHistoryItem[]>(`${base(workspaceId)}/history`, { ...read, signal }, { fallback: 'Falha ao carregar o histórico do orçamento.' })

export const getReferencePreview = (workspaceId: string, source: string, from: string, signal?: AbortSignal) =>
  requestJson<ReferencePreview>(`${base(workspaceId)}/reference?source=${encodeURIComponent(source)}&from=${encodeURIComponent(from)}`, { ...read, signal }, { fallback: 'Falha ao carregar a referência.' })

async function post<T>(url: string, body: unknown, fallback: string): Promise<T> {
  return requestJson<T>(url, { method: 'POST', headers: { Accept: 'application/json', 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify(body) }, { fallback })
}

export const changeBudgetRule = (workspaceId: string, change: BudgetChange) =>
  post<EffectiveBudget>(`${base(workspaceId)}/changes`, change, 'Falha ao salvar a meta do orçamento.')

export const applyBudgetReference = (workspaceId: string, source: string, from: string, targets: { target: BudgetRuleTarget; categoryId: string | null }[]) =>
  post<EffectiveBudget>(`${base(workspaceId)}/reference`, { source, from, targets }, 'Falha ao aplicar a referência como padrão.')
