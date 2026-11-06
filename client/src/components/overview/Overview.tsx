import { useCallback, useMemo } from 'react'
import { getPlanActual, type PlanActualDashboard, type PlanActualScopeChoice } from '../../api/planActual'
import { getDashboardOverview } from '../../api/overview'
import type { BudgetGroup } from '../budget/budgetText'
import type { ExpenseNature } from '../../api/expenseNature'
import { addCalendarMonths } from '../../dates'
import { OverviewHero } from './OverviewHero'
import { OverviewAlerts } from './OverviewAlerts'
import { OverviewEvolutionSection } from './OverviewEvolution'
import { OverviewExpenseDistribution } from './OverviewExpenseDistribution'
import { OverviewBudgetProgress } from './OverviewBudgetProgress'
import { OverviewAccounts, OverviewGoals, OverviewRecent, OverviewUpcoming } from './OverviewLists'
import { OverviewSyncLine } from './OverviewSync'
import { buildPriorityItems } from './priorityItems'
import { monthTitle, periodLabels, scopeLabel } from './overviewText'
import { useLoad } from './useLoad'
import '../../Overview.css'

export type OverviewSection = 'accounts' | 'cards' | 'goals' | 'budgets' | 'calendar' | 'transactions' | 'transfers' | 'automation' | 'planning'
/** Contexto levado ao detalhe: o mês da Agenda, a competência do orçamento ou o recorte da lista de lançamentos. */
export interface OverviewNavigation { planningTab?: 'projection'; month?: string; competence?: string; budgetGroup?: BudgetGroup; transactions?: { from: string; to: string; accountId: string | null; expenseNature?: ExpenseNature; type?: 'Transfer' }; newTransaction?: boolean; goalId?: string; automationTab?: 'connections' | 'automations' | 'history'; review?: { kind: 'nature' | 'category'; from?: string; to?: string } }

export interface OverviewSelection { competence: string; scope: PlanActualScopeChoice }

export type Go = (section: OverviewSection, context?: OverviewNavigation) => void
/** Estado de uma consulta para um bloco: dado pronto, carregando ou falha com nova tentativa. Nenhum bloco mostra dado de outra competência ou conta. */
export interface BlockLoad<T> { data: T | null; loading: boolean; error: unknown; retry: () => void }

/**
 * Visão Geral no padrão do dashboard Folio: cabeçalho com competência e conta; o saldo bancário como informação principal, com o cheque especial e a recuperação do mês
 * compactos e o resumo de receitas, despesas e fechamento; a evolução financeira; distribuição das despesas e orçamento lado a lado; últimos lançamentos; e, na coluna de
 * apoio, contas, próximos compromissos e metas. Consultas: `plan-actual`, `financial-dashboard/overview` e `monthly-plan/six-months`. Todo valor vem do servidor.
 */
export function Overview({ workspaceId, refreshKey = 0, selection, onSelectionChange, onNavigate }: {
  workspaceId: string; refreshKey?: number; selection: OverviewSelection; onSelectionChange: (selection: OverviewSelection) => void; onNavigate: Go
}) {
  const { competence, scope } = selection
  const key = `${workspaceId}|${competence}|${scope.kind}:${scope.accountId ?? ''}|${refreshKey}`

  const plan = useLoad(key, useCallback((signal: AbortSignal) => getPlanActual(workspaceId, competence, scope, signal), [workspaceId, competence, scope]))
  const overview = useLoad(key, useCallback((signal: AbortSignal) => getDashboardOverview(workspaceId, competence, scope, signal), [workspaceId, competence, scope]))

  const planData = plan.load.state === 'loaded' ? plan.load.data : null
  const data = overview.load.state === 'loaded' ? overview.load.data : null
  const planBlock: BlockLoad<PlanActualDashboard> = { data: planData, loading: plan.load.state === 'loading', error: plan.load.state === 'error' ? plan.load.error : null, retry: plan.retry }
  const overviewBlock = { loading: overview.load.state === 'loading', error: overview.load.state === 'error' ? overview.load.error : null, retry: overview.retry }
  // A API é a fonte da verdade do escopo: guarda-se a última lista de contas para o seletor não esvaziar durante a recarga.
  const choices = planData?.scope ?? plan.previous?.scope ?? null
  const reloadAll = () => { plan.retry(); overview.retry() }

  const scopeText = data
    ? data.scopeKind === 'all' || !data.scopeAccountId ? 'Todas as contas'
      : `${data.scopeKind === 'primary' ? 'Conta principal · ' : ''}${data.accounts.items.find(a => a.accountId === data.scopeAccountId)?.name ?? 'Conta'}`
    : planData ? scopeLabel(planData.scope) : ''
  const items = useMemo(() => buildPriorityItems({ competence, decision: data?.decision ?? null, control: data?.budgetControl ?? null, plan: planData, classification: data?.classification ?? null }),
    [competence, data, planData])
  const setCompetence = (value: string) => { if (/^\d{4}-\d{2}$/.test(value)) onSelectionChange({ competence: value, scope }) }
  const step = (months: number) => setCompetence(addCalendarMonths(`${competence}-01`, months).slice(0, 7))
  const recentRange = data ? { from: data.recent.from, to: data.recent.through, accountId: data.scopeAccountId } : null

  return <div className="ov" aria-live="polite">
    <header className="ov-header">
      <div className="ov-header__title">
        <h2>{monthTitle(competence)}{planData && <span className="ov-period">{periodLabels[planData.periodKind]}</span>}</h2>
        <p className="ov-meta">{scopeText || 'Carregando conta…'}</p>
      </div>
      <div className="ov-filters">
        <div className="ov-month" role="group" aria-label="Competência analisada">
          <button type="button" className="ov-icon-button" aria-label="Mês anterior" onClick={() => step(-1)}>‹</button>
          <input type="month" aria-label="Competência" required value={competence} onChange={event => setCompetence(event.target.value)} />
          <button type="button" className="ov-icon-button" aria-label="Próximo mês" onClick={() => step(1)}>›</button>
        </div>
        <select aria-label="Conta ou consolidado" value={scope.kind === 'account' ? `account:${scope.accountId}` : scope.kind} onChange={event => {
          const next = event.target.value
          onSelectionChange({ competence, scope: next.startsWith('account:') ? { kind: 'account', accountId: next.slice('account:'.length) } : { kind: next as 'primary' | 'all' } })
        }}>
          <option value="primary">{choices?.primaryAccountName ? `Conta principal · ${choices.primaryAccountName}` : 'Conta principal'}</option>
          <option value="all">Todas as contas (consolidado)</option>
          {(choices?.accounts ?? []).map(account => <option key={account.accountId} value={`account:${account.accountId}`}>{account.name} · {account.scope}{account.isActive ? '' : ' (inativa)'}</option>)}
        </select>
        <button type="button" className="button--primary ov-new" onClick={() => onNavigate('transactions', { newTransaction: true })}>+ Novo lançamento</button>
      </div>
    </header>

    <OverviewHero competence={competence} plan={planBlock} decision={data?.decision ?? null} overview={overviewBlock} onNavigate={onNavigate}>
      {planData && <OverviewSyncLine workspaceId={workspaceId} sync={planData.sync} onSynced={reloadAll} />}
    </OverviewHero>

    <OverviewAlerts items={items} onNavigate={onNavigate} onReload={reloadAll} />

    <div className="ov-grid">
      <div className="ov-grid__main">
        <OverviewEvolutionSection workspaceId={workspaceId} competence={competence} scope={scope} refreshKey={refreshKey} />
        <div className="ov-pair">
          <OverviewExpenseDistribution competence={competence} distribution={data?.expenseDistribution ?? null} {...overviewBlock}
            onOpen={recentRange ? () => onNavigate('transactions', { transactions: recentRange }) : undefined} />
          <OverviewBudgetProgress control={data?.budgetControl ?? null} {...overviewBlock}
            onOpenBudget={() => onNavigate('budgets', { competence })} onShowAll={() => onSelectionChange({ competence, scope: { kind: 'all' } })} />
        </div>
        <OverviewRecent recent={data?.recent ?? null} {...overviewBlock}
          onOpenAll={() => recentRange && onNavigate('transactions', { transactions: recentRange, newTransaction: data?.recent.items.length === 0 })} />
      </div>
      <aside className="ov-grid__side" aria-label="Contas, compromissos e metas">
        <OverviewAccounts accounts={data?.accounts ?? null} {...overviewBlock} onOpen={() => onNavigate('accounts')} />
        <OverviewUpcoming commitments={data?.commitments ?? null} {...overviewBlock}
          onOpenAgenda={month => onNavigate('calendar', { month: month ?? competence })} onOpenCards={() => onNavigate('cards')} />
        {data && data.goals.state === 'Complete' && data.goals.activeCount > 0
          && <OverviewGoals goals={data.goals} onOpen={() => onNavigate('goals')} onOpenGoal={goalId => onNavigate('goals', { goalId })} />}
      </aside>
    </div>
  </div>
}
