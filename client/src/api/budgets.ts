import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { FinancialCategory } from './categories'
import type { ExpenseNature } from './expenseNature'

/**
 * Tipo de meta: por categoria (a principal inclui as subcategorias), despesas fixas (toda despesa fixa, de qualquer categoria e meio de pagamento) e
 * todos os cartões (somados). As três são perspectivas dos mesmos gastos: coexistem e nunca se somam.
 */
export type BudgetGroupingType = 'Category' | 'FixedExpenses' | 'AllCards'

export interface FinancialBudgetTarget {
  id: string
  name: string
  plannedAmount: string
  groupingType: BudgetGroupingType
  /** Só nas metas por categoria. */
  category: (Pick<FinancialCategory, 'id' | 'name' | 'isActive'> & { parentName?: string | null; path?: string }) | null
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

export type FinancialBudgetAlertStatus = 'WithinBudget' | 'ApproachingLimit' | 'LimitReached' | 'Exceeded'

/** Sinais de dados incompletos de uma meta. Nunca viram "zero confirmado". */
export interface BudgetCoverage {
  /** Compras de cartão cuja competência veio só do calendário configurado do cartão (sem a fatura do banco). */
  estimatedCompetenceCount: number
  /** Despesas do mês sem tipo definido (só informado na meta de fixas): não entram nela. */
  undefinedNatureCount: number
  undefinedNatureAmount: string
  /** Meta de cartões sem nenhum cartão cadastrado: não há consumo a medir. */
  noCards: boolean
}

/**
 * Consumido = efetivo + compras/parcelas já lançadas nos cartões. Previsto adicional (`committed`) = compromissos cadastrados ainda não realizados.
 * Projetado = consumido + previsto. Disponível = limite − consumido; disponível projetado = limite − projetado.
 */
export interface FinancialBudgetTargetExecution {
  targetId: string
  groupingType: BudgetGroupingType
  categoryId: string | null
  planned: string
  consumed: string
  realized: string
  creditCardCommitted: string
  committed: string
  projectedConsumption: string
  available: string
  availableProjected: string
  exceededAmount: string
  exceededProjected: string
  consumptionPercentage: string | null
  projectedPercentage: string | null
  status: FinancialBudgetAlertStatus
  projectedStatus: FinancialBudgetAlertStatus
  alertMessage: string
  coverage: BudgetCoverage
}

/** Resumo conjunto só das metas de categoria (não se sobrepõem). Fixas e cartões nunca entram em soma alguma. */
export interface FinancialBudgetCategoriesSummary {
  planned: string
  consumed: string
  realized: string
  committed: string
  projectedConsumption: string
  available: string
  availableProjected: string
  exceededAmount: string
  consumptionPercentage: string | null
  exceededProjected: string
  projectedPercentage: string | null
  status: FinancialBudgetAlertStatus
  /** Situação pela projeção (consumido + previsto adicional): é a que a Agenda, de natureza projetada, usa. */
  projectedStatus: FinancialBudgetAlertStatus
  alertMessage: string
  approachingCount: number
  reachedCount: number
  exceededCount: number
  targetCount: number
}

export interface FinancialBudgetExecution {
  budgetId: string
  competence: string
  categories: FinancialBudgetCategoriesSummary
  targets: FinancialBudgetTargetExecution[]
}

export type BudgetFactState = 'Realized' | 'CardLaunched' | 'Pending'
export type BudgetFactOrigin = 'Transaction' | 'PlannedTransaction' | 'DebtPayment' | 'DebtInstallment' | 'CardInstallment' | 'CardKnownInstallment' | 'CardAdjustment'

export interface BudgetCompositionItem {
  origin: BudgetFactOrigin
  recordId: string
  date: string
  /** Assinado: crédito/estorno de cartão é negativo. */
  amount: string
  state: BudgetFactState
  categoryId: string | null
  categoryPath: string | null
  expenseNature: ExpenseNature
  description: string
  cardId: string | null
  cardName: string | null
  accountName: string | null
  invoiceCompetence: string | null
  /** False: a competência do cartão é estimativa (sem a fatura do banco). */
  competenceReliable: boolean
}

export interface BudgetCompositionCard { cardId: string; cardName: string; consumed: string; count: number }

export interface BudgetComposition {
  budgetId: string
  competence: string
  targetId: string
  groupingType: BudgetGroupingType
  amounts: Omit<FinancialBudgetTargetExecution, 'targetId' | 'groupingType' | 'categoryId' | 'status' | 'projectedStatus' | 'alertMessage' | 'coverage'>
  status: FinancialBudgetAlertStatus
  coverage: BudgetCoverage
  page: number
  pageSize: number
  totalCount: number
  items: BudgetCompositionItem[]
  /** Participação de cada cartão: só informativa (não há teto individual). */
  cards: BudgetCompositionCard[]
}

export type BudgetSaveResult =
  | { ok: true; budget: FinancialBudget }
  | { ok: false; errors: Record<string, string[]>; conflict?: boolean }

const jsonHeaders = { Accept: 'application/json' }

function budgetsUrl(workspaceId: string, budgetId?: string): string {
  const base = `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/budgets`
  return budgetId ? `${base}/${encodeURIComponent(budgetId)}` : base
}

async function readProblem(response: Response): Promise<Record<string, string[]>> {
  const body = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]>; title?: string }
  return body.errors ?? { general: [body.title ?? `Falha na API (HTTP ${response.status}).`] }
}

export async function listBudgets(workspaceId: string, competence: string, signal?: AbortSignal): Promise<FinancialBudget[]> {
  const response = await fetch(`${budgetsUrl(workspaceId)}?competence=${encodeURIComponent(competence)}`, {
    signal,
    headers: jsonHeaders,
  })
  if (!response.ok) throw new Error(`Falha ao carregar o orçamento (HTTP ${response.status}).`)
  return (await response.json()) as FinancialBudget[]
}

export async function getBudget(workspaceId: string, budgetId: string, signal?: AbortSignal): Promise<FinancialBudget> {
  const response = await fetch(budgetsUrl(workspaceId, budgetId), { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar o orçamento (HTTP ${response.status}).`)
  return (await response.json()) as FinancialBudget
}

export async function getBudgetExecution(
  workspaceId: string,
  budgetId: string,
  signal?: AbortSignal,
): Promise<FinancialBudgetExecution> {
  const response = await fetch(`${budgetsUrl(workspaceId, budgetId)}/execution`, { signal, headers: jsonHeaders })
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
  if (response.status === 404) throw new Error('Orçamento não encontrado.')
  throw new Error(`Falha ao salvar o orçamento (HTTP ${response.status}).`)
}

export function createBudget(workspaceId: string, competence: string, description?: string): Promise<BudgetSaveResult> {
  return writeBudget('POST', budgetsUrl(workspaceId), { competence, description: description || null }, 201)
}

export function updateBudget(workspaceId: string, budgetId: string, description: string): Promise<BudgetSaveResult> {
  return writeBudget('PUT', budgetsUrl(workspaceId, budgetId), { description }, 200)
}

export type TargetSaveResult = BudgetSaveResult

export type NewBudgetTarget =
  | { groupingType: 'Category'; categoryId: string; name: string; plannedAmount: string }
  | { groupingType: 'FixedExpenses' | 'AllCards'; plannedAmount: string }

/** Cria a meta. Fixas e todos os cartões não têm categoria nem nome (o servidor define) e há no máximo uma de cada por orçamento. */
export function addBudgetTarget(workspaceId: string, budgetId: string, target: NewBudgetTarget): Promise<TargetSaveResult> {
  return writeBudget('POST', `${budgetsUrl(workspaceId, budgetId)}/targets`, target, 201)
}

/** Registros que compõem o consumo de uma meta, paginados no servidor (mesma lista e regra do resumo). */
export async function getBudgetComposition(
  workspaceId: string,
  budgetId: string,
  targetId: string,
  page: number,
  pageSize: number,
  signal?: AbortSignal,
): Promise<BudgetComposition> {
  const response = await fetch(`${budgetsUrl(workspaceId, budgetId)}/targets/${encodeURIComponent(targetId)}/composition?page=${page}&pageSize=${pageSize}`, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar a composição da meta (HTTP ${response.status}).`)
  return (await response.json()) as BudgetComposition
}

export function updateBudgetTarget(
  workspaceId: string,
  budgetId: string,
  targetId: string,
  name: string,
  plannedAmount: string,
): Promise<TargetSaveResult> {
  return writeBudget('PUT', `${budgetsUrl(workspaceId, budgetId)}/targets/${encodeURIComponent(targetId)}`, {
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

export function archiveBudget(workspaceId: string, budgetId: string) {
  return archive(budgetsUrl(workspaceId, budgetId), 'o orçamento')
}

export function archiveBudgetTarget(workspaceId: string, budgetId: string, targetId: string) {
  return archive(`${budgetsUrl(workspaceId, budgetId)}/targets/${encodeURIComponent(targetId)}`, 'a meta')
}
