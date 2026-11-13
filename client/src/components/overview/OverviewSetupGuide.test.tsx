import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OverviewSetupGuide } from './OverviewSetupGuide'
import type { OverviewNavigation, OverviewSection } from './Overview'
import type { FinancialAccount } from '../../api/accounts'
import type { SixMonthPlan } from '../../api/monthlyPlan'
import type { PlanActualScopeChoice } from '../../api/planActual'
import type { PluggyConnection } from '../../api/pluggyPoc'
import { sixMonths } from '../monthlyPlanFixtures'
import { useSixMonthPlan } from './useSixMonthPlan'

// P1: guia "Configure seu orçamento" com dados fictícios. Cada etapa tem a sua consulta; nenhuma depende do resumo da Visão Geral.

const COMPETENCE = '2026-11'
const now = new Date(); const CURRENT = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
const account = (over: Partial<FinancialAccount> = {}): FinancialAccount => ({
  id: 'a1', name: 'Itaú PF', institution: 'Itaú', type: 'Checking', financialScope: 'PF', responsible: null, openingBalance: '1000.00', openingBalanceDate: '2026-11-01',
  isActive: true, currency: 'BRL', createdAt: '2026-11-01T00:00:00Z', origin: 'Manual', integration: null, isPrimary: true, ...over,
})
const connection = (over: Partial<PluggyConnection> = {}): PluggyConnection => ({
  id: 'c1', label: 'Itaú', itemReference: 'item', institution: 'Itaú', institutionType: null, isOpenFinance: null, state: 'Healthy', status: null, executionStatus: null,
  lastUpdatedAt: '2026-11-04T12:00:00Z', consentExpiresAt: null, errorCode: null, errorMessage: null, integrationError: null, createdAt: '2026-11-01T00:00:00Z', ...over,
})
const total = (over: object = {}) => ({ targetDefined: false, target: null, realized: '0.00', committed: '0.00', estimated: '0.00', projected: '0.00', margin: null, excess: null, realizedPercent: null, projectedPercent: null, status: 'NoTarget', ...over })
const summary = (over: object = {}) => ({ state: 'TargetNotDefined', competence: CURRENT, scopeKind: 'pf', total: total(over), notes: [] })
const defined = (target: string) => ({ ...summary({ targetDefined: true, target, status: 'WithinBudget' }), state: 'Complete' })
const plan = (patch: Partial<SixMonthPlan['months'][number]> = {}): SixMonthPlan => ({ ...sixMonths, from: COMPETENCE, months: sixMonths.months.map(m => m.competence === COMPETENCE ? { ...m, ...patch } : m) })
const partialMonth = { coverage: { dataComplete: false, obligationsCoverage: 'Covered' as const, reasons: ['CardCoverageIncomplete'] } }
const noData = { coverage: { dataComplete: false, obligationsCoverage: 'Unknown' as const, reasons: [] }, closingBalance: null, minimumProjectedBalance: null }

type Reply = unknown
interface Routes { accounts: Reply; status: Reply; connections: Reply; runs: Reply; summary: Reply; plan: Reply }
const defaults = (): Routes => ({ accounts: [account()], status: true, connections: [], runs: { items: [], total: 0, page: 1, pageSize: 100 }, summary: summary(), plan: plan() })
let routes: Routes
let calls: { url: string; method: string }[] = []

async function answer(reply: Reply, url: string): Promise<Response> {
  const value = typeof reply === 'function' ? await (reply as (u: string) => unknown)(url) : reply
  if (value === 'error') return new Response('{}', { status: 500 })
  if (value === 'hang') return new Promise<Response>(() => {})
  return json(value)
}
function mockApi(over: Partial<Routes> = {}) {
  routes = { ...defaults(), ...over }
  calls = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, method: init?.method ?? 'GET' })
    if (url.endsWith('/pluggy-integration/status')) return routes.status === true ? new Response('{}', { status: 200 }) : routes.status === false ? new Response('{}', { status: 404 }) : answer(routes.status, url)
    if (url.endsWith('/pluggy-integration/connections')) return answer(routes.connections, url)
    if (url.includes('/automation/runs')) return answer(routes.runs, url)
    if (url.includes('/financial-dashboard/budget-summary')) return answer(routes.summary, url)
    if (url.includes('/monthly-plan/six-months')) return answer(routes.plan, url)
    if (url.endsWith('/accounts') || url.includes('/accounts?')) return answer(routes.accounts, url)
    return new Response('{}', { status: 404 })
  }))
}
const runs = (...ids: string[]) => ({ items: ids.map((connectionId, i) => ({ id: `r${i}`, kind: 'Sync', state: 'Completed', connectionId })), total: ids.length, page: 1, pageSize: 100 })
const count = (part: string) => calls.filter(call => call.url.includes(part)).length

const navigations: { section: OverviewSection; context?: OverviewNavigation }[] = []
const PRIMARY: PlanActualScopeChoice = { kind: 'primary' } // referência estável, como a seleção da Visão Geral (um objeto novo por render refaria a consulta)
function Guide({ scope = PRIMARY, competence = COMPETENCE, refreshKey = 0 }) {
  const plan = useSixMonthPlan('w', competence, scope, refreshKey) // a mesma leitura que a Visão Geral compartilha com a Evolução
  return <OverviewSetupGuide workspaceId="w" competence={competence} scope={scope} refreshKey={refreshKey} plan={plan} onNavigate={(section, context) => { navigations.push({ section, context }) }} />
}
const setup = (props: Parameters<typeof Guide>[0] = {}) => { navigations.length = 0; return render(<Guide {...props} />) }
const step = (name: string) => within(screen.getByRole('list')).getByText(name).closest('li') as HTMLElement
const block = () => screen.findByRole('region', { name: 'Configure seu orçamento' })
const stateOf = (name: string) => step(name).querySelector('.ov-setup__state')?.textContent

beforeEach(() => { try { window.localStorage.clear() } catch { /* sem armazenamento */ } })
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('guia de primeira configuração — percursos', () => {
  it('orçamento novo: próxima ação é conectar o banco, com o percurso manual ao lado; só a próxima etapa tem ação', async () => {
    mockApi({ accounts: [] })
    setup()
    await block()
    expect(within(screen.getByRole('list')).getAllByRole('listitem')).toHaveLength(4)
    await waitFor(() => expect(stateOf('Conectar uma conta')).toBe('Pendente'))
    fireEvent.click(within(step('Conectar uma conta')).getByRole('button', { name: 'Conectar banco' }))
    expect(navigations[0]).toEqual({ section: 'automation', context: { automationTab: 'connections' } })
    fireEvent.click(within(step('Conectar uma conta')).getByRole('button', { name: 'Usar conta manual' }))
    expect(navigations[1].section).toBe('accounts')
    expect(within(step('Definir o orçamento')).queryByRole('button')).toBeNull()
  })

  it('integração indisponível: só o percurso manual, com o motivo', async () => {
    mockApi({ accounts: [], status: false })
    setup()
    await block()
    await waitFor(() => expect(within(step('Conectar uma conta')).getByText(/integração bancária não está disponível/)).toBeTruthy())
    const first = step('Conectar uma conta')
    expect(within(first).queryByRole('button', { name: 'Conectar banco' })).toBeNull()
    expect(within(first).getByRole('button', { name: 'Usar conta manual' })).toBeTruthy()
  })

  it('conexão sem primeira importação é um estado distinto e leva à conexão', async () => {
    mockApi({ connections: [connection({ state: 'Updating', lastUpdatedAt: null })] })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Conferir os dados')).toBe('Importando'))
    expect(stateOf('Conectar uma conta')).toBe('Concluída')
    fireEvent.click(within(step('Conferir os dados')).getByRole('button', { name: 'Ver conexão' }))
    expect(navigations[0]).toEqual({ section: 'automation', context: { automationTab: 'connections', connectionIds: ['c1'] } })
  })

  it('primeira importação desconhecida aparece como Consultando, nunca como pendente', async () => {
    mockApi({ connections: [connection()], runs: 'hang' })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Conferir os dados')).toBe('Consultando'))
    expect(within(step('Conferir os dados')).queryByRole('button')).toBeNull()
    expect(screen.queryByText(/Aguardando a primeira importação/)).toBeNull()
  })

  it('primeira importação com falha leva à conexão afetada', async () => {
    mockApi({ connections: [connection({ id: 'c9', state: 'Error', errorCode: 'X' })] })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Conferir os dados')).toBe('Pendência'))
    expect(within(step('Conferir os dados')).getByText(/primeira importação não foi concluída/)).toBeTruthy()
    fireEvent.click(within(step('Conferir os dados')).getByRole('button', { name: 'Ver pendência' }))
    expect(navigations[0].context).toEqual({ automationTab: 'connections', connectionIds: ['c9'] })
  })

  it('importação anterior concluída, sem depender do escopo: dados concluídos', async () => {
    mockApi({ connections: [connection()], runs: runs('c1') })
    setup({ scope: { kind: 'account', accountId: 'a1' } })
    await block()
    await waitFor(() => expect(stateOf('Conferir os dados')).toBe('Concluída'))
  })

  it('percurso manual: conta sem saldo mantém a etapa de dados pendente', async () => {
    mockApi({ accounts: [account({ openingBalance: null, openingBalanceDate: null })] })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Conferir os dados')).toBe('Pendente'))
    expect(within(step('Conectar uma conta')).getByText(/cadastrada manualmente/)).toBeTruthy()
    fireEvent.click(within(step('Conferir os dados')).getByRole('button', { name: 'Usar conta manual' }))
    expect(navigations[0].section).toBe('accounts')
  })

  it('meta ausente (competência atual) e meta zero definida são estados distintos', async () => {
    mockApi()
    const first = setup()
    await block()
    await waitFor(() => expect(stateOf('Definir o orçamento')).toBe('Meta ausente'))
    expect(calls.some(call => call.url.includes(`budget-summary?competence=${CURRENT}`))).toBe(true)
    fireEvent.click(within(step('Definir o orçamento')).getByRole('button', { name: 'Configurar orçamento' }))
    expect(navigations[0]).toEqual({ section: 'budgets', context: { competence: CURRENT } })
    first.unmount()
    mockApi({ summary: defined('0.00'), plan: plan(partialMonth) })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Definir o orçamento')).toBe('Concluída'))
    expect(within(step('Definir o orçamento')).getByText(/valor zero/)).toBeTruthy()
  })
})

describe('guia de primeira configuração — retomada', () => {
  it('ao voltar de Contas, Automação ou Orçamento (nova montagem) relê tudo e retoma na etapa correta; recarga direta idem', async () => {
    mockApi({ accounts: [] })
    const first = setup()
    await block()
    await waitFor(() => expect(within(step('Conectar uma conta')).getByRole('button', { name: 'Conectar banco' })).toBeTruthy())
    first.unmount()
    // Conta cadastrada em Contas: a Visão Geral monta de novo e a próxima etapa passa a ser a meta.
    routes.accounts = [account()]
    const second = setup()
    await block()
    await waitFor(() => expect(stateOf('Conectar uma conta')).toBe('Concluída'))
    await waitFor(() => expect(within(step('Definir o orçamento')).getByRole('button', { name: 'Configurar orçamento' })).toBeTruthy())
    second.unmount()
    // Meta salva em Orçamento: o guia retoma na projeção.
    routes.summary = defined('5000.00'); routes.plan = plan(partialMonth)
    setup()
    await block()
    await waitFor(() => expect(within(step('Abrir a primeira projeção')).getByRole('button', { name: 'Ver projeção' })).toBeTruthy())
    expect(stateOf('Definir o orçamento')).toBe('Concluída')
  })
})

describe('guia de primeira configuração — projeção', () => {
  it('projeção parcial: mantém o bloco, mostra a limitação e "Ver projeção" abre Seis meses na competência e no escopo mostrados', async () => {
    mockApi({ summary: defined('5000.00'), plan: plan(partialMonth) })
    setup({ scope: { kind: 'all' } })
    await block()
    await waitFor(() => expect(stateOf('Abrir a primeira projeção')).toBe('Projeção parcial'))
    expect(within(step('Abrir a primeira projeção')).getByText(/Novembro de 2026 · .*limitações/)).toBeTruthy()
    fireEvent.click(within(step('Abrir a primeira projeção')).getByRole('button', { name: 'Ver projeção' }))
    expect(navigations[0]).toEqual({ section: 'planning', context: { planningTab: 'six', competence: COMPETENCE, recoveryScope: { kind: 'all', accountId: undefined } } })
  })

  it('projeção indisponível é um estado distinto, sem ação de abrir', async () => {
    mockApi({ summary: defined('5000.00'), plan: plan(noData) })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Abrir a primeira projeção')).toBe('Projeção indisponível'))
    expect(within(step('Abrir a primeira projeção')).queryByRole('button')).toBeNull()
  })

  it('resposta atrasada de outro escopo é descartada', async () => {
    let release: (value: unknown) => void = () => {}
    const slow = new Promise(resolve => { release = resolve })
    mockApi({ summary: defined('5000.00'), plan: (url: string) => url.includes('scope=primary') ? slow : plan(partialMonth) })
    const view = setup({ scope: { kind: 'primary' } })
    await waitFor(() => expect(count('scope=primary')).toBeGreaterThan(0))
    view.rerender(<Guide scope={{ kind: 'all' }} />)
    await waitFor(() => expect(stateOf('Abrir a primeira projeção')).toBe('Projeção parcial'))
    release(plan(noData))
    await new Promise(resolve => setTimeout(resolve, 30))
    expect(stateOf('Abrir a primeira projeção')).toBe('Projeção parcial')
  })
})

describe('guia de primeira configuração — falhas, independência e conclusão', () => {
  it('chega à primeira ação mesmo sem o resumo da Visão Geral (nenhuma consulta do dashboard é feita)', async () => {
    mockApi({ accounts: [] })
    setup()
    await block()
    await waitFor(() => expect(within(step('Conectar uma conta')).getByRole('button', { name: 'Conectar banco' })).toBeTruthy())
    expect(calls.some(call => call.url.includes('/financial-dashboard/overview') || call.url.includes('/financial-dashboard/plan-actual'))).toBe(false)
  })

  it('uma consulta lenta não esconde as demais: a meta carrega enquanto a projeção ainda consulta', async () => {
    mockApi({ plan: 'hang' })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Definir o orçamento')).toBe('Meta ausente'))
    expect(stateOf('Abrir a primeira projeção')).toBe('Consultando')
  })

  it('falha na meta: tentar novamente refaz só a meta e preserva as etapas confirmadas', async () => {
    mockApi({ summary: 'error', plan: plan(partialMonth) })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Definir o orçamento')).toBe('Meta não consultada'))
    expect(stateOf('Conectar uma conta')).toBe('Concluída')
    const before = { accounts: count('/accounts'), plan: count('/six-months'), summary: count('budget-summary'), runs: count('/automation/runs') }
    routes.summary = defined('3000.00')
    fireEvent.click(within(step('Definir o orçamento')).getByRole('button', { name: 'Tentar novamente' }))
    await waitFor(() => expect(stateOf('Definir o orçamento')).toBe('Concluída'))
    expect(count('budget-summary')).toBe(before.summary + 1)
    expect(count('/accounts')).toBe(before.accounts); expect(count('/six-months')).toBe(before.plan); expect(count('/automation/runs')).toBe(before.runs)
    expect(stateOf('Conectar uma conta')).toBe('Concluída')
  })

  it('falha nas contas: explica, oferece nova tentativa e o percurso manual', async () => {
    mockApi({ accounts: 'error' })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Conectar uma conta')).toMatch(/não consultadas/))
    const first = step('Conectar uma conta')
    expect(within(first).getByText(/Não foi possível consultar as contas/)).toBeTruthy()
    expect(within(first).getByRole('button', { name: 'Usar conta manual' })).toBeTruthy()
    routes.accounts = []
    fireEvent.click(within(first).getByRole('button', { name: 'Tentar novamente' }))
    await waitFor(() => expect(within(step('Conectar uma conta')).getByRole('button', { name: 'Conectar banco' })).toBeTruthy())
  })

  it('falha em etapa que não é a próxima ainda oferece Tentar novamente', async () => {
    mockApi({ accounts: [], plan: 'error' })
    setup()
    await block()
    await waitFor(() => expect(stateOf('Abrir a primeira projeção')).toBe('Projeção não consultada'))
    expect(within(step('Abrir a primeira projeção')).getByRole('button', { name: 'Tentar novamente' })).toBeTruthy()
    expect(within(step('Abrir a primeira projeção')).queryByRole('button', { name: 'Ver projeção' })).toBeNull()
  })

  it('configuração concluída: o guia não aparece e a conclusão fica registrada', async () => {
    mockApi({ connections: [connection()], runs: runs('c1'), summary: defined('5000.00') })
    setup()
    await waitFor(() => expect(window.localStorage.getItem('saldo360.setup-done.w')).toBe('1'))
    expect(screen.queryByRole('region', { name: 'Configure seu orçamento' })).toBeNull()
  })

  it('concluído antes: falha transitória ao atualizar não reabre o guia', async () => {
    window.localStorage.setItem('saldo360.setup-done.w', '1')
    mockApi({ summary: 'error', plan: 'error', runs: 'error', connections: [connection()] })
    setup()
    await waitFor(() => expect(count('budget-summary')).toBeGreaterThan(0))
    await new Promise(resolve => setTimeout(resolve, 50))
    expect(screen.queryByRole('region', { name: 'Configure seu orçamento' })).toBeNull()
  })

  it('concluído antes, mas agora há etapa confirmada pendente: o guia volta', async () => {
    window.localStorage.setItem('saldo360.setup-done.w', '1')
    mockApi({ accounts: [] })
    setup()
    await block()
    await waitFor(() => expect(window.localStorage.getItem('saldo360.setup-done.w')).toBeNull())
  })

  it('apenas consultas em andamento não são evidência: nada é mostrado', async () => {
    mockApi({ accounts: 'hang', status: 'hang', runs: 'hang', summary: 'hang', plan: 'hang' })
    setup()
    await new Promise(resolve => setTimeout(resolve, 30))
    expect(screen.queryByRole('region', { name: 'Configure seu orçamento' })).toBeNull()
  })

  it('somente leitura: nenhuma requisição de escrita ao abrir', async () => {
    mockApi({ connections: [connection()], runs: runs('c1') })
    setup()
    await block()
    expect(calls.every(call => call.method === 'GET')).toBe(true)
  })
})
