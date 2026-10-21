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
import { FinancialReset } from './FinancialReset'
import { retryResetCleanup, type ResetResult } from '../api/financialReset'

const sections = [['overview','Visão Geral'],['calendar','Agenda'],['budgets','Orçamentos'],['cards','Cartões'],['documents','Documentos'],['recurrences','Recorrências'],['planning','Planejamento'],['debts','Dívidas'],['goals','Objetivos'],['accounts','Contas'],['categories','Categorias'],['transactions','Lançamentos'],['transfers','Transferências'],['planned','Previsões'],['social','Família (social)']] as const

export function PersonalFinance() {
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
  if (workspace) return <>{resetResult && <div role="status"><p>Orçamento reiniciado. Todos os dados financeiros deste orçamento foram removidos. Sua conta continua ativa.</p>
    {resetResult.pendingFileCleanup > 0 && <><p>Remoção física de arquivos pendente: {resetResult.pendingFileCleanup}. Os dados financeiros já foram removidos.</p><button type="button" onClick={() => { void retryResetCleanup(workspace.id).then(value => setResetResult(old => old ? { ...old, ...value } : old)).catch(() => setError('Não foi possível repetir o cleanup. A intenção permanece registrada.')) }}>Repetir cleanup de arquivos</button></>}{error && <p role="alert">{error}</p>}</div>}
    <WorkspaceArea key={`${workspace.id}:${generation}`} workspace={workspace} onReset={value => { setResetResult(value); setGeneration(old => old + 1) }} /></>
  return <section className="card">{error ? <><p role="alert">{error}</p><button type="button" onClick={() => { setError(null); setRetry(value => value + 1) }}>Tentar novamente</button></> : <p role="status">Abrindo seu orçamento pessoal…</p>}</section>
}

function WorkspaceArea({ workspace, onReset }: { workspace: PersonalWorkspace; onReset: (result: ResetResult) => void }) {
  const [section, setSection] = useState<string>('overview')
  const [version, setVersion] = useState(0)
  const [month, setMonth] = useState<string | undefined>()
  const [competence, setCompetence] = useState<string | undefined>()
  const changed = () => setVersion(value => value + 1)
  const agenda = (value: string) => { setMonth(`${value}-01`); setSection('calendar') }
  const budgets = (value: string) => { setCompetence(value); setSection('budgets') }
  const workspaceId = workspace.id
  return <section className="card"><h2>{workspace.name}</h2><nav className="family-navigation" aria-label="Navegação do orçamento pessoal">{sections.map(([id,label]) => <button key={id} type="button" onClick={() => setSection(id)} aria-current={section === id ? 'page' : undefined}>{label}</button>)}<button type="button" onClick={() => setSection('settings')} aria-current={section === 'settings' ? 'page' : undefined}>Configurações</button></nav>
    {section === 'settings' && <FinancialReset workspaceId={workspaceId} onReset={onReset} />}
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
