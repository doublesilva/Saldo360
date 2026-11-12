import type { BudgetAction, BudgetControl, BudgetPressure, FinancialDecision, OverviewClassification } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { BudgetGroup } from '../budget/budgetText'
import type { OverviewNavigation, OverviewSection } from './Overview'
import { actionText, pressureText, recommendationText } from './budgetControlText'
import { caveat, coverageMessage, unavailableReason } from './decisionText'
import { formatDate } from '../../money'
import { explain, isNegative, isPositiveAmount, money } from './overviewText'

// T12.2 / dashboard Folio: pontos de atenção. Escolhe e ordena o que o servidor já entregou (cobertura, recuperação, orçamento, confiabilidade). Nenhum valor é calculado aqui:
// os valores exibidos são os textos decimais do servidor, e só se compara sinal ou presença.

export interface Destination { section: OverviewSection; context?: OverviewNavigation }
export interface PriorityAction { label: string; to?: Destination; reload?: boolean }
export interface PriorityCause { key: string; text: string; action?: PriorityAction }
/** `rank`: 1 = risco de caixa ou da meta (sempre visível), 2 = excesso de orçamento, 3 = pendências que reduzem a confiabilidade. */
export interface PriorityItem {
  key: string; rank: 1 | 2 | 3; title: string; impact: string; action: PriorityAction | null; causes?: PriorityCause[]
}

export const VISIBLE_ITEMS = 3

export function budgetDestination(action: BudgetAction, context: string | null, competence: string): Destination {
  switch (action) {
    case 'OpenCards': return { section: 'cards' }
    case 'OpenBudget': return { section: 'budgets', context: { competence, budgetGroup: (context ?? undefined) as BudgetGroup | undefined } }
    case 'OpenAgenda': return { section: 'calendar', context: { month: competence } }
    case 'OpenPlanning': return { section: 'planning', context: { planningTab: 'projection' } }
    case 'OpenReview': return { section: 'transactions', context: { review: { kind: 'category' } } }
    case 'OpenAccounts': return { section: 'accounts' }
  }
}

const overTargetKinds = new Set(['CardsOverTarget', 'OutsideCardsOverTarget', 'FixedExpensesOverTarget', 'CategoryOverTarget'])
// Recomendações que já viram item próprio (excesso, vencidos) ou entram agrupadas como pendência.
const handledElsewhere = new Set(['OverdueExpenses', 'ProjectedOverTotal', 'UncategorizedExpenses', 'CardCoverageIncomplete', 'IncomeEstimateUnconfirmed', 'BudgetTotalNotDefined', 'CardTargetNotDefined'])
const budgetSetup = new Set(['BudgetTotalNotDefined', 'CardTargetNotDefined'])

export interface PriorityInput {
  /** `control` = contexto bancário da conta escolhida (encargos, receitas, cheque especial); `pfControl` = orçamento PF (metas, excessos, pendências do orçamento), igual em qualquer conta. */
  competence: string; decision: FinancialDecision | null; control: BudgetControl | null; pfControl?: BudgetControl | null; plan: PlanActualDashboard | null; classification: OverviewClassification | null
}

export function buildPriorityItems({ competence, decision, control, pfControl = null, plan, classification }: PriorityInput): PriorityItem[] {
  const items: PriorityItem[] = []
  const agenda: Destination = { section: 'calendar', context: { month: competence } }
  const projection: Destination = { section: 'planning', context: { planningTab: 'projection' } }

  if (decision) {
    const { coverage, commitments, recovery } = decision
    if (coverage.state === 'Insufficient') items.push({
      key: 'coverage', rank: 1, title: coverageMessage(coverage),
      impact: coverage.deficit !== null ? `Déficit estimado de ${money(coverage.deficit)}.` : 'O valor do déficit ainda não pode ser calculado.', action: { label: 'Ver evolução na Agenda', to: agenda },
    })
    else if (coverage.state === 'CoveredWithCredit') items.push({
      key: 'coverage', rank: 1, title: coverageMessage(coverage), impact: 'Depende de crédito do banco, que não é saldo seu.', action: { label: 'Ver evolução na Agenda', to: agenda },
    })
    if (isPositiveAmount(commitments.overdue) && decision.context.period !== 'Closed') items.push({
      key: 'overdue', rank: 1, title: 'Há compromissos vencidos e não pagos.', impact: `${money(commitments.overdue)} em aberto.`, action: { label: 'Abrir a Agenda', to: agenda },
    })
    if ((recovery.state === 'Fixed' || recovery.state === 'Provisional') && isNegative(recovery.gapToTarget)) items.push({
      key: 'recovery', rank: 1, title: 'A recuperação projetada está abaixo da meta.',
      impact: `Faltam ${money((recovery.gapToTarget ?? '').replace('-', ''))} para a meta${recovery.state === 'Provisional' ? ' provisória' : ''}.`, action: { label: 'Abrir a Projeção financeira', to: projection },
    })
  }

  // Orçamento PF: meta total, cartões e fora dos cartões acima da meta, metas e recomendações. Não muda com a conta bancária escolhida.
  if (pfControl) {
    const total = pfControl.total
    if (total.excess !== null && isPositiveAmount(total.excess)) items.push({
      key: 'total-over', rank: 2, title: 'O total previsto de gastos passou da meta mensal.', impact: `Excesso de ${money(total.excess)} sobre a meta.`,
      action: { label: actionText.OpenBudget, to: budgetDestination('OpenBudget', null, competence) },
    })
    for (const pressure of pfControl.pressures) {
      if (!overTargetKinds.has(pressure.kind)) continue
      items.push({
        key: `pressure-${pressure.kind}-${pressure.subject ?? ''}`, rank: 2, title: pressureText(pressure), impact: pressureImpact(pressure),
        action: { label: actionText[pressure.action], to: budgetDestination(pressure.action, pressure.actionContext, competence) },
      })
    }
    for (const recommendation of pfControl.recommendations) {
      if (handledElsewhere.has(recommendation.code) || overTargetKinds.has(recommendation.code) && pfControl.pressures.some(p => p.kind === recommendation.code)) continue
      items.push({
        key: `recommendation-${recommendation.code}`, rank: 2, title: recommendationText[recommendation.code] ?? recommendation.code, impact: 'Recomendação do orçamento.',
        action: { label: actionText[recommendation.action], to: budgetDestination(recommendation.action, recommendation.actionContext, competence) },
      })
    }
  }
  // Contexto bancário da conta escolhida: encargos do cartão e receitas previstas que não entraram.
  if (control) for (const pressure of control.pressures) {
    if (pressure.kind !== 'CardFinanceCharges' && pressure.kind !== 'IncomeNotReceived') continue
    items.push({
      key: `pressure-${pressure.kind}-${pressure.subject ?? ''}`, rank: 2, title: pressureText(pressure), impact: pressureImpact(pressure),
      action: { label: actionText[pressure.action], to: budgetDestination(pressure.action, pressure.actionContext, competence) },
    })
  }

  const syncItem = syncAttentionItem(plan)
  if (syncItem) items.push(syncItem)
  const dataCauses = dataReliabilityCauses({ competence, decision, control, pfControl, plan })
  if (dataCauses.length > 0) items.push({
    key: 'reliability-data', rank: 3, title: 'Alguns valores podem estar desatualizados ou incompletos.', impact: 'Saldos, cobertura e totais podem mudar quando estes pontos forem resolvidos.',
    action: dataCauses[0].action ?? null, causes: dataCauses,
  })
  const setupCauses = budgetPendingCauses({ competence, control: pfControl, plan, classification })
  if (setupCauses.length > 0) items.push({
    key: 'reliability-budget', rank: 3, title: 'O orçamento tem pendências de classificação ou de metas.', impact: 'A comparação com a meta pode estar incompleta.',
    action: setupCauses[0].action ?? null, causes: setupCauses,
  })

  return items.sort((a, b) => a.rank - b.rank)
}

const syncProblem: Record<string, string> = { ConsentExpired: 'Autorização do banco expirada', Failed: 'Falha na sincronização bancária', CompletedWithErrors: 'Sincronização bancária concluída com erros' }
const lastSuccess = (iso: string | null) => iso ? `Último sucesso em ${formatDate(iso.slice(0, 10))} ${new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: 'America/Sao_Paulo' })}.` : 'Ainda sem sincronização bem-sucedida.'

/** Conexões que exigem atenção, pelos sinais do servidor (status e atraso por conexão). Não recalcula atraso aqui. */
export function affectedConnections(sync: NonNullable<PlanActualDashboard['sync']>) {
  return sync.connections.filter(c => c.stale === true || c.status in syncProblem)
}

function syncAttentionItem(plan: PlanActualDashboard | null): PriorityItem | null {
  if (!plan?.sync) return null
  const affected = affectedConnections(plan.sync)
  if (affected.length === 0) return null
  const name = (c: (typeof affected)[number]) => c.label ?? 'Conexão'
  const ids = affected.map(c => c.connectionId)
  if (affected.length === 1) {
    const c = affected[0]
    return {
      key: 'sync-attention', rank: 3, title: `${syncProblem[c.status] ?? 'Sincronização bancária atrasada'} · ${name(c)}`,
      impact: `${lastSuccess(c.lastSuccessAt)} Os dados dessa conexão requerem atenção.`, action: { label: 'Ver conexão', to: { section: 'automation', context: { automationTab: 'connections', connectionIds: ids } } },
    }
  }
  return {
    key: 'sync-attention', rank: 3, title: `Sincronização bancária com problema · ${affected.length} conexões`,
    impact: `${affected.map(name).join(', ')}. Os dados dessas conexões requerem atenção.`, action: { label: 'Ver conexões', to: { section: 'automation', context: { automationTab: 'connections', connectionIds: ids } } },
  }
}

function pressureImpact(pressure: BudgetPressure): string {
  if (pressure.amount === null) return 'O impacto ainda não pode ser calculado.'
  return pressure.amountKind === 'OverTarget' ? `Excesso de ${money(pressure.amount)} sobre a meta.` : `${money(pressure.amount)}.`
}

function dataReliabilityCauses({ competence, decision, control, pfControl, plan }: Pick<PriorityInput, 'competence' | 'decision' | 'control' | 'pfControl' | 'plan'>): PriorityCause[] {
  const causes: PriorityCause[] = []
  const add = (cause: PriorityCause) => { if (!causes.some(c => c.key === cause.key || c.text === cause.text)) causes.push(cause) }
  const sync = plan?.sync
  if (plan && !plan.snapshotConsistent || decision && !decision.snapshotConsistent) add({ key: 'snapshot', text: caveat('PositionsInconsistent'), action: { label: 'Atualizar', reload: true } })
  // Problemas de sincronização com conexão identificável têm item próprio (syncAttentionItem); aqui só o caso sem conexão apontada.
  if (sync && affectedConnections(sync).length === 0 && (sync.stale || ['Failed', 'ConsentExpired', 'CompletedWithErrors'].includes(sync.state))) {
    add({
      key: 'sync', text: sync.state === 'ConsentExpired' ? 'A autorização do banco expirou.' : sync.state === 'Failed' ? 'A última sincronização com o banco falhou.' : 'Os dados do banco podem estar desatualizados.',
      action: { label: 'Ver conexões', to: { section: 'automation', context: { automationTab: 'connections' } } },
    })
  }
  if (plan?.coverage.reviewAffectsConfidence) add({
    key: 'review', text: `${plan.coverage.reviewCount} movimentação(ões) do banco aguardam sua decisão (ou foram ignoradas por você) e não entram nos totais.`, action: { label: 'Revisar em Contas', to: { section: 'accounts' } },
  })
  if (decision?.coverage.state === 'Unavailable') add({
    key: 'coverage-unavailable', text: `Não é possível confirmar a cobertura: ${unavailableReason(decision.coverage.unavailableReason) ?? 'faltam dados essenciais.'}`, action: { label: 'Ver contas', to: { section: 'accounts' } },
  })
  for (const code of new Set([...(decision?.commitments.caveats ?? []), ...(decision?.coverage.caveats ?? [])])) add({ key: `caveat-${code}`, text: caveat(code), action: { label: 'Ver contas', to: { section: 'accounts' } } })
  if (plan && (!plan.coverage.complete || plan.situation.state !== 'Complete')) add({ key: 'projection', text: 'Projeção incompleta: parte dos dados ainda não pode ser calculada.' })
  if (control?.recommendations.some(r => r.code === 'IncomeEstimateUnconfirmed')) add({
    key: 'income-estimate', text: recommendationText.IncomeEstimateUnconfirmed, action: { label: actionText.OpenPlanning, to: budgetDestination('OpenPlanning', null, competence) },
  })
  if (pfControl?.recommendations.some(r => r.code === 'CardCoverageIncomplete')) add({ key: 'card-coverage', text: recommendationText.CardCoverageIncomplete, action: { label: actionText.OpenCards, to: { section: 'cards' } } })
  const overdraft = control?.pressures.find(p => p.kind === 'OverdraftChargesNotEstimable')
  if (overdraft) add({ key: 'overdraft-charges', text: pressureText(overdraft) })
  // As explicações das ressalvas só complementam um aviso que já existe: sozinhas não são um alerta.
  if (causes.length > 0 && plan) for (const text of explain(Array.from(new Set([...plan.situation.reasons, ...plan.coverage.reasons, ...plan.income.reasons, ...plan.expenses.consumption.reasons])))) add({ key: `reason-${text}`, text })
  return causes
}

function budgetPendingCauses({ competence, control, plan, classification }: { competence: string; control: BudgetControl | null | undefined; plan: PriorityInput['plan']; classification: PriorityInput['classification'] }): PriorityCause[] {
  const causes: PriorityCause[] = []
  const pending = classification && classification.state === 'Complete' ? classification.needsReview + classification.notEvaluated : 0
  if (pending > 0) causes.push({
    key: 'classification', text: `Há ${pending} lançamento(s) pendentes de classificação (todos os importados do banco, não só deste mês).`,
    action: { label: 'Revisar classificação', to: { section: 'automation', context: { automationTab: 'automations', review: { kind: 'category' } } } },
  })
  else if (control?.recommendations.some(r => r.code === 'UncategorizedExpenses')) causes.push({ key: 'uncategorized', text: recommendationText.UncategorizedExpenses, action: { label: actionText.OpenReview, to: budgetDestination('OpenReview', null, competence) } })
  for (const recommendation of control?.recommendations ?? []) {
    if (budgetSetup.has(recommendation.code)) causes.push({
      key: recommendation.code, text: recommendationText[recommendation.code], action: { label: actionText[recommendation.action], to: budgetDestination(recommendation.action, recommendation.actionContext, competence) },
    })
  }
  if (plan?.scope.primaryMissing) causes.push({ key: 'primary', text: 'Nenhuma conta principal definida: mostrando todas as contas.', action: { label: 'Definir em Contas', to: { section: 'accounts' } } })
  return causes
}

/** Os três primeiros por prioridade, mais todo item de risco de caixa ou da meta (rank 1): um alerta crítico nunca fica atrás de "Ver todas". */
export function visibleItems(items: PriorityItem[], expanded: boolean): PriorityItem[] {
  if (expanded) return items
  return items.filter((item, index) => index < VISIBLE_ITEMS || item.rank === 1)
}
