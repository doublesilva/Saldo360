import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { MonthlyPlan, PlanHistoryEntry, SixMonthPlan, WorkingDays } from '../api/monthlyPlan'
import { PlanningHub } from './PlanningHub'
import { emptyMonthlyPlan, monthlyPlan, sixMonths, workingDays } from './monthlyPlanFixtures'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
const categories = [
  { id: 'cat-f', name: 'Alimentação', type: 'Expense', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
  { id: 'cat-h', name: 'Moradia', type: 'Expense', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
  { id: 'cat-r', name: 'Receitas', type: 'Income', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
]

interface Stubs { plan?: MonthlyPlan | ((url: URL) => MonthlyPlan); six?: SixMonthPlan; days?: WorkingDays; history?: PlanHistoryEntry[]; write?: (url: URL, init: RequestInit) => Promise<Response> | undefined }
const stub = (stubs: Stubs = {}) => {
  const fetchMock = vi.fn((input: string, init: RequestInit = {}) => {
    const url = new URL(String(input), 'http://x')
    const method = init.method ?? 'GET'
    if (url.pathname === '/auth/csrf') return json({ token: 'csrf' })
    if (url.pathname.endsWith('/categories')) return json(categories)
    if (method !== 'GET') return stubs.write?.(url, init) ?? json({ id: 'new', competence: '2026-10', linesCopied: 0 }, method === 'POST' ? 201 : 200)
    if (url.pathname.endsWith('/monthly-plan/six-months')) return json(stubs.six ?? sixMonths)
    if (url.pathname.endsWith('/monthly-plan/working-days')) return json(stubs.days ?? workingDays)
    if (url.pathname.endsWith('/monthly-plan/history')) return json(stubs.history ?? [])
    if (url.pathname.endsWith('/monthly-plan/hourly-income')) return json({ competence: '2026-10', hourlyRate: url.searchParams.get('hourlyRate'), hoursPerDay: url.searchParams.get('hoursPerDay'), billableDays: Number(url.searchParams.get('billableDays')), businessDays: 22, amount: '24000.00' })
    if (url.pathname.endsWith('/monthly-plan')) return json(typeof stubs.plan === 'function' ? stubs.plan(url) : stubs.plan ?? monthlyPlan)
    return json({}, 404)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}
const calls = (fetchMock: ReturnType<typeof stub>, method: string, suffix: string) =>
  fetchMock.mock.calls.filter(([input, init]) => (init?.method ?? 'GET') === method && new URL(String(input), 'http://x').pathname.endsWith(suffix))
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body))
const planQueries = (fetchMock: ReturnType<typeof stub>) => calls(fetchMock, 'GET', '/monthly-plan').map(([input]) => new URL(String(input), 'http://x').searchParams)

const open = async (stubs: Stubs = {}) => {
  const fetchMock = stub(stubs)
  render(<PlanningHub workspaceId="w" />)
  await screen.findByRole('article', { name: 'Receitas: planejado, projetado e realizado' })
  return fetchMock
}

describe('PlanningHub — plano mensal', () => {
  it('abre no plano mensal da conta principal com planejado, projetado, realizado e desvios vindos do backend', async () => {
    const fetchMock = await open()

    const income = screen.getByRole('article', { name: 'Receitas: planejado, projetado e realizado' })
    expect(text(income)).toContain('R$ 7.500,50')
    expect(text(income)).toContain('Desvio do realizado')
    expect(text(income)).toContain('Planejamento mensal')
    const consumption = screen.getByRole('article', { name: 'Despesas: planejado, projetado e realizado' })
    expect(text(consumption)).toMatch(/Planejado\s*R\$ 4\.500,00/)
    expect(text(consumption)).toMatch(/Efetivo\s*R\$ 1\.200,00/)
    const query = planQueries(fetchMock)[0]
    expect(query.get('scope')).toBe('primary')
    expect(query.get('competence')).toMatch(/^\d{4}-\d{2}$/)
    expect(screen.getByRole('tab', { name: 'Plano mensal' }).getAttribute('aria-selected')).toBe('true')
  })

  it('mostra “Não definido” e a pendência quando o mês não tem planejamento, sem zero fictício', async () => {
    await open({ plan: emptyMonthlyPlan })

    expect(text(screen.getByRole('article', { name: 'Receitas: planejado, projetado e realizado' }))).toMatch(/Planejado\s*Não definido/)
    expect(text(screen.getByRole('article', { name: 'Despesas: planejado, projetado e realizado' }))).toMatch(/Planejado\s*Não definido/)
    expect(text(screen.getByRole('article', { name: 'Resultado e reservas planejados' }))).toContain('Não definido')
    expect(text(screen.getByRole('region', { name: 'Pendências do planejamento' }))).toContain('Este mês ainda não tem planejamento')
    expect(screen.getByText('Nenhuma linha neste mês para a conta selecionada.')).toBeTruthy()
    expect(screen.getByText('Nenhum limite definido para este mês.')).toBeTruthy()
  })

  it('lista linhas com valor original e vigente, conta PF/PJ e categoria, e limites com a execução', async () => {
    await open()

    const lines = within(screen.getByRole('region', { name: 'Receitas, despesas e transferências planejadas' }))
    const rent = lines.getByRole('row', { name: /Aluguel/ })
    expect(text(rent)).toContain('Despesa · Fixa')
    expect(text(rent)).toContain('Conta A · PF')
    expect(text(rent)).toContain('Moradia')
    expect(text(rent)).toContain('R$ 1.800,00')
    expect(text(rent)).toContain('R$ 2.000,00')
    expect(text(rent)).toContain('+R$ 200,00')
    expect(text(lines.getByRole('row', { name: /Salário/ }))).toContain('05/10/2026')
    const limits = within(screen.getByRole('region', { name: 'Limites por categoria' }))
    const food = limits.getByRole('row', { name: /Alimentação/ })
    expect(text(food)).toContain('R$ 2.500,00')
    expect(text(food)).toContain('original R$ 2.000,00')
    expect(text(food)).toContain('R$ 600,00')
    expect(text(food)).toContain('R$ 1.800,00')
    expect(text(food)).toContain('Dentro do limite')
  })

  it('mostra a meta de cartões existente como somente leitura, independente das linhas e dos limites', async () => {
    await open()

    const cards = screen.getByRole('article', { name: 'Meta dos cartões' })
    expect(text(cards)).toContain('R$ 5.000,00')
    expect(text(cards)).toContain('independente das linhas e dos limites')
    expect(within(cards).queryByRole('button')).toBeNull()
  })

  it('troca a conta sem recarregar a página e consulta a conta escolhida e o consolidado', async () => {
    const fetchMock = await open()

    fireEvent.change(screen.getByLabelText('Conta do planejamento'), { target: { value: 'account:pj' } })
    await waitFor(() => expect(planQueries(fetchMock).some(query => query.get('scope') === 'account' && query.get('accountId') === 'pj')).toBe(true))
    fireEvent.change(screen.getByLabelText('Conta do planejamento'), { target: { value: 'all' } })
    await waitFor(() => expect(planQueries(fetchMock).some(query => query.get('scope') === 'all')).toBe(true))
    const options = within(screen.getByLabelText('Conta do planejamento')).getAllByRole('option').map(option => text(option))
    expect(options).toContain('Conta B · PJ')
  })

  it('muda a competência e consulta o mês escolhido', async () => {
    const fetchMock = await open()

    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2026-12' } })

    await waitFor(() => expect(planQueries(fetchMock).some(query => query.get('competence') === '2026-12')).toBe(true))
  })

  it('mostra a limitação dos limites quando a conta não é o consolidado e as pendências do backend', async () => {
    await open({ plan: { ...monthlyPlan, planned: { ...monthlyPlan.planned, limitsNotAttributable: true, expense: null }, pendencies: [{ code: 'LimitsNotAttributable', count: 1 }, { code: 'MovementsInReview', count: 3 }] } })

    expect(text(screen.getByRole('region', { name: 'Pendências do planejamento' }))).toContain('escolha “Todas as contas”')
    expect(text(screen.getByRole('region', { name: 'Pendências do planejamento' }))).toContain('movimentações em revisão')
  })

  it('exibe uma falha de carregamento com nova tentativa', async () => {
    let attempts = 0
    stub({ plan: () => { attempts += 1; throw new Error('nunca chega') } })
    vi.stubGlobal('fetch', vi.fn((input: string) => {
      const url = new URL(String(input), 'http://x')
      if (url.pathname.endsWith('/categories')) return json(categories)
      attempts += 1
      return attempts === 1 ? json({ title: 'Falha temporária.' }, 500) : json(monthlyPlan)
    }))
    render(<PlanningHub workspaceId="w" />)

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('Falha temporária')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e realizado' })
  })
})

describe('PlanningHub — edição do orçamento', () => {
  it('adiciona uma receita planejada com o valor exato em texto e atualiza a visão', async () => {
    const fetchMock = await open()
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar receita' }))
    const form = screen.getByRole('dialog', { name: /Adicionar receita planejada/ })

    fireEvent.change(within(form).getByLabelText('Descrição'), { target: { value: 'Consultoria' } })
    fireEvent.change(within(form).getByLabelText('Conta'), { target: { value: 'pj' } })
    fireEvent.change(within(form).getByLabelText('Valor planejado'), { target: { value: '9.999,05' } })
    fireEvent.change(within(form).getByLabelText('Data esperada (opcional)'), { target: { value: '2026-10-10' } })
    const before = planQueries(fetchMock).length
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls(fetchMock, 'POST', '/monthly-plan/lines')).toHaveLength(1))
    const sent = bodyOf(calls(fetchMock, 'POST', '/monthly-plan/lines')[0])
    expect(sent).toMatchObject({ kind: 'Income', nature: 'Recurring', description: 'Consultoria', accountId: 'pj', amount: '9999.05', expectedDate: '2026-10-10', categoryId: null })
    expect((calls(fetchMock, 'POST', '/monthly-plan/lines')[0][1] as RequestInit).headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf' })
    await waitFor(() => expect(planQueries(fetchMock).length).toBeGreaterThan(before))
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByRole('status', { name: '' }) || true).toBeTruthy()
  })

  it('não envia valor inválido, zero a mais ou descrição vazia', async () => {
    const fetchMock = await open()
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar despesa' }))
    const form = screen.getByRole('dialog', { name: /Adicionar despesa planejada/ })

    fireEvent.change(within(form).getByLabelText('Descrição'), { target: { value: 'Mercado' } })
    fireEvent.change(within(form).getByLabelText('Valor planejado'), { target: { value: '10,005' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar' }))
    expect((await within(form).findByRole('alert')).textContent).toContain('duas casas decimais')
    fireEvent.change(within(form).getByLabelText('Valor planejado'), { target: { value: '' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(within(form).getByRole('alert').textContent).toContain('Informe o valor'))
    expect(calls(fetchMock, 'POST', '/monthly-plan/lines')).toHaveLength(0)
  })

  it('reserva só existe para despesa e avisa que não é saída bancária', async () => {
    await open()
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar despesa' }))
    const expense = screen.getByRole('dialog', { name: /Adicionar despesa planejada/ })
    expect(within(within(expense).getByLabelText('Natureza')).getAllByRole('option').map(option => text(option))).toContain('Reserva / provisão')
    fireEvent.change(within(expense).getByLabelText('Natureza'), { target: { value: 'Reserve' } })
    expect(text(expense)).toContain('não são saída bancária')
    fireEvent.click(within(expense).getByRole('button', { name: 'Cancelar' }))

    fireEvent.click(screen.getByRole('button', { name: 'Adicionar receita' }))
    const income = screen.getByRole('dialog', { name: /Adicionar receita planejada/ })
    expect(within(within(income).getByLabelText('Natureza')).getAllByRole('option').map(option => text(option))).not.toContain('Reserva / provisão')
  })

  it('calcula a receita por hora no backend: pré-visualiza o valor e envia os parâmetros sem valor próprio', async () => {
    const fetchMock = await open()
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar receita' }))
    const form = screen.getByRole('dialog', { name: /Adicionar receita planejada/ })

    fireEvent.change(within(form).getByLabelText('Descrição'), { target: { value: 'Serviços PJ' } })
    fireEvent.click(within(form).getByLabelText('Por hora trabalhada'))
    fireEvent.change(within(form).getByLabelText('Valor por hora'), { target: { value: '150' } })
    fireEvent.change(within(form).getByLabelText('Horas por dia'), { target: { value: '8' } })
    fireEvent.change(within(form).getByLabelText('Dias faturáveis'), { target: { value: '20' } })

    await waitFor(() => expect(text(form)).toContain('Valor esperado do mês: R$ 24.000,00'))
    expect(text(form)).toContain('22 dia(s) útil(eis)')
    const preview = calls(fetchMock, 'GET', '/monthly-plan/hourly-income').at(-1)!
    const query = new URL(String(preview[0]), 'http://x').searchParams
    expect([query.get('hourlyRate'), query.get('hoursPerDay'), query.get('billableDays')]).toEqual(['150.00'.replace('.00', ''), '8', '20'])
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls(fetchMock, 'POST', '/monthly-plan/lines')).toHaveLength(1))
    const sent = bodyOf(calls(fetchMock, 'POST', '/monthly-plan/lines')[0])
    expect(sent).toMatchObject({ kind: 'Income', hourlyRate: '150', hoursPerDay: '8', billableDays: 20 })
    expect(sent.amount).toBeUndefined()
  })

  it('revisa uma linha preservando o histórico com o motivo informado', async () => {
    const fetchMock = await open()
    const row = screen.getByRole('row', { name: /Aluguel/ })

    fireEvent.click(within(row).getByRole('button', { name: 'Revisar' }))
    const form = screen.getByRole('dialog', { name: /Revisar despesa planejada/ })
    expect((within(form).getByLabelText('Valor planejado') as HTMLInputElement).value).toBe('2000,00')
    fireEvent.change(within(form).getByLabelText('Valor planejado'), { target: { value: '2.150,00' } })
    fireEvent.change(within(form).getByLabelText('Motivo da revisão (opcional)'), { target: { value: 'Reajuste anual' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls(fetchMock, 'PUT', '/monthly-plan/lines/l2')).toHaveLength(1))
    expect(bodyOf(calls(fetchMock, 'PUT', '/monthly-plan/lines/l2')[0])).toMatchObject({ kind: 'Expense', amount: '2150.00', note: 'Reajuste anual', categoryId: 'cat-h' })
  })

  it('remove uma linha só depois da confirmação', async () => {
    const fetchMock = await open()
    const confirm = vi.fn((_message?: string) => false)
    vi.stubGlobal('confirm', confirm)
    const row = () => screen.getByRole('row', { name: /Aluguel/ })

    fireEvent.click(within(row()).getByRole('button', { name: 'Remover' }))
    expect(calls(fetchMock, 'DELETE', '/monthly-plan/lines/l2')).toHaveLength(0)
    confirm.mockReturnValue(true)
    fireEvent.click(within(row()).getByRole('button', { name: 'Remover' }))

    await waitFor(() => expect(calls(fetchMock, 'DELETE', '/monthly-plan/lines/l2')).toHaveLength(1))
    expect(confirm.mock.calls[0][0]).toContain('histórico será preservado')
  })

  it('define o limite de uma categoria de despesa e recusa valor negativo', async () => {
    const fetchMock = await open()
    fireEvent.click(screen.getByRole('button', { name: 'Definir limite por categoria' }))
    const form = screen.getByRole('dialog', { name: /Definir limite por categoria/ })

    expect(within(within(form).getByLabelText('Categoria de despesa')).getAllByRole('option').map(option => text(option))).toEqual(['Selecione', 'Alimentação', 'Moradia'])
    fireEvent.change(within(form).getByLabelText('Categoria de despesa'), { target: { value: 'cat-h' } })
    fireEvent.change(within(form).getByLabelText('Limite mensal'), { target: { value: '-10' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar limite' }))
    expect((await within(form).findByRole('alert')).textContent).toContain('não pode ser negativo')
    fireEvent.change(within(form).getByLabelText('Limite mensal'), { target: { value: '2.500,00' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar limite' }))

    await waitFor(() => expect(calls(fetchMock, 'PUT', '/monthly-plan/category-limits')).toHaveLength(1))
    expect(bodyOf(calls(fetchMock, 'PUT', '/monthly-plan/category-limits')[0])).toMatchObject({ categoryId: 'cat-h', amount: '2500.00' })
  })

  it('remove um limite só depois da confirmação', async () => {
    const fetchMock = await open()
    vi.stubGlobal('confirm', vi.fn(() => true))

    fireEvent.click(within(screen.getByRole('row', { name: /Alimentação/ })).getByRole('button', { name: 'Remover' }))

    await waitFor(() => expect(calls(fetchMock, 'DELETE', '/monthly-plan/category-limits/cat-f')).toHaveLength(1))
    expect(new URL(String(calls(fetchMock, 'DELETE', '/monthly-plan/category-limits/cat-f')[0][0]), 'http://x').searchParams.get('competence')).toMatch(/^\d{4}-\d{2}$/)
  })

  it('copia o mês anterior somente depois de uma confirmação explícita e informa o resultado', async () => {
    const fetchMock = await open({ write: (url) => url.pathname.endsWith('/copy') ? json({ fromCompetence: '2026-09', toCompetence: '2026-10', linesCopied: 3, linesSkipped: 1, limitsCopied: 2, limitsSkipped: 0, recalculatedHourlyLines: ['x'] }) : undefined })

    fireEvent.click(screen.getByRole('button', { name: 'Copiar mês anterior' }))
    const dialog = screen.getByRole('dialog', { name: /Copiar planejamento de/ })
    expect(text(dialog)).toContain('não são copiados')
    expect(calls(fetchMock, 'POST', '/monthly-plan/copy')).toHaveLength(0)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Confirmar cópia' }))

    await waitFor(() => expect(calls(fetchMock, 'POST', '/monthly-plan/copy')).toHaveLength(1))
    const sent = bodyOf(calls(fetchMock, 'POST', '/monthly-plan/copy')[0])
    expect(sent.confirm).toBe(true)
    expect(sent.toCompetence).toMatch(/^\d{4}-\d{2}$/)
    expect(sent.fromCompetence < sent.toCompetence).toBe(true)
    const notice = await screen.findByText(/Planejamento copiado: 3 linha\(s\) e 2 limite\(s\)/)
    expect(notice.textContent).toContain('1 receita(s) por hora')
  })

  it('cancelar a cópia não envia nada', async () => {
    const fetchMock = await open()
    fireEvent.click(screen.getByRole('button', { name: 'Copiar mês anterior' }))
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancelar' }))

    expect(screen.queryByRole('dialog')).toBeNull()
    expect(calls(fetchMock, 'POST', '/monthly-plan/copy')).toHaveLength(0)
  })

  it('mostra o conflito da cópia (destino já planejado) sem fechar o diálogo', async () => {
    await open({ write: (url) => url.pathname.endsWith('/copy') ? json({ title: 'A competência de destino já tem linhas de planejamento; nada foi copiado.' }, 409) : undefined })
    fireEvent.click(screen.getByRole('button', { name: 'Copiar mês anterior' }))
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Confirmar cópia' }))

    expect((await screen.findByRole('alert')).textContent).toContain('já tem linhas de planejamento')
    expect(screen.getByRole('dialog', { name: /Copiar planejamento de/ })).toBeTruthy()
  })

  it('consulta as alterações anteriores do mês e de um item', async () => {
    const entry: PlanHistoryEntry = { id: 'h1', competence: '2026-10', subject: 'Line', subjectId: 'l2', action: 'Revised', label: 'Aluguel', previousAmount: '1800.00', newAmount: '2000.00', note: 'Reajuste', userId: 'u', at: '2026-10-02T15:00:00Z' }
    const fetchMock = await open({ history: [entry] })

    fireEvent.click(within(screen.getByRole('row', { name: /Aluguel/ })).getByRole('button', { name: 'Histórico' }))

    const panel = await screen.findByRole('dialog', { name: /Histórico · Aluguel/ })
    await waitFor(() => expect(text(panel)).toContain('Revisada'))
    expect(text(panel)).toContain('R$ 1.800,00 → R$ 2.000,00')
    expect(text(panel)).toContain('Reajuste')
    const query = new URL(String(calls(fetchMock, 'GET', '/monthly-plan/history').at(-1)![0]), 'http://x').searchParams
    expect(query.get('subjectId')).toBe('l2')
    fireEvent.click(within(panel).getByRole('button', { name: 'Fechar' }))
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('planeja uma transferência entre contas sem tratá-la como receita ou despesa', async () => {
    const fetchMock = await open()
    fireEvent.click(screen.getByRole('button', { name: 'Planejar transferência' }))
    const form = screen.getByRole('dialog', { name: /Adicionar transferência prevista/ })
    expect(text(form)).toContain('não é receita nem despesa consolidada')

    fireEvent.change(within(form).getByLabelText('Descrição'), { target: { value: 'Pró-labore' } })
    fireEvent.change(within(form).getByLabelText('Conta de origem'), { target: { value: 'pj' } })
    fireEvent.change(within(form).getByLabelText('Conta de destino'), { target: { value: 'pj' } })
    fireEvent.change(within(form).getByLabelText('Valor planejado'), { target: { value: '4000' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar' }))
    expect((await within(form).findByRole('alert')).textContent).toContain('devem ser diferentes')
    fireEvent.change(within(form).getByLabelText('Conta de destino'), { target: { value: 'pf' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls(fetchMock, 'POST', '/monthly-plan/lines')).toHaveLength(1))
    expect(bodyOf(calls(fetchMock, 'POST', '/monthly-plan/lines')[0])).toMatchObject({ kind: 'Transfer', nature: null, accountId: 'pj', destinationAccountId: 'pf', categoryId: null, amount: '4000' })
  })
})

describe('PlanningHub — seis meses', () => {
  const openSix = async (stubs: Stubs = {}) => {
    const fetchMock = stub(stubs)
    render(<PlanningHub workspaceId="w" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e realizado' })
    fireEvent.click(screen.getByRole('tab', { name: 'Seis meses' }))
    await screen.findByRole('table')
    return fetchMock
  }

  it('apresenta o mês e os cinco seguintes com planejado, projetado, realizado, obrigações, cobertura e risco', async () => {
    const fetchMock = await openSix()

    const table = screen.getByRole('table')
    expect(within(table).getAllByRole('columnheader')).toHaveLength(7)
    const rowText = (name: RegExp) => text(within(table).getByRole('row', { name }))
    expect(rowText(/Receitas · Planejadas/)).toContain('R$ 7.500,50')
    expect(rowText(/Receitas · Planejadas/)).toContain('Não definido')
    expect(rowText(/Despesas · Projetadas/)).toContain('R$ 5.000,00')
    expect(rowText(/Resultado · Projetado/)).toContain('-R$ 5.000,00')
    expect(rowText(/Obrigações · Conhecidas no mês/)).toContain('R$ 300,00')
    expect(rowText(/Cobertura · Das obrigações/)).toContain('Saldo insuficiente')
    expect(rowText(/Cobertura · Dos dados/)).toContain('Parcial')
    expect(rowText(/Risco do mês/)).toMatch(/Alto/)
    expect(calls(fetchMock, 'GET', '/monthly-plan/six-months')).toHaveLength(1)
  })

  it('identifica os meses de maior risco e explica o motivo, sem calcular nada no navegador', async () => {
    await openSix()

    expect(text(screen.getByRole('region', { name: 'Resumo dos seis meses' }))).toContain('Meses de maior risco: Dezembro de 2026')
    expect(text(screen.getByRole('region', { name: 'Resumo dos seis meses' }))).toContain('4 mês(es) sem planejamento')
    const reasons = text(screen.getByRole('region', { name: 'Motivos de risco' }))
    expect(reasons).toContain('Saldo projetado negativo na Agenda')
    expect(reasons).toContain('Despesa projetada acima da planejada')
    expect(reasons).toContain('Cobertura incompleta')
    expect(text(screen.getByText(/nenhuma receita ou despesa é inventada/))).toContain('O plano é meta')
  })

  it('compartilha a conta escolhida e consulta a competência inicial informada', async () => {
    const fetchMock = await openSix()

    fireEvent.change(screen.getByLabelText('Conta do planejamento'), { target: { value: 'all' } })
    await waitFor(() => expect(calls(fetchMock, 'GET', '/monthly-plan/six-months').some(([input]) => new URL(String(input), 'http://x').searchParams.get('scope') === 'all')).toBe(true))
    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2027-02' } })
    await waitFor(() => expect(calls(fetchMock, 'GET', '/monthly-plan/six-months').some(([input]) => new URL(String(input), 'http://x').searchParams.get('from') === '2027-02')).toBe(true))
  })

  it('sem nenhum risco informa isso claramente', async () => {
    await openSix({ six: { ...sixMonths, summary: { ...sixMonths.summary, highestRiskMonths: [], monthsWithRisk: 0 }, months: sixMonths.months.map(month => ({ ...month, risk: 'Low' as const, riskReasons: [] })) } })

    expect(text(screen.getByRole('region', { name: 'Resumo dos seis meses' }))).toContain('Nenhum mês com risco alto ou médio identificado')
    expect(screen.queryByRole('region', { name: 'Motivos de risco' })).toBeNull()
  })
})

describe('PlanningHub — calendário de dias úteis e cenários', () => {
  it('mostra a contagem de dias úteis do backend e os dias cadastrados', async () => {
    stub()
    render(<PlanningHub workspaceId="w" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e realizado' })
    fireEvent.click(screen.getByRole('tab', { name: 'Dias úteis' }))

    const counts = await screen.findByLabelText('Contagem de dias do mês')
    expect(text(counts)).toMatch(/Dias úteis\s*21/)
    expect(text(counts)).toContain('Dias de segunda a sexta22')
    expect(text(screen.getByText(/Nossa Senhora Aparecida/))).toContain('12/10/2026')
    expect(text(screen.getByText(/O calendário começa vazio/))).toContain('cadastre os feriados')
  })

  it('cadastra e remove um dia não trabalhado', async () => {
    const fetchMock = stub()
    render(<PlanningHub workspaceId="w" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e realizado' })
    fireEvent.click(screen.getByRole('tab', { name: 'Dias úteis' }))
    await screen.findByLabelText('Contagem de dias do mês')

    fireEvent.click(screen.getByRole('button', { name: 'Cadastrar dia' }))
    expect((await screen.findByRole('alert')).textContent).toContain('Informe a data')
    const form = screen.getByRole('form', { name: 'Cadastrar dia não trabalhado' })
    fireEvent.change(within(form).getByLabelText('Data'), { target: { value: '2026-10-15' } })
    fireEvent.change(within(form).getByLabelText('Tipo'), { target: { value: 'NonWorking' } })
    fireEvent.change(within(form).getByLabelText('Descrição'), { target: { value: 'Recesso' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Cadastrar dia' }))
    await waitFor(() => expect(calls(fetchMock, 'POST', '/monthly-plan/non-working-days')).toHaveLength(1))
    expect(bodyOf(calls(fetchMock, 'POST', '/monthly-plan/non-working-days')[0])).toEqual({ date: '2026-10-15', kind: 'NonWorking', description: 'Recesso' })

    fireEvent.click(await screen.findByRole('button', { name: 'Remover Nossa Senhora Aparecida' }))
    await waitFor(() => expect(calls(fetchMock, 'DELETE', '/monthly-plan/non-working-days/d1')).toHaveLength(1))
  })

  it('mantém os cenários de recuperação existentes numa aba do mesmo módulo', async () => {
    const fetchMock = vi.fn((input: string) => {
      const url = new URL(String(input), 'http://x')
      if (url.pathname.endsWith('/planning-scenarios')) return json([])
      if (url.pathname.endsWith('/accounts')) return json([])
      if (url.pathname.endsWith('/categories')) return json(categories)
      return json(monthlyPlan)
    })
    vi.stubGlobal('fetch', fetchMock)
    render(<PlanningHub workspaceId="w" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e realizado' })

    fireEvent.click(screen.getByRole('tab', { name: 'Cenários' }))

    expect(await screen.findByRole('heading', { name: 'Planejamento financeiro' })).toBeTruthy()
    expect(screen.queryByLabelText('Conta do planejamento')).toBeNull()
  })
})

describe('Valores exatos', () => {
  it('preserva centavos exatos de valores grandes, sem passar por ponto flutuante', async () => {
    await open({ plan: { ...monthlyPlan, planned: { ...monthlyPlan.planned, result: '29999999999999.97' } } })

    expect(text(screen.getByRole('article', { name: 'Resultado e reservas planejados' }))).toContain('R$ 29.999.999.999.999,97')
  })
})
