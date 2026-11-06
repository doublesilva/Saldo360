import { useCallback, useMemo, useState } from 'react'
import { getPlanActual, type PlanActualScopeChoice } from '../../api/planActual'
import { getDashboardOverview, type OverviewCommitment } from '../../api/overview'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { OverviewIncomeExpense } from './OverviewIndicators'
import { OverviewEvolutionSection } from './OverviewEvolution'
import { OverviewBudget } from './OverviewBudget'
import type { BudgetGroup } from '../budget/budgetText'
import type { ExpenseNature } from '../../api/expenseNature'
import { OverviewCommitmentsBlock, OverviewGoalsBlock, OverviewRecentBlock } from './OverviewAside'
import { OverviewRecovery } from './OverviewRecovery'
import { OverviewSituationSection } from './OverviewDecision'
import { OverviewBudgetSummarySection } from './OverviewBudgetControl'
import { OverviewRecoverySummary } from './OverviewRecoverySummary'
import { OverviewActionsSection } from './OverviewActions'
import { OverviewProjectedRecovery } from './OverviewProjectedRecovery'
import { OverviewCashFlowSection, OverviewCompetenceSection } from './OverviewCashFlow'
import { OverviewSyncLine } from './OverviewSync'
import { Disclosure, type DetailKey } from './Disclosure'
import { buildPriorityItems } from './priorityItems'
import { monthTitle, periodLabels, scopeLabel } from './overviewText'
import { useLoad } from './useLoad'
import '../../Overview.css'

export type OverviewSection = 'accounts' | 'cards' | 'goals' | 'budgets' | 'calendar' | 'transactions' | 'transfers' | 'automation' | 'planning'
/** Contexto levado ao detalhe: o mês da Agenda, a competência do orçamento ou o recorte da lista de lançamentos. */
export interface OverviewNavigation { planningTab?: 'projection'; month?: string; competence?: string; budgetGroup?: BudgetGroup; transactions?: { from: string; to: string; accountId: string | null; expenseNature?: ExpenseNature; type?: 'Transfer' }; newTransaction?: boolean; goalId?: string; automationTab?: 'connections' | 'automations' | 'history'; review?: { kind: 'nature' | 'category'; from?: string; to?: string } }

export interface OverviewSelection { competence: string; scope: PlanActualScopeChoice }

/**
 * Visão Geral (T12.2): um painel de decisões em quatro áreas, nesta ordem: situação financeira, orçamento, recuperação e onde agir. Os detalhes ficam em "Análises
 * detalhadas", expansíveis e só montadas quando abertas. Resumo: `plan-actual` e `financial-dashboard/overview`; a evolução (`monthly-plan/six-months`) e a projeção só
 * são consultadas quando o usuário abre o detalhe. Cada bloco carrega, falha e se recarrega sozinho, e nunca mostra dados de outra competência ou conta.
 */
export function Overview({ workspaceId, refreshKey = 0, selection, onSelectionChange, onNavigate }: {
  workspaceId: string; refreshKey?: number; selection: OverviewSelection; onSelectionChange: (selection: OverviewSelection) => void
  onNavigate: (section: OverviewSection, context?: OverviewNavigation) => void
}) {
  const { competence, scope } = selection
  const scopeKey = `${scope.kind}:${scope.accountId ?? ''}`
  const key = `${workspaceId}|${competence}|${scopeKey}|${refreshKey}`
  const [opened, setOpened] = useState<Partial<Record<DetailKey, boolean>>>({})

  const plan = useLoad(key, useCallback((signal: AbortSignal) => getPlanActual(workspaceId, competence, scope, signal), [workspaceId, competence, scope]))
  const overview = useLoad(key, useCallback((signal: AbortSignal) => getDashboardOverview(workspaceId, competence, scope, signal), [workspaceId, competence, scope]))

  const planData = plan.load.state === 'loaded' ? plan.load.data : null
  const overviewData = overview.load.state === 'loaded' ? overview.load.data : null
  // A API é a fonte da verdade do escopo: guarda-se a última lista de contas para o seletor não esvaziar durante a recarga.
  const choices = planData?.scope ?? plan.previous?.scope ?? null
  const reloadAll = () => { plan.retry(); overview.retry() }

  const scopeText = overviewData
    ? overviewData.scopeKind === 'all' || !overviewData.scopeAccountId ? 'Todas as contas'
      : `${overviewData.scopeKind === 'primary' ? 'Conta principal · ' : ''}${overviewData.accounts.items.find(a => a.accountId === overviewData.scopeAccountId)?.name ?? 'Conta'}`
    : planData ? scopeLabel(planData.scope) : ''
  const openAgenda = (month?: string) => onNavigate('calendar', { month: month ?? competence })
  const openCommitment = (item: OverviewCommitment) => openAgenda(item.date.slice(0, 7))
  const go = (section: OverviewSection, context?: OverviewNavigation) => onNavigate(section, context)

  const setOpen = (detail: DetailKey) => (open: boolean) => setOpened(current => current[detail] === open ? current : { ...current, [detail]: open })
  const openDetail = (detail: DetailKey) => {
    setOpened(current => ({ ...current, [detail]: true }))
    setTimeout(() => document.getElementById(`ov-d-${detail}`)?.scrollIntoView?.({ behavior: 'smooth', block: 'start' }), 0)
  }

  const items = useMemo(() => buildPriorityItems({ competence, decision: overviewData?.decision ?? null, control: overviewData?.budgetControl ?? null, plan: planData, classification: overviewData?.classification ?? null }),
    [competence, overviewData, planData])
  const planState = { data: planData, loading: plan.load.state === 'loading', failed: plan.load.state === 'error' }
  const cashFlow = overviewData?.cashFlow ?? null

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

    <OverviewSituationSection competence={competence} decision={overviewData?.decision ?? null} plan={planState} scopeText={scopeText}
      decisionStatus={overviewData ? (overviewData.decision ? 'ready' : 'unavailable') : overview.load.state === 'error' ? 'unavailable' : 'loading'}
      coverageOpen={opened.coverage === true} onCoverageOpenChange={setOpen('coverage')} onNavigate={go}>
      {plan.load.state === 'error' && <OperationalError error={plan.load.error} onRetry={plan.retry} retryLabel="Recarregar indicadores" />}
      {overview.load.state === 'error' && <OperationalError error={overview.load.error} onRetry={overview.retry} retryLabel="Recarregar a Visão Geral" />}
      {overview.load.state === 'loading' && !overviewData && <OperationalLoading>Carregando a situação financeira…</OperationalLoading>}
      {planData && <OverviewSyncLine workspaceId={workspaceId} sync={planData.sync} onSynced={reloadAll} />}
    </OverviewSituationSection>

    {overviewData?.budgetControl && <OverviewBudgetSummarySection control={overviewData.budgetControl} competence={competence} pressuresOpen={opened.pressures === true} onPressuresOpenChange={setOpen('pressures')} onNavigate={go} />}

    {overviewData?.decision && <OverviewRecoverySummary recovery={overviewData.decision.recovery} onOpenDetail={() => openDetail('recovery')} onNavigate={go} />}

    <OverviewActionsSection items={items} loading={!overviewData && overview.load.state === 'loading'} onNavigate={go} onReload={reloadAll} onOpenDetail={openDetail} />

    <section className="ov-details-area" aria-labelledby="ov-details-title">
      <header className="ov-area__head"><h2 id="ov-details-title">Análises detalhadas</h2>
        <p className="ov-meta">Abra apenas o que precisar. Alertas de segurança financeira continuam visíveis acima.</p></header>

      <Disclosure id="cashflow" title="Fluxo de caixa completo" hint="Entradas, saídas, saldo de abertura e fechamento" open={opened.cashflow === true} onOpenChange={setOpen('cashflow')}>
        {cashFlow ? <OverviewCashFlowSection flow={cashFlow} scopeAccountId={overviewData?.scopeAccountId ?? null} onNavigate={go} /> : <DetailState loading={overview.load.state === 'loading'} error={overview.load.state === 'error' ? overview.load.error : null} retry={overview.retry} what="o fluxo de caixa" />}
      </Disclosure>

      <Disclosure id="competence" title="Receitas e despesas por competência" hint="Totais do mês, sem transferências" open={opened.competence === true} onOpenChange={setOpen('competence')}>
        {planData ? <><OverviewIncomeExpense data={planData} byCompetence={cashFlow !== null} />{cashFlow && <OverviewCompetenceSection flow={cashFlow} />}</>
          : <DetailState loading={plan.load.state === 'loading'} error={plan.load.state === 'error' ? plan.load.error : null} retry={plan.retry} what="as receitas e despesas" />}
      </Disclosure>

      <Disclosure id="evolution" title="Evolução financeira" hint="Receitas e despesas dos últimos e próximos meses" open={opened.evolution === true} onOpenChange={setOpen('evolution')}>
        <OverviewEvolutionSection workspaceId={workspaceId} competence={competence} scope={scope} refreshKey={refreshKey} />
      </Disclosure>

      <Disclosure id="recovery" title="Recuperação detalhada" hint="Registrar a meta do mês, ver o histórico e a projeção do limite" open={opened.recovery === true} onOpenChange={setOpen('recovery')}>
        {planData && <OverviewRecovery key={`${planData.competence}:${planData.scope.accountId ?? planData.scope.kind}`} workspaceId={workspaceId} data={planData} onAccounts={() => onNavigate('accounts')} onProjection={() => onNavigate('planning', { planningTab: 'projection' })} />}
        <OverviewProjectedRecovery workspaceId={workspaceId} competence={competence} refreshKey={refreshKey} onOpen={() => onNavigate('planning', { planningTab: 'projection' })} />
      </Disclosure>

      <Disclosure id="budget" title="Metas por categoria e despesas fixas" hint="O orçamento completo tem a composição de cada meta" open={opened.budget === true} onOpenChange={setOpen('budget')}>
        {overviewData ? <OverviewBudget competence={competence} budget={overviewData.budget} onOpenBudget={group => onNavigate('budgets', { competence, budgetGroup: group as BudgetGroup | undefined })} />
          : <DetailState loading={overview.load.state === 'loading'} error={overview.load.state === 'error' ? overview.load.error : null} retry={overview.retry} what="as metas" />}
      </Disclosure>

      <Disclosure id="commitments" title="Próximos compromissos por vencimento" hint="Despesas pendentes e faturas de cartão" open={opened.commitments === true} onOpenChange={setOpen('commitments')}>
        {overviewData ? <OverviewCommitmentsBlock commitments={overviewData.commitments} onOpenItem={openCommitment} onOpenAgenda={() => openAgenda()} onOpenCards={() => onNavigate('cards')} />
          : <DetailState loading={overview.load.state === 'loading'} error={overview.load.state === 'error' ? overview.load.error : null} retry={overview.retry} what="os compromissos" />}
      </Disclosure>

      <Disclosure id="recent" title="Últimos lançamentos" hint="Lançamentos efetivos do mês" open={opened.recent === true} onOpenChange={setOpen('recent')}>
        {overviewData ? <OverviewRecentBlock recent={overviewData.recent} competence={competence} scopeText={scopeText}
          onOpenAll={() => onNavigate('transactions', { transactions: { from: overviewData.recent.from, to: overviewData.recent.through, accountId: overviewData.scopeAccountId }, newTransaction: overviewData.recent.items.length === 0 })} />
          : <DetailState loading={overview.load.state === 'loading'} error={overview.load.state === 'error' ? overview.load.error : null} retry={overview.retry} what="os lançamentos" />}
      </Disclosure>

      <Disclosure id="goals" title="Metas e objetivos" hint="Reservas e prazos" open={opened.goals === true} onOpenChange={setOpen('goals')}>
        {overviewData ? <OverviewGoalsBlock goals={overviewData.goals} onOpen={() => onNavigate('goals')} onOpenGoal={goalId => onNavigate('goals', { goalId })} />
          : <DetailState loading={overview.load.state === 'loading'} error={overview.load.state === 'error' ? overview.load.error : null} retry={overview.retry} what="as metas e objetivos" />}
      </Disclosure>
    </section>
  </div>
}

function DetailState({ loading, error, retry, what }: { loading: boolean; error: unknown; retry: () => void; what: string }) {
  return error ? <OperationalError error={error} onRetry={retry} retryLabel="Recarregar" /> : loading ? <OperationalLoading>{`Carregando ${what}…`}</OperationalLoading> : null
}
