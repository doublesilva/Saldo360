import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AutomationOverview, AutomationRun, ClassificationReview, ReviewItem } from '../api/pluggyAutomation'
import { AutomationCenter } from './AutomationCenter'

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const text = (element: Element | null) => (element?.textContent ?? '').replace(/ /g, ' ')

const syncRun = (overrides: Partial<AutomationRun> = {}): AutomationRun => ({
  id: 's1', kind: 'Sync', trigger: 'Scheduled', state: 'Completed', startedAt: '2026-10-29T10:30:00Z', finishedAt: '2026-10-29T10:31:15Z', durationSeconds: 75,
  connectionId: 'c1', scope: 'Itaú PF', relatedRunId: null, failureCode: null, interrupted: false,
  sync: { accountsTotal: 4, accountsSynced: 4, seen: 30, created: 12, updated: 3, unchanged: 15, missing: 0, errors: 0, errorSummary: null }, categorization: null, ...overrides,
})
const catRun = (overrides: Partial<AutomationRun> = {}): AutomationRun => ({
  id: 'k1', kind: 'Categorization', trigger: 'AfterSync', state: 'CompletedWithPending', startedAt: '2026-10-29T10:31:20Z', finishedAt: '2026-10-29T10:31:50Z', durationSeconds: 30,
  connectionId: 'c1', scope: 'Itaú PF', relatedRunId: 's1', failureCode: null, interrupted: false, sync: null,
  categorization: { considered: 9, classifiedLocal: 2, classifiedAi: 5, pending: 2, skippedChanged: 0, aiItemsSent: 7, aiCalls: 1, usedAi: true, model: 'modelo-teste', aiStoppedBecause: null }, ...overrides,
})

const overview = (overrides: Partial<AutomationOverview> = {}): AutomationOverview => ({
  now: '2026-10-30T12:00:00Z',
  server: { integrationAvailable: true, importEnabled: true, schedulerEnabled: true, defaultIntervalHours: 24, windowStartHour: 0, windowEndHour: 24, timeZone: 'America/Sao_Paulo', intervalOptions: [6, 12, 24],
    ai: { enabled: true, available: true, model: 'modelo-teste', reason: null } },
  settings: { syncPaused: false, syncIntervalHours: 24, syncIntervalIsDefault: true, categorizeAfterSync: false, categorizeConsentAt: null },
  sync: { state: 'Scheduled', connections: 2, connectionsNeedingAttention: 0, running: false, nextRunAt: '2026-10-30T10:30:00Z', lastRun: syncRun() },
  categorization: { state: 'Ready', eligible: 9, needsReview: 4, notEvaluated: 1, running: false, lastRun: catRun() },
  ...overrides,
})

const reviewItem = (overrides: Partial<ReviewItem> = {}): ReviewItem => ({
  action: 'CardPurchase', recordId: 'p1', cardId: 'card1', date: '2026-10-12', description: 'Livraria Online', amount: '80.00', categoryId: null, categoryPath: null, expenseNature: 'Undefined',
  cardName: 'Itaú Visa', accountName: null, imported: true, canChangeCategory: true, canChangeNature: true, scope: 'Esta compra (as parcelas herdam o tipo)', ...overrides,
})
const review = (items: ReviewItem[], overrides: Partial<ClassificationReview> = {}): ClassificationReview => ({
  kind: 'nature', from: '2026-10-01', to: '2026-10-31', wholeBudget: false, items, total: items.length, page: 1, pageSize: 10, unclassifiableCount: 0, ...overrides,
})

interface Api { overview?: AutomationOverview | Response | 'fail'; runs?: AutomationRun[]; review?: ClassificationReview }
function mockApi(api: Api = {}) {
  const calls: { url: string; method: string; body?: string }[] = []
  vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input); const method = init?.method ?? 'GET'
    calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : undefined })
    if (url === '/auth/csrf') return json({ token: 'csrf' })
    if (url.endsWith('/pluggy-integration/automation') && method === 'GET') return api.overview === 'fail' ? json({ title: 'Falha' }, 500) : json(api.overview ?? overview())
    if (url.endsWith('/pluggy-integration/automation') && method === 'PUT') return json({})
    if (method === 'PUT' && (url.endsWith('/category') || url.endsWith('/expense-nature'))) return json({})
    if (url.includes('/automation/runs')) { const runs = api.runs ?? [syncRun(), catRun()]; return json({ items: runs, total: runs.length, page: 1, pageSize: 10 }) }
    if (url.includes('/classification-review')) return json(api.review ?? review([reviewItem()]))
    if (url.includes('/pluggy-integration/connections')) return json([{ id: 'c1', label: 'Itaú PF', itemReference: 'item', institution: 'MeuPluggy', institutionType: null, isOpenFinance: null, state: 'Healthy', status: null, executionStatus: null, lastUpdatedAt: '2026-10-29T09:00:00Z', consentExpiresAt: null, errorCode: null, errorMessage: null, integrationError: null, createdAt: '2026-09-01T10:00:00Z' }])
    if (url.endsWith('/pluggy-integration/links')) return json([{ id: 'l1', connectionId: 'c1', kind: 'Bank', displayName: 'Itaú corrente', status: 'Active', lastSyncedAt: '2026-10-29T10:31:00Z' }, { id: 'l2', connectionId: 'c1', kind: 'Card', displayName: 'Itaú Visa', status: 'Active', lastSyncedAt: null }])
    if (url.endsWith('/pluggy-integration/sync-status')) return json({ now: '2026-10-30T12:00:00Z', auto: { enabled: true, intervalHours: 24, windowStartHour: 0, windowEndHour: 24, maxConnectionsPerRun: 5, pollIntervalMinutes: 15 }, connections: [] })
    if (url.endsWith('/categories') || url.includes('/categories?')) return json([{ id: 'cat1', name: 'Livros', type: 'Expense', isActive: true, isAvailable: true, createdAt: '2026-01-01T00:00:00Z', path: 'Livros' }])
    return json({}, 404)
  }))
  return calls
}

describe('Central de automação', () => {
  it('abre nas conexões com instituição, estado, contas e cartões vinculados e diferencia cadastrada de atualizada', async () => {
    mockApi(); render(<AutomationCenter workspaceId="w" />)

    const connection = await screen.findByRole('listitem', { name: 'Conexão Itaú PF' })
    expect(within(connection).getByText('Ativa', { selector: '.badge' })).toBeTruthy()
    expect(text(within(connection).getByText('Cadastrada em').nextElementSibling)).toBe('01/09/2026')
    expect(text(within(connection).getByText('Última atualização pela instituição').nextElementSibling)).toBe('29/10/2026')
    const links = within(within(connection).getByRole('list', { name: 'Contas e cartões de Itaú PF' })).getAllByRole('listitem')
    expect(links.map(item => text(item))).toEqual([expect.stringContaining('Itaú corrente'), expect.stringContaining('Itaú Visa')])
    expect(text(links[1])).toContain('ainda não sincronizada')
    expect(screen.getByRole('form', { name: 'Cadastrar Item da Pluggy' })).toBeTruthy() // um único formulário de cadastro
    expect(screen.queryByText('Desconectar')).toBeNull() // sem ação inventada: o encerramento é no Meu Pluggy
    expect(screen.getByText(/Para encerrar uma conexão, use o Meu Pluggy/)).toBeTruthy()
  })

  it('mostra que a integração está indisponível, sem consultar nada', async () => {
    const calls = mockApi({ overview: overview({ server: { ...overview().server, integrationAvailable: false } }) })
    render(<AutomationCenter workspaceId="w" />)
    expect(await screen.findByText(/não está disponível neste ambiente/)).toBeTruthy()
    expect(calls.some(call => call.url.includes('/links'))).toBe(false)
  })

  it('mostra carregamento e erro com nova tentativa', async () => {
    mockApi({ overview: 'fail' })
    render(<AutomationCenter workspaceId="w" />)
    expect(screen.getByText('Carregando a central de automação…')).toBeTruthy()
    expect(await screen.findByRole('alert')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Tentar novamente' })).toBeTruthy()
  })

  it('o cartão de sincronização mostra estado, frequência, próxima execução real e última execução, e pausa só as agendadas', async () => {
    const calls = mockApi(); render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Automações' }))

    const card = await screen.findByRole('region', { name: 'Sincronização bancária' })
    expect(within(card).getByText('Ativa', { selector: '.badge' })).toBeTruthy()
    expect(text(within(card).getByText('Frequência').nextElementSibling)).toBe('a cada 24 h (padrão do servidor)')
    expect(text(within(card).getByText('Próxima execução').nextElementSibling)).toBe('30/10/2026 07:30')
    expect(text(within(card).getByLabelText('Última execução'))).toContain('Concluída')
    expect(text(within(card).getByLabelText('Última execução'))).toContain('Lidas 30 · novas 12 · atualizadas 3 · sem mudança 15')
    expect(within(card).getByText(/não interrompe uma sincronização já iniciada e não impede "Sincronizar agora"/)).toBeTruthy()

    fireEvent.click(within(card).getByRole('button', { name: 'Pausar execuções agendadas' }))
    await waitFor(() => expect(calls.some(call => call.method === 'PUT' && call.body === JSON.stringify({ syncPaused: true }))).toBe(true))
    expect(await screen.findByText(/Uma sincronização que já começou termina normalmente/)).toBeTruthy()
  })

  it('só oferece os intervalos que o servidor executa e salva a escolha', async () => {
    const calls = mockApi(); render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Automações' }))
    const select = await screen.findByLabelText('Intervalo entre sincronizações') as HTMLSelectElement
    expect([...select.options].map(option => option.value)).toEqual(['6', '12', '24'])
    fireEvent.change(select, { target: { value: '12' } })
    await waitFor(() => expect(calls.some(call => call.method === 'PUT' && call.body === JSON.stringify({ syncIntervalHours: 12 }))).toBe(true))
  })

  it('sem agendador no servidor, pausado ou sem conexão, não inventa uma próxima execução', async () => {
    const base = overview()
    mockApi({ overview: overview({ server: { ...base.server, schedulerEnabled: false }, sync: { ...base.sync, state: 'SchedulerOff', nextRunAt: null } }) })
    render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Automações' }))
    const card = await screen.findByRole('region', { name: 'Sincronização bancária' })
    expect(text(within(card).getByText('Próxima execução').nextElementSibling)).toContain('o agendamento está desligado no servidor')
    expect(within(card).getByText('Agendamento desligado no servidor', { selector: '.badge' })).toBeTruthy()
  })

  it('execução pausada explica por que não há próxima execução e oferece retomar', async () => {
    const base = overview()
    mockApi({ overview: overview({ settings: { ...base.settings, syncPaused: true }, sync: { ...base.sync, state: 'Paused', nextRunAt: null } }) })
    render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Automações' }))
    const card = await screen.findByRole('region', { name: 'Sincronização bancária' })
    expect(text(within(card).getByText('Próxima execução').nextElementSibling)).toContain('pausadas')
    expect(within(card).getByRole('button', { name: 'Retomar execuções agendadas' })).toBeTruthy()
  })

  it('categorização: ligar após a sincronização exige confirmação explícita; cancelar não grava', async () => {
    const calls = mockApi(); render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Automações' }))
    const card = await screen.findByRole('region', { name: 'Categorização com IA' })
    expect(text(within(card).getByText('Lançamentos elegíveis (orçamento inteiro)').nextElementSibling)).toBe('9')
    expect(text(within(card).getByText('Após cada sincronização').nextElementSibling)).toBe('Desligada')
    const checkbox = within(card).getByRole('checkbox', { name: /Categorizar com IA após cada sincronização/ })

    vi.spyOn(window, 'confirm').mockReturnValueOnce(false)
    fireEvent.click(checkbox)
    expect(calls.some(call => call.method === 'PUT')).toBe(false)

    vi.spyOn(window, 'confirm').mockReturnValueOnce(true)
    fireEvent.click(checkbox)
    await waitFor(() => expect(calls.some(call => call.method === 'PUT' && call.body === JSON.stringify({ categorizeAfterSync: true, confirmExternal: true }))).toBe(true))
  })

  it('com o agente de IA indisponível, diz por quê, desabilita a IA e mantém a categorização local', async () => {
    const base = overview()
    mockApi({ overview: overview({ server: { ...base.server, ai: { enabled: false, available: false, model: null, reason: 'AiNotConfigured' } }, categorization: { ...base.categorization, state: 'AiUnavailable' } }) })
    render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Automações' }))
    const card = await screen.findByRole('region', { name: 'Categorização com IA' })
    expect(within(card).getByText('Agente indisponível', { selector: '.badge' })).toBeTruthy()
    expect(within(card).getByText(/O agente de IA não está configurado neste ambiente/)).toBeTruthy()
    expect((within(card).getByRole('button', { name: 'Categorizar com IA…' }) as HTMLButtonElement).disabled).toBe(true)
    expect((within(card).getByRole('checkbox') as HTMLInputElement).disabled).toBe(true)
    expect((within(card).getByRole('button', { name: 'Categorizar sem IA' }) as HTMLButtonElement).disabled).toBe(false)
  })

  it('o histórico lista as duas automações separadas, sem somar contagens, e filtra por resultado', async () => {
    const calls = mockApi(); render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Histórico' }))

    const items = await screen.findAllByRole('listitem', { name: undefined })
    const sync = items.find(item => text(item).includes('Sincronização bancária'))!
    const categorization = items.find(item => text(item).includes('Categorização com IA'))!
    expect(text(sync)).toContain('Lidas 30 · novas 12')
    expect(text(sync)).not.toContain('pendentes')
    expect(text(categorization)).toContain('Elegíveis 9 · classificadas sem IA 2 · pela IA 5 · pendentes 2')
    expect(text(categorization)).toContain('Concluída com pendências')
    expect(text(categorization)).toContain('Após a sincronização')

    fireEvent.change(screen.getByLabelText('Resultado'), { target: { value: 'Failed' } })
    await waitFor(() => expect(calls.some(call => call.url.includes('state=Failed'))).toBe(true))
    fireEvent.change(screen.getByRole('combobox', { name: 'Automação' }), { target: { value: 'Sync' } })
    await waitFor(() => expect(calls.some(call => call.url.includes('kind=Sync') && call.url.includes('state=Failed'))).toBe(true))
  })

  it('execução interrompida aparece como falha com o motivo em frase, sem término inventado', async () => {
    mockApi({ runs: [catRun({ state: 'Failed', interrupted: true, finishedAt: null, durationSeconds: null, failureCode: 'Interrupted', categorization: null })] })
    render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Histórico' }))
    expect(await screen.findByText(/A execução foi interrompida \(o servidor parou ou reiniciou\)/)).toBeTruthy()
    expect(screen.getByText('Falhou', { selector: '.badge' })).toBeTruthy()
    expect(screen.getByText('Não registrado')).toBeTruthy()
  })

  it('histórico vazio explica que as execuções aparecem quando acontecerem; período invertido é recusado na tela', async () => {
    const calls = mockApi({ runs: [] }); render(<AutomationCenter workspaceId="w" />)
    fireEvent.click(await screen.findByRole('tab', { name: 'Histórico' }))
    expect(await screen.findByText(/Nenhuma execução registrada ainda/)).toBeTruthy()
    fireEvent.change(screen.getByLabelText('De'), { target: { value: '2026-10-20' } })
    fireEvent.change(screen.getByLabelText('Até'), { target: { value: '2026-10-10' } })
    expect(await screen.findByText('O início do período não pode ser posterior ao fim.')).toBeTruthy()
    expect(calls.filter(call => call.url.includes('from=2026-10-20') && call.url.includes('to=2026-10-10'))).toHaveLength(0)
  })

  it('a revisão inclui a compra de cartão importada, mostra o alcance e salva categoria e tipo só desta compra', async () => {
    const calls = mockApi({ review: review([reviewItem()], { unclassifiableCount: 2 }) })
    render(<AutomationCenter workspaceId="w" entry={{ key: 1, tab: 'automations', review: { kind: 'nature', from: '2026-10-01', to: '2026-10-31' } }} />)

    const item = (await screen.findByText('Livraria Online')).closest('li')!
    expect(text(item)).toContain('Cartão Itaú Visa')
    expect(text(item)).toContain('importado do banco')
    expect(text(item)).toContain('Alcance: Esta compra (as parcelas herdam o tipo)')
    expect(screen.getByText(/2 despesa\(s\) do período \(encargos e estornos de fatura/)).toBeTruthy()
    expect(screen.getByText(/Período de 01\/10\/2026 a 31\/10\/2026: 1 despesa\(s\)/)).toBeTruthy()

    fireEvent.change(within(item).getByLabelText('Tipo de despesa'), { target: { value: 'Variable' } })
    fireEvent.change(within(item).getByLabelText('Categoria'), { target: { value: 'cat1' } })
    fireEvent.click(within(item).getByRole('button', { name: 'Salvar classificação' }))

    await waitFor(() => expect(calls.some(call => call.method === 'PUT' && call.url.endsWith('/credit-cards/card1/purchases/p1/category') && call.body === JSON.stringify({ categoryId: 'cat1' }))).toBe(true))
    await waitFor(() => expect(calls.some(call => call.method === 'PUT' && call.url.endsWith('/credit-cards/card1/purchases/p1/expense-nature'))).toBe(true))
    expect(await screen.findByText('Classificação de "Livraria Online" salva.')).toBeTruthy()
  })

  it('previsão e lista por categoria: previsão é editada na origem, e a lista sem categoria diz que vale para o orçamento inteiro', async () => {
    mockApi({ review: review([reviewItem({ action: 'Planned', cardId: null, canChangeCategory: false, canChangeNature: false, description: 'Aluguel previsto', scope: 'Esta previsão (editada em Previsões)', cardName: null, accountName: 'Conta A' })]) })
    const view = render(<AutomationCenter workspaceId="w" entry={{ key: 1, tab: 'automations', review: { kind: 'nature' } }} />)
    const item = (await screen.findByText('Aluguel previsto')).closest('li')!
    expect(text(item)).toContain('editada na tela de origem')
    expect(within(item).queryByRole('button', { name: 'Salvar classificação' })).toBeNull()
    view.unmount()

    mockApi({ review: review([reviewItem({ description: 'Loja Zeta' })], { kind: 'category', wholeBudget: true, from: null, to: null }) })
    render(<AutomationCenter workspaceId="w" entry={{ key: 2, tab: 'automations', review: { kind: 'category' } }} />)
    expect(await screen.findByText(/Orçamento inteiro \(sem recorte de mês\): 1 registro\(s\) importado\(s\) sem categoria segura\./)).toBeTruthy()
  })

  it('lista de revisão vazia explica que nada precisa de classificação', async () => {
    mockApi({ review: review([]) })
    render(<AutomationCenter workspaceId="w" entry={{ key: 1, tab: 'automations', review: { kind: 'nature' } }} />)
    expect(await screen.findByText('Nenhuma despesa classificável sem tipo neste período.')).toBeTruthy()
  })

  it('falha ao salvar a classificação mostra a mensagem do servidor na própria linha', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input); calls.push(`${init?.method ?? 'GET'} ${url}`)
      if (url === '/auth/csrf') return json({ token: 'csrf' })
      if (url.endsWith('/pluggy-integration/automation')) return json(overview())
      if (url.includes('/classification-review')) return json(review([reviewItem()]))
      if (url.includes('/categories')) return json([{ id: 'cat1', name: 'Livros', type: 'Expense', isActive: true, isAvailable: true, createdAt: '2026-01-01T00:00:00Z', path: 'Livros' }])
      if (url.endsWith('/category')) return json({ title: 'A compra pertence a uma fatura oficial fechada: a categoria não pode ser alterada.' }, 409)
      return json({}, 404)
    }))
    render(<AutomationCenter workspaceId="w" entry={{ key: 1, tab: 'automations', review: { kind: 'nature' } }} />)
    const item = (await screen.findByText('Livraria Online')).closest('li')!
    fireEvent.change(await within(item).findByLabelText('Categoria'), { target: { value: 'cat1' } })
    fireEvent.click(within(item).getByRole('button', { name: 'Salvar classificação' }))
    expect((await within(item).findByRole('alert')).textContent).toContain('fatura oficial fechada')
  })
})
