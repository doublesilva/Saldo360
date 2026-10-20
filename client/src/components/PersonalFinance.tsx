import { useEffect, useState } from 'react'
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
import { Families } from './Families'

const sections = [['overview','Visão Geral'],['calendar','Agenda'],['budgets','Orçamentos'],['cards','Cartões'],['documents','Documentos'],['recurrences','Recorrências'],['planning','Planejamento'],['debts','Dívidas'],['goals','Objetivos'],['accounts','Contas'],['categories','Categorias'],['transactions','Lançamentos'],['transfers','Transferências'],['planned','Previsões'],['social','Família (social)']] as const

export function PersonalFinance() {
  const [workspace, setWorkspace] = useState<PersonalWorkspace | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [retry, setRetry] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    ensurePersonal(controller.signal).then(result => { if (!controller.signal.aborted) setWorkspace(result) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Falha ao abrir orçamento.') })
    return () => controller.abort()
  }, [retry])
  if (workspace) return <WorkspaceArea key={workspace.id} workspace={workspace} />
  return <section className="card">{error ? <><p role="alert">{error}</p><button type="button" onClick={() => { setError(null); setRetry(value => value + 1) }}>Tentar novamente</button></> : <p role="status">Abrindo seu orçamento pessoal…</p>}</section>
}

function WorkspaceArea({ workspace }: { workspace: PersonalWorkspace }) {
  const [section, setSection] = useState<string>('overview')
  const [version, setVersion] = useState(0)
  const [month, setMonth] = useState<string | undefined>()
  const [competence, setCompetence] = useState<string | undefined>()
  const changed = () => setVersion(value => value + 1)
  const agenda = (value: string) => { setMonth(`${value}-01`); setSection('calendar') }
  const budgets = (value: string) => { setCompetence(value); setSection('budgets') }
  const workspaceId = workspace.id
  return <section className="card"><h2>{workspace.name}</h2><nav className="family-navigation" aria-label="Navegação do orçamento pessoal">{sections.map(([id,label]) => <button key={id} type="button" onClick={() => setSection(id)} aria-current={section === id ? 'page' : undefined}>{label}</button>)}</nav>
    {section === 'overview' && <FinancialDashboard workspaceId={workspaceId} refreshKey={version} onNavigate={setSection} />}
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
    {section === 'social' && <><p className="notice">Vínculos familiares não compartilham seu orçamento pessoal.</p><Families /></>}
  </section>
}
