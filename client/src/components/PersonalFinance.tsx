import { useEffect, useRef, useState, type ReactNode } from 'react'
import { AppShell } from './AppShell'
import { todayInSaoPaulo } from '../dates'
import { listAccounts } from '../api/accounts'
import { accountFromParams, accountToParam, isCompetence, parseRoute, planningTabFromParams, planningTabParams, problemNotice, routeUrl, sectionPaths, unavailableAccountNotice, type AppRoute, type RouteSection } from '../routing'
import type { CategoryType } from '../api/categories'
import type { PlanActualScopeChoice } from '../api/planActual'
import { ensurePersonal, type PersonalWorkspace } from '../api/workspaces'
import { Accounts } from './Accounts'
import { Categories } from './Categories'
import { Transactions } from './Transactions'
import { Transfers } from './Transfers'
import { Planned } from './Planned'
import type { BudgetGroup } from './budget/budgetText'
import { Overview, type OverviewNavigation, type OverviewSection, type OverviewSelection } from './overview/Overview'
import { FinancialCalendarPage, type CalendarFilters } from './FinancialCalendar'
import { FinancialBudgetsPage } from './FinancialBudgets'
import { FinancialCreditCardsPage } from './FinancialCreditCards'
import { FinancialRecurrences } from './FinancialRecurrences'
import { PlanningHub, type PlanningState } from './PlanningHub'
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
  if (workspace) return <>{resetResult && <div role="status" className="workspace-reset-result"><p>Orçamento reiniciado. Todos os dados financeiros deste orçamento foram removidos. Sua conta continua ativa. As categorias e subcategorias padrão foram recriadas do zero. Para voltar a usar o banco, reconecte-o: as conexões e os dados importados da Pluggy foram apagados.</p>
    {error && <p role="alert">{error}</p>}</div>}
    <WorkspaceArea key={`${workspace.id}:${generation}`} workspace={workspace} userArea={userArea} onReset={value => { window.history.replaceState(null, '', '/'); setResetResult(value); setGeneration(old => old + 1) }} /></>
  return <div className="bootstrap-state"><div className="bootstrap-state__user">{userArea}</div><section className="card">{error ? <><p role="alert">{error}</p><button type="button" onClick={() => { setError(null); setRetry(value => value + 1) }}>Tentar novamente</button></> : <p role="status">Abrindo seu orçamento pessoal…</p>}</section></div>
}

const scopeParam = (scope: PlanActualScopeChoice) => accountToParam(scope.kind === 'all' ? { kind: 'all' } : scope.kind === 'account' && scope.accountId ? { kind: 'account', accountId: scope.accountId } : { kind: 'default' })

function overviewParams(selection: OverviewSelection) {
  const params = new URLSearchParams({ competencia: selection.competence })
  const account = scopeParam(selection.scope)
  if (account) params.set('conta', account)
  return params
}

function selectionFromParams(params: URLSearchParams): OverviewSelection {
  const competence = params.get('competencia')
  const account = accountFromParams(params)
  return {
    competence: isCompetence(competence) ? competence : todayInSaoPaulo().slice(0, 7),
    scope: account.kind === 'all' ? { kind: 'all' } : account.kind === 'account' ? { kind: 'account', accountId: account.accountId } : { kind: 'primary' },
  }
}

function isSection(value: string): value is RouteSection { return value in sectionPaths }

function WorkspaceArea({ workspace, onReset, userArea }: { workspace: PersonalWorkspace; onReset: (result: ResetResult) => void; userArea?: ReactNode }) {
  // A seção e os filtros essenciais vivem na URL: recarregar, voltar, avançar e abrir um link restauram a tela. Sem biblioteca de rotas.
  const [route, setRoute] = useState<AppRoute>(() => parseRoute(window.location))
  const [routeKey, setRouteKey] = useState(0)
  const [notice, setNotice] = useState<string | null>(() => route.problems.length ? problemNotice : null)
  // Contas já confirmadas para os parâmetros da URL (as escolhidas nas próprias telas entram direto).
  const [validAccounts, setValidAccounts] = useState<ReadonlySet<string>>(() => new Set())
  const knownAccounts = useRef<ReadonlySet<string> | null>(null)
  const section: string = route.section
  const [version, setVersion] = useState(0)
  const [competence, setCompetence] = useState<string | undefined>()
  const [budgetGroup, setBudgetGroup] = useState<BudgetGroup | undefined>()
  // Competência e conta da Visão Geral ficam aqui para sobreviver à ida a um detalhe e à volta.
  const [overviewSelection, setOverviewSelection] = useState<OverviewSelection>(() => route.section === 'overview' ? selectionFromParams(route.params) : { competence: todayInSaoPaulo().slice(0, 7), scope: { kind: 'primary' } })
  const [transactionContext, setTransactionContext] = useState<{ key: number; filters?: OverviewNavigation['transactions']; newTransaction?: boolean } | null>(null)
  const changed = () => setVersion(value => value + 1)
  const workspaceId = workspace.id

  const current = () => window.location.pathname + window.location.search
  const go = (target: RouteSection, params?: URLSearchParams, mode: 'push' | 'replace' = 'push') => {
    const url = routeUrl(target, params)
    const changedUrl = url !== current()
    if (changedUrl) window.history[mode === 'push' ? 'pushState' : 'replaceState'](null, '', url)
    const account = params?.get('conta')
    if (account && account !== 'todas') setValidAccounts(known => known.has(account) ? known : new Set(known).add(account))
    if (changedUrl) {
      const next = parseRoute(window.location)
      setRoute(next)
      if (mode === 'push') setNotice(null)
    }
  }

  // Normaliza uma URL aberta com caminho ou parâmetros inválidos (substitui a entrada: não cria histórico).
  useEffect(() => {
    if (route.problems.length) window.history.replaceState(null, '', routeUrl(route.section, route.params))
    // Só na abertura.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])
  useEffect(() => {
    const onPop = () => {
      const next = parseRoute(window.location)
      if (next.problems.length) window.history.replaceState(null, '', routeUrl(next.section, next.params))
      if (next.section === 'overview') setOverviewSelection(selectionFromParams(next.params))
      setRoute(next); setRouteKey(key => key + 1); setNotice(next.problems.length ? problemNotice : null)
    }
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  // Conta da URL (link colado, recarga, histórico): só vale se existir neste orçamento; senão cai no padrão do módulo, avisando no bloco de filtros.
  const urlAccount = accountFromParams(route.params)
  const accountToCheck = urlAccount.kind === 'account' && (section === 'overview' || section === 'calendar' || section === 'planning') ? urlAccount.accountId : null
  const awaitingAccount = accountToCheck !== null && !validAccounts.has(accountToCheck)
  useEffect(() => {
    if (!accountToCheck || validAccounts.has(accountToCheck)) return
    const controller = new AbortController()
    const settle = (ids: ReadonlySet<string> | null) => {
      if (controller.signal.aborted) return
      if (ids === null || ids.has(accountToCheck)) { setValidAccounts(known => new Set(known).add(accountToCheck)); return }
      const params = new URLSearchParams(window.location.search); params.delete('conta')
      window.history.replaceState(null, '', routeUrl(parseRoute(window.location).section, params))
      const next = parseRoute(window.location)
      if (next.section === 'overview') setOverviewSelection(selectionFromParams(next.params))
      setRoute(next); setRouteKey(key => key + 1); setNotice(unavailableAccountNotice)
    }
    if (knownAccounts.current) settle(knownAccounts.current)
    else listAccounts(workspaceId, true, controller.signal)
      .then(accounts => { knownAccounts.current = new Set(accounts.map(account => account.id.toLowerCase())); settle(knownAccounts.current) })
      .catch(() => settle(null)) // sem lista, a própria tela mostra o erro da API
    return () => controller.abort()
  }, [accountToCheck, validAccounts, workspaceId])

  const selectOverview = (selection: OverviewSelection) => { setOverviewSelection(selection); go('overview', overviewParams(selection)) }
  const navigate = (target: string) => {
    if (!isSection(target) || target === section) return
    go(target, target === 'overview' ? overviewParams(overviewSelection) : undefined)
  }
  const agenda = (value: string) => go('calendar', new URLSearchParams({ competencia: value }))
  const budgets = (value: string) => { setCompetence(value); setBudgetGroup(undefined); go('budgets') }
  // Orçamento → central de automação: abre a lista de revisão com as despesas do período sem tipo definido, inclusive as compras de cartão importadas.
  const [goalId, setGoalId] = useState<string | undefined>()
  const [automationEntry, setAutomationEntry] = useState<AutomationEntry | undefined>()
  const reviewClassification = (range: { from: string; to: string }) => { setAutomationEntry({ key: Date.now(), tab: 'automations', review: { kind: 'nature', ...range } }); go('automation') }
  // Resumo → detalhe: leva a conta, a competência ou a data da Visão Geral para a página de destino.
  const openFromOverview = (target: OverviewSection, context?: OverviewNavigation) => {
    setCompetence(target === 'budgets' ? context?.competence : undefined)
    setBudgetGroup(target === 'budgets' ? context?.budgetGroup : undefined)
    setTransactionContext(target === 'transactions' ? { key: Date.now(), filters: context?.transactions, newTransaction: context?.newTransaction } : null)
    setGoalId(target === 'goals' ? context?.goalId : undefined)
    if (target === 'automation') setAutomationEntry({ key: Date.now(), tab: context?.automationTab, review: context?.review, connectionIds: context?.connectionIds })
    let params: URLSearchParams | undefined
    if (target === 'calendar' && context?.month) params = new URLSearchParams({ competencia: context.month })
    if (target === 'planning') {
      params = new URLSearchParams({ aba: planningTabParams[context?.planningTab ?? 'monthly'] })
      if (context?.competence) params.set('competencia', context.competence)
      if (context?.recoveryScope) { const account = scopeParam(context.recoveryScope); if (account) params.set('conta', account); if (context.recoveryScope.kind === 'account') params.set('recuperacao', '1') }
    }
    go(target, params)
  }
  const reportCalendar = (filters: CalendarFilters, mode: 'push' | 'replace') => {
    const params = new URLSearchParams({ competencia: filters.month })
    if (filters.account) params.set('conta', filters.account)
    if (filters.type) params.set('tipo', filters.type)
    go('calendar', params, mode)
  }
  const reportPlanning = (state: PlanningState, mode: 'push' | 'replace') => {
    const params = new URLSearchParams({ aba: planningTabParams[state.tab], competencia: state.competence })
    const account = scopeParam(state.scope)
    if (account) params.set('conta', account)
    if (state.recovery) params.set('recuperacao', '1')
    go('planning', params, mode)
  }
  // Central de automação: só aparece quando a API habilita a Pluggy para este usuário (Pluggy:Enabled).
  const [pluggyLab, setPluggyLab] = useState(false)
  const [labChecked, setLabChecked] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    isPluggyLabAvailable(workspaceId, controller.signal).then(available => { if (!controller.signal.aborted) { setPluggyLab(available); setLabChecked(true) } }).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId])
  useEffect(() => { if (labChecked && !pluggyLab && section === 'automation') go('overview', overviewParams(overviewSelection), 'replace') })

  const calendarAccount = urlAccount.kind === 'all' ? 'todas' : urlAccount.kind === 'account' ? urlAccount.accountId : undefined
  const calendarType = route.params.get('tipo') as CategoryType | null
  const planningAccount: PlanActualScopeChoice | undefined = urlAccount.kind === 'all' ? { kind: 'all' } : urlAccount.kind === 'account' ? { kind: 'account', accountId: urlAccount.accountId } : undefined
  const planningTab = planningTabFromParams(route.params)
  const waiting = <section className="card" role="status">Conferindo a conta do link…</section>
  return <AppShell section={section} onNavigate={navigate} automation={pluggyLab} workspaceName={workspace.name} userArea={userArea} subtitle={section === 'overview' ? 'Sua situação, o mês e os próximos compromissos.' : 'Seu orçamento pessoal, sem compartilhar dados com a família.'}>
    {section === 'automation' && pluggyLab && <AutomationCenter key={automationEntry?.key} workspaceId={workspaceId} refreshKey={String(version)} onChanged={changed} entry={automationEntry} />}
    {section === 'settings' && <WorkspaceSettings workspaceId={workspaceId} onReset={onReset} />}
    {section === 'overview' && (awaitingAccount ? waiting : <Overview workspaceId={workspaceId} refreshKey={version} selection={overviewSelection} onSelectionChange={selectOverview} onNavigate={openFromOverview} notice={notice} />)}
    {section === 'calendar' && (awaitingAccount ? waiting : <FinancialCalendarPage key={routeKey} workspaceId={workspaceId} initialMonth={route.params.get('competencia') ? `${route.params.get('competencia')}-01` : undefined} initialAccount={calendarAccount} initialType={calendarType ?? undefined}
      filterNotice={notice} onFiltersChange={reportCalendar} onChanged={changed} onNavigateToBudgets={budgets} onNavigateToCards={() => go('cards')} />)}
    {section === 'budgets' && <FinancialBudgetsPage key={`${competence}:${budgetGroup}`} workspaceId={workspaceId} initialCompetence={competence} initialGroup={budgetGroup} refreshKey={version} onNavigateToAgenda={agenda} onReviewClassification={reviewClassification} />}
    {section === 'cards' && <FinancialCreditCardsPage workspaceId={workspaceId} refreshKey={version} onChanged={changed} />}
    {section === 'recurrences' && <FinancialRecurrences workspaceId={workspaceId} refreshKey={version} onChanged={changed} onNavigateToAgenda={agenda} />}
    {section === 'planning' && (awaitingAccount ? waiting : <PlanningHub key={routeKey} initialTab={planningTab} initialCompetence={route.params.get('competencia') ?? undefined} initialScope={planningAccount}
      recoveryScope={route.params.get('recuperacao') && planningAccount?.kind === 'account' ? planningAccount : undefined} notice={notice} onStateChange={reportPlanning} workspaceId={workspaceId} refreshKey={version} onChanged={changed} />)}
    {section === 'debts' && <FinancialDebts workspaceId={workspaceId} refreshKey={version} onChanged={changed} />}
    {section === 'goals' && <FinancialGoals key={goalId} initialGoalId={goalId} workspaceId={workspaceId} refreshKey={version} onChanged={changed} onNavigateToPlanning={() => go('planning')} />}
    {section === 'accounts' && <Accounts workspaceId={workspaceId} members={[{ userId: workspace.ownerUserId, displayName: 'Você', isCurrentUser: true }]} balanceVersion={version} onOpenStatement={(filters) => { setTransactionContext({ key: Date.now(), filters: { from: filters.from, to: filters.to, accountId: filters.accountId, type: filters.type } }); go('transactions') }} />}
    {section === 'categories' && <Categories workspaceId={workspaceId} />}
    {section === 'transactions' && <Transactions key={transactionContext?.key} workspaceId={workspaceId} onChanged={changed} refreshKey={version} initialFilters={transactionContext?.filters ? { from: transactionContext.filters.from, to: transactionContext.filters.to, accountId: transactionContext.filters.accountId ?? '', ...(transactionContext.filters.type === 'Transfer' ? { type: 'Transfer' as const } : {}), ...(transactionContext.filters.expenseNature ? { expenseNature: transactionContext.filters.expenseNature, type: 'Expense' as const } : {}) } : undefined} startWithForm={transactionContext?.newTransaction} onNavigate={navigate} />}
    {section === 'transfers' && <Transfers workspaceId={workspaceId} onChanged={changed} />}
    {section === 'planned' && <Planned workspaceId={workspaceId} onChanged={changed} refreshKey={version} />}
  </AppShell>
}
