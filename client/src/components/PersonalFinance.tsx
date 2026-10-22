import { useEffect, useState, type ReactNode } from 'react'
import { AppShell } from './AppShell'
import { DashboardFilters } from './DashboardFilters'
import { todayInSaoPaulo } from '../dates'
import { ensurePersonal, type PersonalWorkspace } from '../api/workspaces'
import { Accounts } from './Accounts'
import { Categories } from './Categories'
import { Transactions } from './Transactions'
import { Transfers } from './Transfers'
import { Planned } from './Planned'
import { FinancialDashboard } from './FinancialDashboard'
import { FinancialCalendarPage } from './FinancialCalendar'
import { FinancialBudgetsPage } from './FinancialBudgets'
import { FinancialCreditCardsPage } from './FinancialCreditCards'
import { FinancialDocumentInbox } from './FinancialDocumentInbox'
import { FinancialRecurrences } from './FinancialRecurrences'
import { FinancialPlanning } from './FinancialPlanning'
import { FinancialDebts } from './FinancialDebts'
import { FinancialGoals } from './FinancialGoals'
import { WorkspaceSettings } from './WorkspaceSettings'
import { retryResetCleanup, type ResetResult } from '../api/financialReset'

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
    {resetResult.pendingFileCleanup > 0 && <><p>Remoção física de arquivos pendente: {resetResult.pendingFileCleanup}. Os dados financeiros já foram removidos.</p><button type="button" onClick={() => { void retryResetCleanup(workspace.id).then(value => setResetResult(old => old ? { ...old, ...value } : old)).catch(() => setError('Não foi possível repetir o cleanup. A intenção permanece registrada.')) }}>Repetir cleanup de arquivos</button></>}{error && <p role="alert">{error}</p>}</div>}
    <WorkspaceArea key={`${workspace.id}:${generation}`} workspace={workspace} userArea={userArea} onReset={value => { setResetResult(value); setGeneration(old => old + 1) }} /></>
  return <div className="bootstrap-state"><div className="bootstrap-state__user">{userArea}</div><section className="card">{error ? <><p role="alert">{error}</p><button type="button" onClick={() => { setError(null); setRetry(value => value + 1) }}>Tentar novamente</button></> : <p role="status">Abrindo seu orçamento pessoal…</p>}</section></div>
}

function WorkspaceArea({ workspace, onReset, userArea }: { workspace: PersonalWorkspace; onReset: (result: ResetResult) => void; userArea?: ReactNode }) {
  const [section, setSection] = useState<string>('overview')
  const [version, setVersion] = useState(0)
  const [month, setMonth] = useState<string | undefined>()
  const [competence, setCompetence] = useState<string | undefined>()
  const [dashboardCompetence, setDashboardCompetence] = useState(() => todayInSaoPaulo().slice(0, 7))
  const [horizon, setHorizon] = useState(6)
  const filters = { competence: dashboardCompetence, months: horizon, onCompetenceChange: setDashboardCompetence, onMonthsChange: setHorizon }
  const changed = () => setVersion(value => value + 1)
  const agenda = (value: string) => { setMonth(`${value}-01`); setSection('calendar') }
  const budgets = (value: string) => { setCompetence(value); setSection('budgets') }
  const workspaceId = workspace.id
  return <AppShell section={section} onNavigate={setSection} workspaceName={workspace.name} userArea={userArea} subtitle={section === 'overview' ? 'Seu mês em perspectiva, com os dados do seu orçamento.' : 'Seu orçamento pessoal, sem compartilhar dados com a família.'} controls={section === 'overview' ? <DashboardFilters {...filters} /> : undefined}>
    {section === 'settings' && <WorkspaceSettings workspaceId={workspaceId} onReset={onReset} />}
    {section === 'overview' && <FinancialDashboard workspaceId={workspaceId} refreshKey={version} onNavigate={setSection} filters={filters} embedded />}
    {section === 'calendar' && <FinancialCalendarPage key={month} workspaceId={workspaceId} initialMonth={month} onChanged={changed} onNavigateToBudgets={budgets} />}
    {section === 'budgets' && <FinancialBudgetsPage key={competence} workspaceId={workspaceId} initialCompetence={competence} refreshKey={version} onNavigateToAgenda={agenda} />}
    {section === 'cards' && <FinancialCreditCardsPage workspaceId={workspaceId} refreshKey={version} onChanged={changed} />}
    {section === 'documents' && <FinancialDocumentInbox workspaceId={workspaceId} onChanged={changed} />}
    {section === 'recurrences' && <FinancialRecurrences workspaceId={workspaceId} refreshKey={version} onChanged={changed} onNavigateToAgenda={agenda} />}
    {section === 'planning' && <FinancialPlanning workspaceId={workspaceId} refreshKey={version} />}
    {section === 'debts' && <FinancialDebts workspaceId={workspaceId} refreshKey={version} onChanged={changed} onNavigateToAgenda={agenda} onNavigateToBudgets={budgets} onNavigateToPlanning={() => setSection('planning')} />}
    {section === 'goals' && <FinancialGoals workspaceId={workspaceId} refreshKey={version} onChanged={changed} onNavigateToPlanning={() => setSection('planning')} />}
    {section === 'accounts' && <Accounts workspaceId={workspaceId} members={[{ userId: workspace.ownerUserId, displayName: 'Você', isCurrentUser: true }]} balanceVersion={version} />}
    {section === 'categories' && <Categories workspaceId={workspaceId} />}
    {section === 'transactions' && <Transactions workspaceId={workspaceId} onChanged={changed} refreshKey={version} />}
    {section === 'transfers' && <Transfers workspaceId={workspaceId} onChanged={changed} />}
    {section === 'planned' && <Planned workspaceId={workspaceId} onChanged={changed} refreshKey={version} />}
  </AppShell>
}
