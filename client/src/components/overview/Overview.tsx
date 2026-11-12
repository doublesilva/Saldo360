import { useCallback, useMemo } from 'react'
import { getPlanActual, type PlanActualDashboard, type PlanActualScopeChoice } from '../../api/planActual'
import { getDashboardOverview } from '../../api/overview'
import type { BudgetGroup } from '../budget/budgetText'
import type { ExpenseNature } from '../../api/expenseNature'
import { addCalendarMonths } from '../../dates'
import { OverviewResult, OverviewComposition } from './OverviewResult'
import { OverviewBalanceCard } from './OverviewBalanceCard'
import { OverviewSummary } from './OverviewSummary'
import { OverviewAlerts } from './OverviewAlerts'
import { OverviewEvolutionSection } from './OverviewEvolution'
import { OverviewBudgetProgress } from './OverviewBudgetProgress'
import { OverviewAccounts, OverviewGoals, OverviewRecent, OverviewUpcoming } from './OverviewLists'
import { OverviewSyncLine } from './OverviewSync'
import { buildPriorityItems } from './priorityItems'
import { monthTitle, periodLabels, scopeLabel } from './overviewText'
import { useLoad } from './useLoad'
import '../../Overview.css'

export type OverviewSection = 'accounts' | 'cards' | 'goals' | 'budgets' | 'calendar' | 'transactions' | 'transfers' | 'automation' | 'planning'
/** Contexto levado ao detalhe: o mês da Agenda, a competência do orçamento ou o recorte da lista de lançamentos. */
export interface OverviewNavigation { planningTab?: 'projection' | 'six'; recoveryScope?: PlanActualScopeChoice; month?: string; competence?: string; budgetGroup?: BudgetGroup; transactions?: { from: string; to: string; accountId: string | null; expenseNature?: ExpenseNature; type?: 'Transfer' }; newTransaction?: boolean; goalId?: string; automationTab?: 'connections' | 'automations' | 'history'; connectionIds?: string[]; review?: { kind: 'nature' | 'category'; from?: string; to?: string } }

export interface OverviewSelection { competence: string; scope: PlanActualScopeChoice }

export type Go = (section: OverviewSection, context?: OverviewNavigation) => void
/** Estado de uma consulta para um bloco: dado pronto, carregando ou falha com nova tentativa. Nenhum bloco mostra dado de outra competência ou conta. */
export interface BlockLoad<T> { data: T | null; loading: boolean; error: unknown; retry: () => void }

/**
 * Visão Geral (T14.3): cabeçalho com competência e conta; A) resultado do mês, B) composição do fluxo de caixa e C) saldo e cheque especial; D) orçamento mensal; E) próximas ações;
 * F) evolução (resultado orçamentário) e últimos lançamentos; e, na coluna de apoio, contas, próximos compromissos e metas. Cada seção carrega e falha de forma independente. Consultas: `plan-actual`, `financial-dashboard/overview` e `monthly-plan/six-months` (e, ao pedir "Ver todas", a distribuição
 * completa das despesas). Todo valor vem do servidor.
 */
export function Overview({ workspaceId, refreshKey = 0, selection, onSelectionChange, onNavigate, notice }: {
  notice?: string | null
 
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
  const items = useMemo(() => buildPriorityItems({ competence, decision: data?.decision ?? null, control: data?.budgetControl ?? null, pfControl: data?.pfBudgetControl ?? null, plan: planData, classification: data?.classification ?? null }),
    [competence, data, planData])
  const setCompetence = (value: string) => { if (/^\d{4}-\d{2}$/.test(value)) onSelectionChange({ competence: value, scope }) }
  const step = (months: number) => setCompetence(addCalendarMonths(`${competence}-01`, months).slice(0, 7))
  const recentRange = data ? { from: data.recent.from, to: data.recent.through, accountId: data.scopeAccountId } : null

  return <div className="ov" aria-live="polite">
    <header className="ov-header">
      <div className="ov-header__title">
        <h2>{monthTitle(competence)}{planData && <span className="ov-period">{periodLabels[planData.periodKind]}</span>}</h2>
        <p className="ov-meta">{scopeText || 'Carregando conta…'}</p>{notice && <p className="ov-meta" role="status">{notice}</p>}
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

    <OverviewAlerts items={items} state={{ loading: planBlock.loading || overviewBlock.loading, failed: Boolean(planBlock.error || overviewBlock.error) }} onNavigate={onNavigate} onReload={reloadAll} />
    <OverviewSummary competence={competence} result={data?.monthlyResult ?? null} decision={data?.decision ?? null} plan={planData} control={data?.pfBudgetControl ?? null} block={overviewBlock} onNavigate={onNavigate} />

    <details className="ov-details ov-more">
      <summary>Resultado de caixa, composição e cheque especial</summary>
    <div className="ov-top">
      <div className="ov-top__result"><OverviewResult competence={competence} result={data?.monthlyResult ?? null} decision={data?.decision ?? null} plan={planData} block={overviewBlock}>
        {planData && <OverviewSyncLine workspaceId={workspaceId} sync={planData.sync} onSynced={reloadAll} />}
      </OverviewResult></div>
      <div className="ov-top__composition"><OverviewComposition competence={competence} result={data?.monthlyResult ?? null} scopeAccountId={data?.scopeAccountId ?? null}
        consolidated={(data?.scopeKind ?? scope.kind) === 'all'} block={overviewBlock} onNavigate={onNavigate} /></div>
      <div className="ov-top__balance"><OverviewBalanceCard plan={planBlock} decision={data?.decision ?? null} limit={data?.monthlyResult?.overdraft ?? null} overview={overviewBlock} onNavigate={onNavigate} /></div>
    </div>
    </details>

    <div className="ov-grid">
      <div className="ov-grid__main">
        <div className="ov-budget-area">
          <OverviewBudgetProgress control={data?.pfBudgetControl ?? null} fixedTarget={data?.budget.state === 'Complete' ? data.budget.fixed?.planned ?? null : null}
            scopeLabel="Orçamento PF · Todas as contas PF" monthLabel={monthTitle(competence)} workspaceId={workspaceId} competence={competence} {...overviewBlock}
            onOpenBudget={() => onNavigate('budgets', { competence })} onOpenCards={() => onNavigate('cards')} />
        </div>
        <OverviewEvolutionSection workspaceId={workspaceId} competence={competence} scope={scope} refreshKey={refreshKey} />
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
