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

export interface OverviewTransferLine { direction: 'Received' | 'Sent' | 'Between'; counterpart: string; counterpart2: string | null; amount: string; count: number }
export interface OverviewTransfers {
  state: BlockState; received: string; sent: string; net: string; count: number; items: OverviewTransferLine[]
  externalIncome?: string | null; externalExpenses?: string | null; totalInflows?: string | null; totalOutflows?: string | null; available?: string | null; internalMoved?: string | null
  refunds?: string | null; cashExpenses?: string | null
}

/** Fluxo de caixa (aditivo): ponte de saldo, posição bancária e conciliação. Todos os valores vêm calculados do servidor. */
export interface OverviewCashAccountIssue { accountId: string; name: string; reason: string }
export interface OverviewCashTransfer { id: string; date: string; description: string; source: string; destination: string; amount: string; relation: 'Inbound' | 'Outbound' | 'Internal'; effect: string }
export interface OverviewBankPosition {
  state: 'Complete' | 'Partial' | 'Unavailable'; total: string | null; observedAt: string | null
  accounts: { accountId: string; name: string; amount: string; kind: string; observedAt: string; asOfDate: string; reliability: string }[]; missing: OverviewCashAccountIssue[]
}
export interface OverviewReconciliation { status: 'Verified' | 'Divergent' | 'NotVerifiable'; bank: string | null; calculated: string | null; difference: string | null; cutoffDate: string | null; reasons: string[]; accounts: OverviewCashAccountIssue[] }
export type RecoveryBand = 'Exceeded' | 'Met' | 'Attention' | 'Critical' | 'NoTarget'
/** Recuperação do cheque especial (aditivo; só quando a conta abre a competência com saldo negativo). Tudo vem calculado do servidor. */
export interface OverviewRecovery {
  openingUsed: string; target: string | null; targetState: 'Provisional' | 'Fixed' | 'Pending' | 'NotRegistered'; targetFactor: string | null; capacity: string | null
  targetFixedAt: string | null; fixDate: string; legacyTarget: boolean; pendingReason: string | null
  currentBalance: string; currentAsOf: string; recoveryCurrent: string
  projectedClosing: string | null; projectedRecovery: string | null; projectedAchievementPercent: string | null; band: RecoveryBand
  finalRecovery: string | null; finalAchievementPercent: string | null
}
export interface OverviewCashFlow {
  state: 'Complete' | 'Partial'; competence: string; from: string; through: string; inProgress: boolean; accountCount: number
  opening: string | null; openingMissing: OverviewCashAccountIssue[]
  inflows: { total: string; credits: string; refunds: string | null; transfersReceived: string }
  outflows: { total: string; debits: string; transfersSent: string }
  closing: string | null; identityHolds: boolean | null; snapshotConsistent: boolean
  bankPosition: OverviewBankPosition; reconciliation: OverviewReconciliation; internalMoved: string; transfers: OverviewCashTransfer[]
  byCompetence: { classifiedIncome: string | null; expenses: string | null; difference: string | null }
  recovery?: OverviewRecovery | null
}

/** T11.1: decisão financeira (aditivo; null quando indisponível, servidores antigos não enviam). Tudo calculado no servidor; valores são texto decimal exato, null = indisponível. */
export type DecisionPeriod = 'Current' | 'Future' | 'Closed'
export type CoverageState = 'Covered' | 'CoveredWithCredit' | 'Insufficient' | 'Unavailable'
export interface DecisionContext {
  competence: string; currentCompetence: string; period: DecisionPeriod; scopeKind: 'primary' | 'all' | 'account'; accountId: string | null; accountName: string | null; accountScope: string | null
  referenceDate: string; bankPositionDate: string | null; bankObservedAt: string | null; lastSyncedAt: string | null
  accounts: { accountId: string; name: string; scope: string; isPrimary: boolean }[]; bankBalance: string | null; overdraftUsed: string | null
}
export interface DecisionCommitments {
  period: DecisionPeriod; reliability: 'Complete' | 'Preliminary'; total: string; overdue: string; inCompetence: string; beforeCompetence: string; paidInMonth: string | null
  byNature: { nature: string; overdue: string; inCompetence: string; beforeCompetence: string; total: string; count: number }[]
  byDueDate: { date: string; amount: string }[]; caveats: string[]
}
export interface DecisionAccountCoverage {
  accountId: string; name: string; scope: string; state: CoverageState; reliability: string; guaranteedState: CoverageState; dependsOnInferredIncome: boolean
  minimumBalance: string | null; minimumDate: string | null; firstNegativeDate: string | null; firstBeyondLimitDate: string | null; limit: string | null
  creditNeeded: string | null; deficit: string | null; unavailableReason: string | null; caveats: string[]; projectedClosing: string | null
}
export interface DecisionCoverage {
  state: CoverageState; reliability: string; period: DecisionPeriod; minimumBalance: string | null; firstNegativeDate: string | null; firstBeyondLimitDate: string | null; deficit: string | null
  obligationsConsidered: string; expectedInflows: { date: string; amount: string; kind: 'Income' | 'Transfer'; certainty: 'Registered' | 'Inferred' }[]
  caveats: string[]; unavailableReason: string | null; accounts: DecisionAccountCoverage[]; projectedClosing: string | null
}
export interface DecisionSafeMargin {
  state: 'Calculated' | 'Preliminary' | 'Unavailable'; amount: string | null; scope: 'CashSpend'; referenceDate: string; bindingConstraint: string | null
  constraints: { kind: string; applied: boolean; slack: string | null; binding: boolean; reason: string | null }[]; reasons: string[]; notes: string[]
}
export interface DecisionRecovery {
  state: 'Provisional' | 'Fixed' | 'Pending' | 'NotRegistered' | 'NotApplicable' | 'Unavailable'; reason: string | null; openingUsed: string | null; target: string | null
  targetFixedAt: string | null; legacyTarget: boolean; effectiveRecovery: string | null; projectedRecovery: string | null; gapToTarget: string | null; expectedClosingUsed: string | null; band: RecoveryBand | null
}
export interface FinancialDecision {
  state: 'Complete' | 'Partial'; context: DecisionContext; commitments: DecisionCommitments; coverage: DecisionCoverage; safeMargin: DecisionSafeMargin; recovery: DecisionRecovery
  snapshotConsistent: boolean; notes: string[]
}

/** T11.2: consumo do orçamento (aditivo; null quando indisponível). Dois grupos exclusivos (cartões e fora dos cartões); tudo calculado no servidor, null = não definido (nunca zero). */
export type BudgetGroupState = 'Complete' | 'Partial' | 'TargetNotDefined' | 'TargetNotAttributable' | 'NoCards'
export type BudgetGroupStatus = 'WithinBudget' | 'Exceeded' | 'NoTarget'
export interface BudgetControlGroup {
  key: 'Cards' | 'OutsideCards'; state: BudgetGroupState; target: string | null; realized: string; committed: string; estimated: string; projected: string
  margin: string | null; excess: string | null; excessRealized: string | null; realizedPercent: string | null; projectedPercent: string | null; status: BudgetGroupStatus; reasons: string[]
}
export interface BudgetControlTotal {
  targetDefined: boolean; target: string | null; realized: string; committed: string; estimated: string; projected: string; margin: string | null; excess: string | null
  realizedPercent: string | null; projectedPercent: string | null; status: BudgetGroupStatus
}
export type TargetOrigin = 'Inherited' | 'Customized' | 'Historical' | 'Undefined'
export type BudgetAction = 'OpenCards' | 'OpenBudget' | 'OpenAgenda' | 'OpenPlanning' | 'OpenReview' | 'OpenAccounts'
export interface BudgetPressure {
  rank: number; kind: string; subject: string | null; amount: string | null; amountKind: 'OverTarget' | 'Overdue' | 'Charged' | 'NotEstimable'; referenceAmount: string | null; referenceKind: string
  impact: 'Budget' | 'Cash' | 'BudgetAndCash' | 'NotEstimable'; quality: 'Confirmed' | 'Projected' | 'Estimated' | 'Preliminary' | 'NotEstimable'; action: BudgetAction; actionContext: string | null
}
export interface BudgetRecommendation { code: string; action: BudgetAction; actionContext: string | null }
export interface BudgetControl {
  state: 'Complete' | 'Partial' | 'TargetNotDefined'; competence: string; period: DecisionPeriod; scopeKind: 'primary' | 'all' | 'account' | 'pf'; targetsAttributable: boolean
  total: BudgetControlTotal; cards: BudgetControlGroup; outsideCards: BudgetControlGroup; pressures: BudgetPressure[]; recommendations: BudgetRecommendation[]; notes: string[]
  /** T12.2: de onde vem a meta total (resolvedor único da T12.1); `totalTargetFrom` = início da vigência do padrão (AAAA-MM). Ausente em servidores antigos e quando a meta não se aplica a uma conta isolada. */
  totalTargetOrigin?: TargetOrigin | null; totalTargetFrom?: string | null
  /** Aditivo: sincronização mais antiga das contas integradas do conjunto do controle (horário verificável da origem). Nulo sem integração; ausente em servidores antigos. */
  dataUpdatedAt?: string | null
}

/**
 * Distribuição das despesas realizadas do mês por categoria principal (dashboard Folio). Mesmos fatos do orçamento, agrupados e ordenados no servidor. `kind`: Category, Others ("Outras
 * categorias", só apresentação; `categoryCount` diz quantas agrupa) ou Uncategorized ("Sem categoria", sempre por último). `percentage` nulo quando o total líquido não permite percentuais;
 * quando existem, as linhas somam 100,0. `reference` = "Despesas do mês" do mesmo escopo; divergência vira `Partial` com o motivo. No `overview` vêm as cinco maiores categorias;

 */
export interface ExpenseCategoryShare { categoryId: string | null; name: string; amount: string; percentage: string | null; kind: 'Category' | 'Others' | 'Uncategorized'; categoryCount: number; gross: string }
export interface ExpenseReconciliation { bucket: 'Bank' | 'Cards'; distributed: string; reference: string; difference: string }
export interface ExpenseDistribution {
  state: 'Complete' | 'Partial' | 'Unavailable'; competence: string; total: string | null; reference: string | null; categories: ExpenseCategoryShare[]
  reasons: ('TotalDiffersFromExpenses' | 'ReferenceUnavailable' | 'BankBasisDiffers' | 'CardBasisDiffers' | string)[]; categoryCount: number
  /** Só gastos, devoluções e estornos (negativo) à parte, e a conferência por bloco com "Despesas do mês". `total` = gastos + devoluções. */
  gross: string; adjustments: string; reconciliation: ExpenseReconciliation[] | null
}

/**
 * T14.3: resultado mensal composto no servidor a partir do plan-actual, das transferências e da decisão. Duas perspectivas distintas: CAIXA (`inflows`/`outflows`/`result`; fatura paga é saída,
 * transferências só alteram o caixa de uma conta) e ORÇAMENTÁRIA (`budget`: receitas menos consumo da competência). `realized` nulo = nada realizado (mês futuro). `bridge.unreconciled` é
 * a diferença que os dados não explicam; nunca é absorvida por um ajuste.
 */
export interface MonthlyAmounts { realized: string | null; pending: string | null; projected: string | null }
export interface MonthlyComponent { code: string; side: 'Inflow' | 'Outflow'; status: 'Realized' | 'Pending'; amount: string; target: 'Entries' | 'Calendar' | 'Cards' }
export interface MonthlyBridge { state: 'Complete' | 'Partial' | 'Unavailable'; observedBalance: string | null; observedDate: string | null; inflowsAfter: string | null; outflowsAfter: string | null; projectedClosing: string | null; unreconciled: string | null; reasons: string[] }
export interface MonthlyOverdraft { contractedLimit: string; used: string; available: string; utilizationPercent: string | null; exceedsLimit: boolean; projectedUsed: string | null; projectedAvailable: string | null; projectedPercent: string | null }
export interface MonthlyResult {
  state: 'Complete' | 'Partial'; periodKind: 'Past' | 'Current' | 'Future'; highlight: 'Realized' | 'Projected'; status: 'Positive' | 'Negative' | 'Neutral' | 'Unavailable'
  inflows: MonthlyAmounts; outflows: MonthlyAmounts; result: MonthlyAmounts; transfersReceived: string | null; transfersSent: string | null
  components: MonthlyComponent[]; budget: { income: string | null; consumption: string | null; realized: string | null; projected: string | null }; bridge: MonthlyBridge; reasons: string[]; overdraft?: MonthlyOverdraft | null
}

export interface DashboardOverview {
  competence: string; asOf: string; scopeKind: 'primary' | 'all' | 'account'; scopeAccountId: string | null
  accounts: OverviewAccounts; commitments: OverviewCommitments; goals: OverviewGoals; budget: OverviewBudget; recent: OverviewRecent; classification: OverviewClassification
  /** Aditivo: servidores antigos não enviam. */
  transfers?: OverviewTransfers | null
  /** Aditivo: fluxo de caixa (null quando indisponível; servidores antigos não enviam). */
  cashFlow?: OverviewCashFlow | null
  /** Aditivo (T11.1): compromissos restantes, cobertura, margem segura e meta, todos da MESMA leitura. */
  decision?: FinancialDecision | null
  /** Aditivo (T11.2): consumo do orçamento, principais pressões e ações recomendadas. */
  budgetControl?: BudgetControl | null
  /** Aditivo (dashboard Folio): para onde foram as despesas realizadas do mês. */
  expenseDistribution?: ExpenseDistribution | null
  /** Aditivo: orçamento PF (todas as contas PF pelo `FinancialScope`), independente da conta bancária escolhida; metas sempre atribuíveis. `budgetControl` segue o escopo bancário. */
  pfBudgetControl?: BudgetControl | null
  /** Aditivo (T14.3): resultado de caixa × resultado orçamentário e ponte do saldo (null quando indisponível). */
  monthlyResult?: MonthlyResult | null
}

/**
 * Resumo do orçamento PF de uma competência (tela de Orçamento): o mesmo total de `pfBudgetControl.total` da Visão Geral, sem os demais blocos do dashboard.
 * `total` nulo (estado Unavailable) = indisponível, nunca zerado. Competência e valores vêm da mesma resposta.
 */
export interface BudgetSummary {
  state: 'Complete' | 'Partial' | 'TargetNotDefined' | 'Unavailable'; competence: string; scopeKind: string; total: BudgetControlTotal | null; notes: string[]
  totalTargetOrigin?: TargetOrigin | null; totalTargetFrom?: string | null
}
export function getBudgetSummary(workspaceId: string, competence: string, signal?: AbortSignal): Promise<BudgetSummary> {
  return requestJson<BudgetSummary>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-dashboard/budget-summary?${new URLSearchParams({ competence })}`,
    { signal, headers: { Accept: 'application/json' } }, { fallback: 'Falha ao carregar o acompanhamento do orçamento.' }).then(data => {
    if (!data || typeof data !== 'object' || !('state' in data) || !('competence' in data)) throw new Error('Resposta inesperada do acompanhamento do orçamento.')
    return data
  })
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

/**
 * Cartões do orçamento PF na competência, sob demanda (mesma base de `pfBudgetControl.cards.projected`). `invoiceState`: Closed, Open, Future ou ClosingUnconfirmed (nunca tratado como fechada);
 * `paymentState` (Unpaid, Partial, Paid, Overpaid) é separado do estado da fatura. `total` = soma dos cartões + `foodApplied` (previsão alimentar adicional que coube dentro da meta; `foodReserve` é a submeta nominal, não somada); divergência com `official` = Partial.
 */
export interface BudgetCardItem {
  cardId: string; name: string; projected: string; confirmed: string; pendingKnown: string; estimated: string
  invoiceState: 'Closed' | 'Open' | 'Future' | 'ClosingUnconfirmed'; closing: string | null; due: string | null; datesEstimated: boolean
  quality: 'Confirmed' | 'Projected' | 'Incomplete'; paymentState: 'Unpaid' | 'Partial' | 'Paid' | 'Overpaid' | null
}
export interface BudgetCards {
  state: 'Complete' | 'Partial'; competence: string; total: string; allocated: string; foodApplied: string; foodReserve: string | null; official: string | null
  cards: BudgetCardItem[]; reasons: ('MissingPayerAccount' | 'UnexplainedDifference' | 'OfficialTotalUnavailable' | string)[]
}

export function getBudgetCards(workspaceId: string, competence: string, signal?: AbortSignal): Promise<BudgetCards> {
  return requestJson<BudgetCards>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-dashboard/budget-cards?${new URLSearchParams({ competence })}`,
    { signal, headers: { Accept: 'application/json' } }, { fallback: 'Falha ao carregar os cartões do orçamento.' })
}

/**
 * Previsão de despesas do orçamento PF por categoria principal (dentro da Meta total), sob demanda. `projected` = `pfBudgetControl.total.projected`: categorias + componentes não alocados fecham com ele.
 * `categories` = cinco maiores, "Outras categorias" (`kind` Others) e "Sem categoria"; `allCategories` = todas ("Ver todas"). Estimativas não têm categoria (`unallocatedEstimated`).
 */
export interface BudgetCategoryChild { categoryId: string | null; name: string; projected: string }
export interface BudgetCategorySource { group: 'Payment' | 'Nature'; key: string; amount: string }
export interface BudgetCategoryLine {
  categoryId: string | null; name: string; kind: 'Category' | 'Others' | 'Uncategorized'; categoryCount: number
  realized: string; committed: string; estimated: string; projected: string; children: BudgetCategoryChild[]; sources: BudgetCategorySource[]
}
export interface BudgetCategories {
  state: 'Complete' | 'Partial'; competence: string; scope: string; target: string | null; realized: string; committed: string; estimated: string; projected: string
  categories: BudgetCategoryLine[]; allCategories: BudgetCategoryLine[]; categoryCount: number
  unallocatedRealized: string; unallocatedCommitted: string; unallocatedEstimated: string; reasons: string[]
}

export function getBudgetCategories(workspaceId: string, competence: string, signal?: AbortSignal): Promise<BudgetCategories> {
  return requestJson<BudgetCategories>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-dashboard/budget-categories?${new URLSearchParams({ competence })}`,
    { signal, headers: { Accept: 'application/json' } }, { fallback: 'Falha ao carregar as despesas por categoria.' })
}
