import { useCallback } from 'react'
import { getPlanActual, type PlanActualDashboard, type PlanActualScopeChoice } from '../../api/planActual'
import { getSixMonthPlan } from '../../api/monthlyPlan'
import { getDashboardOverview, type OverviewCommitment } from '../../api/overview'
import { addCalendarMonths } from '../../dates'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { OverviewIndicators, OverviewNotices } from './OverviewIndicators'
import { OverviewEvolution } from './OverviewEvolution'
import { OverviewBudget } from './OverviewBudget'
import type { BudgetGroup } from '../budget/budgetText'
import type { ExpenseNature } from '../../api/expenseNature'
import { OverviewAccountsBlock, OverviewCommitmentsBlock, OverviewGoalsBlock, OverviewRecentBlock } from './OverviewAside'
import { monthTitle, periodLabels, scopeLabel } from './overviewText'
import { useLoad } from './useLoad'
import '../../Overview.css'

export type OverviewSection = 'accounts' | 'cards' | 'goals' | 'budgets' | 'calendar' | 'transactions' | 'automation'
/** Contexto levado ao detalhe: o mês da Agenda, a competência do orçamento ou o recorte da lista de lançamentos. */
export interface OverviewNavigation { month?: string; competence?: string; budgetGroup?: BudgetGroup; transactions?: { from: string; to: string; accountId: string | null; expenseNature?: ExpenseNature }; newTransaction?: boolean; automationTab?: 'connections' | 'automations' | 'history'; review?: { kind: 'nature' | 'category'; from?: string; to?: string } }

export interface OverviewSelection { competence: string; scope: PlanActualScopeChoice }

const EVOLUTION_MONTHS_BEFORE = 3

/**
 * Visão Geral (R01): um único resumo, em blocos independentes. Indicadores e avisos vêm de `plan-actual`; contas, compromissos, metas, orçamento e últimos
 * lançamentos, de `financial-dashboard/overview`; a evolução, de `monthly-plan/six-months`. Cada bloco carrega, falha e se recarrega sozinho.
 */
export function Overview({ workspaceId, refreshKey = 0, selection, onSelectionChange, onNavigate }: {
  workspaceId: string; refreshKey?: number; selection: OverviewSelection; onSelectionChange: (selection: OverviewSelection) => void
  onNavigate: (section: OverviewSection, context?: OverviewNavigation) => void
}) {
  const { competence, scope } = selection
  const scopeKey = `${scope.kind}:${scope.accountId ?? ''}`
  const key = `${workspaceId}|${competence}|${scopeKey}|${refreshKey}`

  const plan = useLoad(key, useCallback((signal: AbortSignal) => getPlanActual(workspaceId, competence, scope, signal), [workspaceId, competence, scope]))
  const overview = useLoad(key, useCallback((signal: AbortSignal) => getDashboardOverview(workspaceId, competence, scope, signal), [workspaceId, competence, scope]))
  const evolutionFrom = addCalendarMonths(`${competence}-01`, -EVOLUTION_MONTHS_BEFORE).slice(0, 7)
  const evolution = useLoad(`${key}|${evolutionFrom}`, useCallback((signal: AbortSignal) => getSixMonthPlan(workspaceId, evolutionFrom, scope, signal), [workspaceId, evolutionFrom, scope]))

  const planData = plan.load.state === 'loaded' ? plan.load.data : null
  const overviewData = overview.load.state === 'loaded' ? overview.load.data : null
  // A API é a fonte da verdade do escopo: guarda-se a última lista de contas para o seletor não esvaziar durante a recarga.
  const evolutionData = evolution.load.state === 'loaded' ? evolution.load.data : evolution.previous
  const choices = planData?.scope ?? plan.previous?.scope ?? null
  const reloadAll = () => { plan.retry(); overview.retry(); evolution.retry() }

  const scopeText = overviewData
    ? overviewData.scopeKind === 'all' || !overviewData.scopeAccountId ? 'Todas as contas'
      : `${overviewData.scopeKind === 'primary' ? 'Conta principal · ' : ''}${overviewData.accounts.items.find(a => a.accountId === overviewData.scopeAccountId)?.name ?? 'Conta'}`
    : planData ? scopeLabel(planData.scope) : ''
  const openAgenda = (month?: string) => onNavigate('calendar', { month: month ?? competence })
  const openCommitment = (item: OverviewCommitment) => openAgenda(item.date.slice(0, 7))

  return <div className="ov" aria-live="polite">
    <header className="ov-header">
      <div><p className="ov-eyebrow">{monthTitle(competence)}{planData ? ` · ${periodLabels[planData.periodKind]}` : ''}</p>
        <p className="ov-scope">{scopeText || 'Carregando escopo…'}</p></div>
      <div className="ov-filters">
        <label>Competência<input type="month" aria-label="Competência" required value={competence} onChange={event => { if (/^\d{4}-\d{2}$/.test(event.target.value)) onSelectionChange({ competence: event.target.value, scope }) }} /></label>
        <label>Conta
          <select aria-label="Conta ou consolidado" value={scope.kind === 'account' ? `account:${scope.accountId}` : scope.kind} onChange={event => {
            const next = event.target.value
            onSelectionChange({ competence, scope: next.startsWith('account:') ? { kind: 'account', accountId: next.slice('account:'.length) } : { kind: next as 'primary' | 'all' } })
          }}>
            <option value="primary">{choices?.primaryAccountName ? `Conta principal · ${choices.primaryAccountName}` : 'Conta principal'}</option>
            <option value="all">Todas as contas (consolidado)</option>
            {(choices?.accounts ?? []).map(account => <option key={account.accountId} value={`account:${account.accountId}`}>{account.name} · {account.scope}{account.isActive ? '' : ' (inativa)'}</option>)}
          </select></label>
        <button type="button" className="button--primary" onClick={() => onNavigate('transactions', { newTransaction: true })}>Novo lançamento</button>
      </div>
    </header>

    <div className="ov-top">
      {plan.load.state === 'loading' && !planData && <OperationalLoading>Carregando indicadores…</OperationalLoading>}
      {plan.load.state === 'error' && <OperationalError error={plan.load.error} onRetry={plan.retry} retryLabel="Recarregar indicadores" />}
      {(planData ?? (plan.load.state === 'loading' ? plan.previous : null)) && <Indicators data={(planData ?? plan.previous) as PlanActualDashboard} stale={!planData}
        workspaceId={workspaceId} classification={overviewData?.classification ?? null} onNavigate={(target, context) => onNavigate(target, context)} onSynced={reloadAll} />}
    </div>

    <div className="ov-layout">
      <div className="ov-main">
        <div className="ov-slot ov-slot--evolution">
        {evolutionData ? <div className={evolution.load.state === 'loading' ? 'ov-stale' : undefined}><OverviewEvolution plan={evolutionData} /></div>
          : evolution.load.state === 'error' ? <section className="ov-card"><h3>Evolução financeira</h3><OperationalError error={evolution.load.error} onRetry={evolution.retry} retryLabel="Recarregar a evolução" /></section>
            : <section className="ov-card"><OperationalLoading>Carregando a evolução…</OperationalLoading></section>}
        {evolutionData && evolution.load.state === 'error' && <OperationalError error={evolution.load.error} onRetry={evolution.retry} retryLabel="Recarregar a evolução" />}
        </div>

        {overview.load.state === 'loading' && !overviewData && <section className="ov-card"><OperationalLoading>Carregando orçamento, contas e compromissos…</OperationalLoading></section>}
        {overview.load.state === 'error' && <section className="ov-card"><h3>Orçamento, contas e compromissos</h3><OperationalError error={overview.load.error} onRetry={overview.retry} retryLabel="Recarregar" /></section>}
        {overviewData && <div className="ov-slot ov-slot--budget"><OverviewBudget competence={competence} budget={overviewData.budget} onOpenBudget={group => onNavigate('budgets', { competence, budgetGroup: group })} /></div>}
        {overviewData && <div className="ov-slot ov-slot--recent"><OverviewRecentBlock recent={overviewData.recent} competence={competence} scopeText={scopeText}
          onOpenAll={() => onNavigate('transactions', { transactions: { from: overviewData.recent.from, to: overviewData.recent.through, accountId: overviewData.scopeAccountId }, newTransaction: overviewData.recent.items.length === 0 })} /></div>}
      </div>
      <div className="ov-aside">
        {overviewData && <div className="ov-slot ov-slot--commitments"><OverviewCommitmentsBlock commitments={overviewData.commitments} onOpenItem={openCommitment} onOpenAgenda={() => openAgenda()} /></div>}
        {overviewData && <div className="ov-slot ov-slot--accounts"><OverviewAccountsBlock accounts={overviewData.accounts} scopeKind={overviewData.scopeKind} onOpen={() => onNavigate('accounts')} /></div>}
        {overviewData && <div className="ov-slot ov-slot--goals"><OverviewGoalsBlock goals={overviewData.goals} onOpen={() => onNavigate('goals')} /></div>}
      </div>
    </div>
  </div>
}

function Indicators({ data, stale, workspaceId, classification, onNavigate, onSynced }: {
  data: PlanActualDashboard; stale: boolean; workspaceId: string; classification: import('../../api/overview').OverviewClassification | null
  onNavigate: (target: 'accounts' | 'transactions' | 'automation', context?: OverviewNavigation) => void; onSynced: () => void
}) {
  return <div className={stale ? 'ov-stale' : undefined} aria-busy={stale || undefined}>
    <OverviewIndicators data={data} />
    <OverviewNotices workspaceId={workspaceId} data={data} classification={classification} onNavigate={onNavigate} onSynced={onSynced} />
  </div>
}
