import { requestJson } from './apiErrors'
import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { IntegratedIncome, IntegratedPj } from './monthlyPlan'

/**
 * Entrega 3: projeção financeira consolidada (`monthly-plan/projection`, Entrega 2). Os tipos espelham `ProjectionModels.cs`. Todo valor chega do backend
 * como texto decimal exato; `null` significa indisponível (nunca zero). Nenhum cálculo financeiro roda no navegador.
 */
export type ProjectionScenarioName = 'Current' | 'Ceilings'
export const PROJECTION_HORIZONS = [6, 13, 24, 36] as const
export type ProjectionHorizon = (typeof PROJECTION_HORIZONS)[number]

export interface ProjectionPf {
  opening: string | null; entries: string; outsideCardsExpenses: string; outsideCardsComplement: string; cardConsumption: string; cardConsumptionComplement: string
  cardPaymentsInCash: string; transfersIn: string; result: string | null; closing: string | null
}
export interface ProjectionBudget {
  cardTarget: string | null; cardProjected: string; cardMargin: string | null; cardExcess: string | null
  outsideTarget: string | null; outsideProjected: string; outsideMargin: string | null; outsideExcess: string | null
}
export interface ProjectionOverdraft { limit: string | null; usedStart: string; usedEnd: string; available: string | null; monthlyRecovery: string; cumulativeRecovery: string; positiveBalance: string }
export interface ProjectionMonth {
  competence: string; periodKind: 'Past' | 'Current' | 'Future'; income: IntegratedIncome; pj: IntegratedPj
  transfers: { plannedPjToPf: string; realizedPjToPf: string; autoProjectedPjToPf?: string; dependsOnInferredIncome?: boolean }
  pf: ProjectionPf; budget: ProjectionBudget; overdraft: ProjectionOverdraft | null; complete: boolean; quality: string[]
}
export interface ProjectionScenario { name: ProjectionScenarioName; months: ProjectionMonth[] }
export interface FinancialProjection {
  from: string; monthCount: number; referenceDate: string; pjAccountId: string | null; pfAccountId: string | null; scenarios: ProjectionScenario[]; notes: string[]
}

export interface FoodProvision { monthlyAmount: string; amountConfigured: boolean; categoryIds: string[]; categoriesInferred: boolean; monthAmounts: Record<string, string> }

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/monthly-plan`
const read = { headers: { Accept: 'application/json' } }

export function getFinancialProjection(workspaceId: string, from: string, months: number, signal?: AbortSignal): Promise<FinancialProjection> {
  const query = new URLSearchParams({ from, months: String(months) })
  return requestJson<FinancialProjection>(`${base(workspaceId)}/projection?${query}`, { ...read, signal }, { fallback: 'Falha ao carregar a projeção financeira.' }).then(data => {
    if (!data || !Array.isArray(data.scenarios) || !Array.isArray(data.notes)) throw new Error('Resposta inesperada da projeção financeira.')
    return data
  })
}

async function write(method: 'PUT' | 'DELETE', url: string, body: unknown, fallback: string): Promise<FoodProvision> {
  return requestJson<FoodProvision>(url, {
    method, headers: { Accept: 'application/json', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), [CSRF_HEADER]: await getCsrfToken() },
    body: body === undefined ? undefined : JSON.stringify(body),
  }, { fallback })
}

export const getFoodProvision = (workspaceId: string, signal?: AbortSignal) =>
  requestJson<FoodProvision>(`${base(workspaceId)}/food-provision`, { ...read, signal }, { fallback: 'Falha ao carregar a provisão de alimentação.' }).then(data => {
    if (typeof data?.monthlyAmount !== 'string' || !Array.isArray(data.categoryIds)) throw new Error('Resposta inesperada da provisão de alimentação.')
    return data
  })
export const saveFoodProvision = (workspaceId: string, monthlyAmount: string, categoryIds: string[]) =>
  write('PUT', `${base(workspaceId)}/food-provision`, { monthlyAmount, categoryIds }, 'Falha ao salvar a provisão de alimentação.')
export const saveFoodProvisionMonth = (workspaceId: string, competence: string, monthlyAmount: string) =>
  write('PUT', `${base(workspaceId)}/food-provision/months/${encodeURIComponent(competence)}`, { monthlyAmount }, 'Falha ao ajustar a competência.')
export const removeFoodProvisionMonth = (workspaceId: string, competence: string) =>
  write('DELETE', `${base(workspaceId)}/food-provision/months/${encodeURIComponent(competence)}`, undefined, 'Falha ao restaurar o valor padrão da competência.')
