import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { OverviewSetupGuide } from './OverviewSetupGuide'
import type { OverviewNavigation, OverviewSection } from './Overview'
import type { DashboardOverview } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { PluggyConnection } from '../../api/pluggyPoc'
import { planActual } from '../planActualFixtures'
import { overview as baseOverview } from './overviewFixtures'
import { decision } from './decisionFixtures'
import { budgetControl } from './budgetControlFixtures'

// P1: guia "Configure seu orçamento" com dados fictícios. A integração e a meta vêm de consultas próprias; o resto, da Visão Geral já carregada.

const COMPETENCE = '2026-11'
const connection = (over: Partial<PluggyConnection> = {}): PluggyConnection => ({
  id: 'c1', label: 'Itaú', itemReference: 'item', institution: 'Itaú', institutionType: null, isOpenFinance: null, state: 'Healthy', status: null, executionStatus: null,
  lastUpdatedAt: '2026-11-04T12:00:00Z', consentExpiresAt: null, errorCode: null, errorMessage: null, integrationError: null, createdAt: '2026-11-01T00:00:00Z', ...over,
})
const planWithSync = (connections: { connectionId: string; lastSuccessAt: string | null }[]): PlanActualDashboard => ({
  ...planActual, sync: { autoEnabled: false, autoIntervalHours: 6, oldestSuccessAt: null, latestSuccessAt: null, sameMoment: true, stale: false, state: 'Fresh', nextAttemptAt: null,
    connections: connections.map(c => ({ ...c, label: 'Itaú', status: 'Healthy', lastAttemptAt: c.lastSuccessAt, nextAttemptAt: null, consentExpiringSoon: false })) },
})
const noTarget = budgetControl({ state: 'TargetNotDefined', total: { ...budgetControl().total, targetDefined: false, target: null, status: 'NoTarget' } })
const zeroTarget = budgetControl({ total: { ...budgetControl().total, targetDefined: true, target: '0.00' } })
const noAccounts: DashboardOverview['accounts'] = { state: 'Complete', asOf: '2026-11-05', items: [] }
const withData = (over: Partial<DashboardOverview> = {}): DashboardOverview => ({ ...baseOverview, decision: decision(), pfBudgetControl: budgetControl({ scopeKind: 'pf' }), ...over })

type Integration = 'unavailable' | 'error' | { connections: PluggyConnection[] }
let calls: string[] = []
function mockApi({ integration = { connections: [] }, summary }: { integration?: Integration; summary?: unknown } = {}) {
  calls = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    calls.push(url)
    if (url.endsWith('/pluggy-integration/status')) {
      if (integration === 'error') throw new TypeError('network')
      return integration === 'unavailable' ? new Response('{}', { status: 404 }) : new Response('{}', { status: 200 })
    }
    if (url.endsWith('/pluggy-integration/connections')) return new Response(JSON.stringify(typeof integration === 'object' ? integration.connections : []), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url.includes('/financial-dashboard/budget-summary')) return new Response(JSON.stringify(summary ?? { state: 'Complete', competence: COMPETENCE, scopeKind: 'pf', total: noTarget.total, notes: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } })
    return new Response('{}', { status: 404 })
  }))
}

const navigations: { section: OverviewSection; context?: OverviewNavigation }[] = []
function setup({ data = withData(), plan = planActual, competence = COMPETENCE, scope = { kind: 'primary' as const } }: { data?: DashboardOverview | null; plan?: PlanActualDashboard | null; competence?: string; scope?: { kind: 'primary' | 'all' | 'account'; accountId?: string } } = {}) {
  navigations.length = 0
  return render(<OverviewSetupGuide workspaceId="w" competence={competence} scope={scope} scopeText="Conta principal · Itaú PF" data={data} plan={plan} refreshKey={0} onNavigate={(section, context) => { navigations.push({ section, context }) }} />)
}
const step = (name: string) => within(screen.getByRole('list')).getByText(name).closest('li') as HTMLElement
const block = () => screen.findByRole('region', { name: 'Configure seu orçamento' })

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('guia de primeira configuração', () => {
  it('orçamento novo: próxima ação é conectar o banco, com o percurso manual ao lado', async () => {
    mockApi()
    setup({ data: withData({ accounts: noAccounts, pfBudgetControl: noTarget }) })
    const region = await block()
    expect(within(region).getAllByRole('listitem')).toHaveLength(4)
    expect(within(step('Conectar uma conta')).getByText('Pendente')).toBeTruthy()
    fireEvent.click(within(step('Conectar uma conta')).getByRole('button', { name: 'Conectar banco' }))
    expect(navigations[0]).toEqual({ section: 'automation', context: { automationTab: 'connections' } })
    fireEvent.click(within(step('Conectar uma conta')).getByRole('button', { name: 'Usar conta manual' }))
    expect(navigations[1].section).toBe('accounts')
    // Só a próxima etapa oferece ação.
    expect(within(step('Definir o orçamento')).queryByRole('button')).toBeNull()
  })

  it('integração indisponível: oferece só o percurso manual e explica o motivo', async () => {
    mockApi({ integration: 'unavailable' })
    setup({ data: withData({ accounts: noAccounts, pfBudgetControl: noTarget }) })
    await block()
    const account = step('Conectar uma conta')
    expect(within(account).getByText(/integração bancária não está disponível/)).toBeTruthy()
    expect(within(account).queryByRole('button', { name: 'Conectar banco' })).toBeNull()
    expect(within(account).getByRole('button', { name: 'Usar conta manual' })).toBeTruthy()
  })

  it('falha ao consultar a integração: mostra Tentar novamente e mantém o percurso manual', async () => {
    mockApi({ integration: 'error' })
    setup({ data: withData({ accounts: noAccounts, pfBudgetControl: noTarget }) })
    await block()
    const account = step('Conectar uma conta')
    expect(within(account).getByText('Integração não consultada')).toBeTruthy()
    expect(within(account).getByRole('button', { name: 'Usar conta manual' })).toBeTruthy()
    mockApi({ integration: { connections: [] } })
    fireEvent.click(within(account).getByRole('button', { name: 'Tentar novamente' }))
    await waitFor(() => expect(within(step('Conectar uma conta')).getByRole('button', { name: 'Conectar banco' })).toBeTruthy())
  })

  it('conexão criada sem primeira importação é um estado distinto e leva à conexão', async () => {
    mockApi({ integration: { connections: [connection({ state: 'Updating', lastUpdatedAt: null })] } })
    setup({ data: withData({ pfBudgetControl: noTarget }), plan: planWithSync([{ connectionId: 'c1', lastSuccessAt: null }]) })
    await block()
    const data = step('Conferir os dados')
    expect(within(data).getByText('Importando')).toBeTruthy()
    expect(within(step('Conectar uma conta')).getByText('Concluída')).toBeTruthy()
    fireEvent.click(within(data).getByRole('button', { name: 'Ver conexão' }))
    expect(navigations[0]).toEqual({ section: 'automation', context: { automationTab: 'connections', connectionIds: ['c1'] } })
  })

  it('primeira importação com falha leva à conexão afetada', async () => {
    mockApi({ integration: { connections: [connection({ id: 'c9', state: 'Error', errorCode: 'X' })] } })
    setup({ data: withData({ pfBudgetControl: noTarget }), plan: planWithSync([{ connectionId: 'c9', lastSuccessAt: null }]) })
    await block()
    const data = step('Conferir os dados')
    expect(within(data).getByText('Pendência')).toBeTruthy()
    expect(within(data).getByText(/primeira importação não foi concluída/)).toBeTruthy()
    fireEvent.click(within(data).getByRole('button', { name: 'Ver pendência' }))
    expect(navigations[0].context).toEqual({ automationTab: 'connections', connectionIds: ['c9'] })
  })

  it('meta ausente e meta zero são estados distintos', async () => {
    mockApi()
    const first = setup({ data: withData({ pfBudgetControl: noTarget }) })
    await block()
    expect(within(step('Definir o orçamento')).getByText('Meta ausente')).toBeTruthy()
    fireEvent.click(within(step('Definir o orçamento')).getByRole('button', { name: 'Configurar orçamento' }))
    expect(navigations[0]).toEqual({ section: 'budgets', context: { competence: COMPETENCE } })
    first.unmount()
    // Meta zero definida conta como concluída; o bloco só continua por causa da projeção parcial.
    setup({ data: withData({ pfBudgetControl: zeroTarget, decision: decision({ state: 'Partial' }) }) })
    await block()
    expect(within(step('Definir o orçamento')).getByText('Concluída')).toBeTruthy()
    expect(within(step('Definir o orçamento')).getByText(/valor zero/)).toBeTruthy()
    expect(screen.queryByText('Meta ausente')).toBeNull()
  })

  it('projeção parcial: mantém o bloco, explicita a limitação e "Ver projeção" abre a competência e o escopo mostrados', async () => {
    mockApi()
    setup({ data: withData({ pfBudgetControl: zeroTarget, decision: decision({ state: 'Partial' }) }), scope: { kind: 'all' } })
    await block()
    const projection = step('Abrir a primeira projeção')
    expect(within(projection).getByText('Projeção parcial')).toBeTruthy()
    expect(within(projection).getByText(/Novembro de 2026 · Conta principal.*limitações/)).toBeTruthy()
    fireEvent.click(within(projection).getByRole('button', { name: 'Ver projeção' }))
    expect(navigations[0]).toEqual({ section: 'planning', context: { planningTab: 'projection', competence: COMPETENCE, recoveryScope: { kind: 'all', accountId: undefined } } })
  })

  it('projeção indisponível é um estado distinto, sem ação de abrir', async () => {
    mockApi()
    const unavailable = decision()
    unavailable.coverage = { ...unavailable.coverage, state: 'Unavailable', unavailableReason: 'ClosedCompetence' }
    setup({ data: withData({ pfBudgetControl: zeroTarget, decision: unavailable }), scope: { kind: 'all' } })
    await block()
    expect(within(step('Abrir a primeira projeção')).getByText('Projeção indisponível')).toBeTruthy()
    expect(within(step('Abrir a primeira projeção')).queryByRole('button')).toBeNull()
  })

  it('percurso manual: conta sem saldo mantém a etapa de dados pendente', async () => {
    mockApi()
    setup({ data: withData({ pfBudgetControl: zeroTarget, accounts: { ...baseOverview.accounts, items: baseOverview.accounts.items.map(a => ({ ...a, balanceAvailable: false, balance: null })) } }) })
    await block()
    const data = step('Conferir os dados')
    expect(within(data).getByText('Pendente')).toBeTruthy()
    expect(within(step('Conectar uma conta')).getByText(/cadastrada manualmente/)).toBeTruthy()
    fireEvent.click(within(data).getByRole('button', { name: 'Usar conta manual' }))
    expect(navigations[0].section).toBe('accounts')
  })

  it('orçamento completo sem pendências: o bloco não aparece', async () => {
    mockApi({ integration: { connections: [connection()] } })
    setup({ data: withData(), plan: planWithSync([{ connectionId: 'c1', lastSuccessAt: '2026-11-04T12:00:00Z' }]) })
    await waitFor(() => expect(calls.some(url => url.endsWith('/pluggy-integration/connections'))).toBe(true))
    await new Promise(resolve => setTimeout(resolve, 30))
    expect(screen.queryByRole('region', { name: 'Configure seu orçamento' })).toBeNull()
  })

  it('meta da competência atual: consulta o resumo quando a competência navegada é outra e mostra erro com nova tentativa', async () => {
    mockApi({ summary: { state: 'Unavailable', competence: COMPETENCE, scopeKind: 'pf', total: null, notes: [] } })
    setup({ data: withData({ competence: '2026-10' }), competence: '2026-10' })
    await block()
    expect(calls.some(url => url.includes('budget-summary') && url.includes(`competence=${COMPETENCE}`))).toBe(true)
    const budget = step('Definir o orçamento')
    expect(within(budget).getByText('Meta não consultada')).toBeTruthy()
  })

  it('não consulta o resumo quando a competência navegada é a atual (reaproveita o orçamento PF)', async () => {
    mockApi()
    setup({ data: withData({ pfBudgetControl: noTarget }) })
    await block()
    expect(calls.some(url => url.includes('budget-summary'))).toBe(false)
  })

  it('não renderiza nada enquanto a Visão Geral não carregou', () => {
    mockApi()
    setup({ data: null })
    expect(screen.queryByRole('region')).toBeNull()
  })

  it('o bloco é somente leitura: nenhuma consulta de escrita ao abrir', async () => {
    mockApi({ integration: { connections: [connection()] } })
    setup({ data: withData({ pfBudgetControl: noTarget }), plan: planWithSync([{ connectionId: 'c1', lastSuccessAt: '2026-11-04T12:00:00Z' }]) })
    await block()
    const fetchMock = globalThis.fetch as unknown as { mock: { calls: [unknown, RequestInit | undefined][] } }
    expect(fetchMock.mock.calls.every(([, init]) => !init?.method || init.method === 'GET')).toBe(true)
  })
})
