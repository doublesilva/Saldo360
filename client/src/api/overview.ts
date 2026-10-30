import { requestJson } from './apiErrors'
import type { PlanActualScopeChoice } from './planActual'

/**
 * R01: blocos da Visão Geral que o servidor compõe (contas com saldo, compromissos da Agenda, metas, orçamento do mês, últimos lançamentos e fila de
 * classificação). Todo valor monetário chega como texto decimal exato e nada é somado ou arredondado no navegador. Cada bloco tem o seu estado: "Unavailable"
 * significa que aquele bloco falhou (nunca zero) e os demais continuam válidos.
 */
export type BlockState = 'Complete' | 'Partial' | 'Unavailable'

export interface OverviewAccount {
  accountId: string; name: string; scope: 'PF' | 'PJ'; type: string; isPrimary: boolean; inScope: boolean
  balanceAvailable: boolean; balance: string | null; unavailableReason: string | null
}
export interface OverviewAccounts { state: BlockState; asOf: string; items: OverviewAccount[] }

export interface OverviewCommitment { id: string; source: string; description: string; date: string; amount: string; accountName: string | null; daysFromToday: number }
export interface OverviewCommitmentGroup { count: number; total: string; items: OverviewCommitment[] }
/** Próxima fatura de um cartão como obrigação de CAIXA pelo vencimento. `status`: Confirmed (total do banco), Provisional (projeção) ou AwaitingBankBill. */
export interface OverviewCardPayment { cardId: string; cardName: string; cardScope: string | null; competence: string; due: string; daysFromToday: number; obligation: string; paid: string; remaining: string; status: 'Confirmed' | 'Provisional' | 'AwaitingBankBill'; paymentState: string }
export interface OverviewCardPayments { state: BlockState; total: string; overdueRemaining: string; overdueCards: number; items: OverviewCardPayment[] }
export interface OverviewCommitments { state: BlockState; asOf: string; horizonThrough: string; horizonDays: number; overdue: OverviewCommitmentGroup; upcoming: OverviewCommitmentGroup; cardPayments?: OverviewCardPayments | null }

export interface OverviewGoal { goalId: string; name: string; priority: string; target: string; allocated: string; remaining: string; progressPercent: string; targetDate: string | null; funded: boolean }
export interface OverviewGoals { state: BlockState; activeCount: number; completedCount: number; items: OverviewGoal[] }

export type BudgetStatus = 'WithinBudget' | 'ApproachingLimit' | 'LimitReached' | 'Exceeded'
/**
 * Uma meta do mês (R03). `group` = Category, FixedExpenses ou AllCards. Consumido = efetivo + cartão lançado; `pending` = previsto adicional. As perspectivas
 * contam os mesmos gastos e nunca são somadas num total global. `reasons` explica dados incompletos (EstimatedCompetence, UndefinedNature, NoCards).
 */
export interface OverviewBudgetTarget {
  targetId: string; group: 'Category' | 'FixedExpenses' | 'AllCards'; categoryId: string | null; name: string; planned: string; consumed: string; pending: string; projected: string
  available: string; availableProjected: string; exceeded: string; percent: string | null; status: BudgetStatus; projectedStatus: BudgetStatus; state: BlockState; reasons: string[]
}
/** Resumo só das metas de categoria (não se sobrepõem, então a soma é válida). */
export interface OverviewBudgetCategories { count: number; planned: string | null; consumed: string | null; exceededCount: number; approachingCount: number; targets: OverviewBudgetTarget[] }
export interface OverviewBudget {
  state: BlockState; hasBudget: boolean; budgetId: string | null; competence: string
  fixed: OverviewBudgetTarget | null; cards: OverviewBudgetTarget | null; categories: OverviewBudgetCategories
}

export interface OverviewRecentItem { id: string; type: 'Income' | 'Expense'; amount: string; date: string; description: string; categoryName: string | null; accountName: string; awaitingClassification: boolean }
export interface OverviewRecent { state: BlockState; from: string; through: string; totalCount: number; items: OverviewRecentItem[] }

export interface OverviewClassification { state: BlockState; needsReview: number; notEvaluated: number }

export interface DashboardOverview {
  competence: string; asOf: string; scopeKind: 'primary' | 'all' | 'account'; scopeAccountId: string | null
  accounts: OverviewAccounts; commitments: OverviewCommitments; goals: OverviewGoals; budget: OverviewBudget; recent: OverviewRecent; classification: OverviewClassification
}

export function getDashboardOverview(workspaceId: string, competence: string, scope: PlanActualScopeChoice, signal?: AbortSignal): Promise<DashboardOverview> {
  const query = new URLSearchParams({ competence, scope: scope.kind })
  if (scope.kind === 'account' && scope.accountId) query.set('accountId', scope.accountId)
  return requestJson<DashboardOverview>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-dashboard/overview?${query}`,
    { signal, headers: { Accept: 'application/json' } }, { fallback: 'Falha ao carregar a Visão Geral.' }).then(data => {
    if (!data?.accounts || !data.commitments || !data.goals || !data.budget || !data.recent || !data.classification) throw new Error('Resposta inesperada da Visão Geral.')
    return data
  })
}
