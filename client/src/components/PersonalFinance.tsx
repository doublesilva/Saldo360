import { useEffect, useState, type ReactNode } from 'react'
import { AppShell } from './AppShell'
import { todayInSaoPaulo } from '../dates'
import { ensurePersonal, type PersonalWorkspace } from '../api/workspaces'
import { Accounts } from './Accounts'
import { Categories } from './Categories'
import { Transactions } from './Transactions'
import { Transfers } from './Transfers'
import { Planned } from './Planned'
import type { BudgetGroup } from './budget/budgetText'
import { Overview, type OverviewNavigation, type OverviewSection, type OverviewSelection } from './overview/Overview'
import { FinancialCalendarPage } from './FinancialCalendar'
import { FinancialBudgetsPage } from './FinancialBudgets'
import { FinancialCreditCardsPage } from './FinancialCreditCards'
import { FinancialRecurrences } from './FinancialRecurrences'
import { PlanningHub } from './PlanningHub'
import { FinancialDebts } from './FinancialDebts'
import { FinancialGoals } from './FinancialGoals'
import { WorkspaceSettings } from './WorkspaceSettings'
import { AutomationCenter, type AutomationEntry } from './AutomationCenter'
import { isPluggyLabAvailable } from '../api/pluggyPoc'
import type { ResetResult } from '../api/financialReset'

export function PersonalFinance({ userArea }: { userArea?: ReactNode }) {
  const [workspace, setWorkspace] = useState<PersonalWorkspace | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [retry, setRetry] = useState(0)
  const [resetResult, setResetResult] = useState<ResetResult | null>(null)
  const [generation, setGeneration] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    ensurePersonal(controller.signal).then(result => { if (!controller.signal.aborted) setWorkspace(result) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Falha ao abrir orçamento.') })
    return () => controller.abort()
  }, [retry])
  if (workspace) return <>{resetResult && <div role="status" className="workspace-reset-result"><p>Orçamento reiniciado. Todos os dados financeiros deste orçamento foram removidos. Sua conta continua ativa.</p>
    {error && <p role="alert">{error}</p>}</div>}
    <WorkspaceArea key={`${workspace.id}:${generation}`} workspace={workspace} userArea={userArea} onReset={value => { setResetResult(value); setGeneration(old => old + 1) }} /></>
  return <div className="bootstrap-state"><div className="bootstrap-state__user">{userArea}</div><section className="card">{error ? <><p role="alert">{error}</p><button type="button" onClick={() => { setError(null); setRetry(value => value + 1) }}>Tentar novamente</button></> : <p role="status">Abrindo seu orçamento pessoal…</p>}</section></div>
}

function WorkspaceArea({ workspace, onReset, userArea }: { workspace: PersonalWorkspace; onReset: (result: ResetResult) => void; userArea?: ReactNode }) {
  const [section, setSection] = useState<string>('overview')
  const [version, setVersion] = useState(0)
  const [month, setMonth] = useState<string | undefined>()
  const [competence, setCompetence] = useState<string | undefined>()
  const [budgetGroup, setBudgetGroup] = useState<BudgetGroup | undefined>()
  // Competência e conta da Visão Geral ficam aqui para sobreviver à ida a um detalhe e à volta.
  const [overviewSelection, setOverviewSelection] = useState<OverviewSelection>(() => ({ competence: todayInSaoPaulo().slice(0, 7), scope: { kind: 'primary' } }))
  const [transactionContext, setTransactionContext] = useState<{ key: number; filters?: OverviewNavigation['transactions']; newTransaction?: boolean } | null>(null)
  const changed = () => setVersion(value => value + 1)
  const agenda = (value: string) => { setMonth(`${value}-01`); setSection('calendar') }
  const budgets = (value: string) => { setCompetence(value); setBudgetGroup(undefined); setSection('budgets') }
  // Orçamento → central de automação: abre a lista de revisão com as despesas do período sem tipo definido, inclusive as compras de cartão importadas.
  const [automationEntry, setAutomationEntry] = useState<AutomationEntry | undefined>()
  const reviewClassification = (range: { from: string; to: string }) => { setAutomationEntry({ key: Date.now(), tab: 'automations', review: { kind: 'nature', ...range } }); setSection('automation') }
  const workspaceId = workspace.id
  // Resumo → detalhe: leva a conta, a competência ou a data da Visão Geral para a página de destino.
  const openFromOverview = (target: OverviewSection, context?: OverviewNavigation) => {
    setMonth(target === 'calendar' && context?.month ? `${context.month}-01` : undefined)
    setCompetence(target === 'budgets' ? context?.competence : undefined)
    setBudgetGroup(target === 'budgets' ? context?.budgetGroup : undefined)
    setTransactionContext(target === 'transactions' ? { key: Date.now(), filters: context?.transactions, newTransaction: context?.newTransaction } : null)
    if (target === 'automation') setAutomationEntry({ key: Date.now(), tab: context?.automationTab, review: context?.review })
    setSection(target)
  }
  // Central de automação: só aparece quando a API habilita a Pluggy para este usuário (Pluggy:Enabled).
  const [pluggyLab, setPluggyLab] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    isPluggyLabAvailable(workspaceId, controller.signal).then(available => { if (!controller.signal.aborted) setPluggyLab(available) }).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId])
  return <AppShell section={section} onNavigate={setSection} automation={pluggyLab} workspaceName={workspace.name} userArea={userArea} subtitle={section === 'overview' ? 'Sua situação, o mês e os próximos compromissos.' : 'Seu orçamento pessoal, sem compartilhar dados com a família.'}>
    {section === 'automation' && pluggyLab && <AutomationCenter key={automationEntry?.key} workspaceId={workspaceId} refreshKey={String(version)} onChanged={changed} entry={automationEntry} />}
    {section === 'settings' && <WorkspaceSettings workspaceId={workspaceId} onReset={onReset} />}
    {section === 'overview' && <Overview workspaceId={workspaceId} refreshKey={version} selection={overviewSelection} onSelectionChange={setOverviewSelection} onNavigate={openFromOverview} />}
    {section === 'calendar' && <FinancialCalendarPage key={month} workspaceId={workspaceId} initialMonth={month} onChanged={changed} onNavigateToBudgets={budgets} />}
    {section === 'budgets' && <FinancialBudgetsPage key={`${competence}:${budgetGroup}`} workspaceId={workspaceId} initialCompetence={competence} initialGroup={budgetGroup} refreshKey={version} onNavigateToAgenda={agenda} onReviewClassification={reviewClassification} />}
    {section === 'cards' && <FinancialCreditCardsPage workspaceId={workspaceId} refreshKey={version} onChanged={changed} />}
    {section === 'recurrences' && <FinancialRecurrences workspaceId={workspaceId} refreshKey={version} onChanged={changed} onNavigateToAgenda={agenda} />}
    {section === 'planning' && <PlanningHub workspaceId={workspaceId} refreshKey={version} onChanged={changed} />}
    {section === 'debts' && <FinancialDebts workspaceId={workspaceId} refreshKey={version} onChanged={changed} />}
    {section === 'goals' && <FinancialGoals workspaceId={workspaceId} refreshKey={version} onChanged={changed} onNavigateToPlanning={() => setSection('planning')} />}
    {section === 'accounts' && <Accounts workspaceId={workspaceId} members={[{ userId: workspace.ownerUserId, displayName: 'Você', isCurrentUser: true }]} balanceVersion={version} />}
    {section === 'categories' && <Categories workspaceId={workspaceId} />}
    {section === 'transactions' && <Transactions key={transactionContext?.key} workspaceId={workspaceId} onChanged={changed} refreshKey={version} initialFilters={transactionContext?.filters ? { from: transactionContext.filters.from, to: transactionContext.filters.to, accountId: transactionContext.filters.accountId ?? '', ...(transactionContext.filters.expenseNature ? { expenseNature: transactionContext.filters.expenseNature, type: 'Expense' as const } : {}) } : undefined} startWithForm={transactionContext?.newTransaction} />}
    {section === 'transfers' && <Transfers workspaceId={workspaceId} onChanged={changed} />}
    {section === 'planned' && <Planned workspaceId={workspaceId} onChanged={changed} refreshKey={version} />}
  </AppShell>
}
