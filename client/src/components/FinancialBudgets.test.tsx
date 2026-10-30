import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { BudgetComposition, FinancialBudget, FinancialBudgetExecution, FinancialBudgetTarget, FinancialBudgetTargetExecution } from '../api/budgets'
import type { FinancialCategory } from '../api/categories'
import { FinancialBudgetsPage } from './FinancialBudgets'

/** R03: orçamento com metas de despesas fixas, todos os cartões e categorias. API simulada; os valores vêm prontos do servidor. */

const category = (id: string, name: string, extra: Partial<FinancialCategory> = {}): FinancialCategory => ({ id, name, type: 'Expense', isActive: true, createdAt: '', ...extra })
const moradia = category('cat-moradia', 'Moradia')
const energia = category('cat-energia', 'Energia', { parentCategoryId: moradia.id, parentName: 'Moradia', isAvailable: true, path: 'Moradia / Energia' })
const lazer = category('cat-lazer', 'Lazer')
const salario = category('cat-salario', 'Salário', { type: 'Income' })
const velha = category('cat-velha', 'Academia', { isActive: false })

const target = (id: string, groupingType: FinancialBudgetTarget['groupingType'], name: string, planned: string, cat?: FinancialCategory): FinancialBudgetTarget => ({
  id, name, plannedAmount: planned, groupingType, category: cat ? { id: cat.id, name: cat.name, isActive: cat.isActive, path: cat.path ?? cat.name } : null,
  isArchived: false, createdAt: '', updatedAt: '', archivedAt: null,
})

const fixedTarget = target('t-fixed', 'FixedExpenses', 'Despesas fixas', '600.00')
const cardsTarget = target('t-cards', 'AllCards', 'Todos os cartões', '5000.00')
const housingTarget = target('t-housing', 'Category', 'Moradia', '1000.00', moradia)
const budget: FinancialBudget = {
  id: 'budget-1', competence: '2026-10', description: null, isArchived: false, totalPlannedAmount: '1000.00', targets: [fixedTarget, cardsTarget, housingTarget],
  createdAt: '', updatedAt: '', archivedAt: null,
}

const run = (targetId: string, groupingType: FinancialBudgetTarget['groupingType'], planned: string, consumed: string, extra: Partial<FinancialBudgetTargetExecution> = {}): FinancialBudgetTargetExecution => ({
  targetId, groupingType, categoryId: null, planned, consumed, realized: consumed, creditCardCommitted: '0.00', committed: '0.00', projectedConsumption: consumed,
  available: '0.00', availableProjected: '0.00', exceededAmount: '0.00', exceededProjected: '0.00', consumptionPercentage: '0.00', projectedPercentage: '0.00',
  status: 'WithinBudget', projectedStatus: 'WithinBudget', alertMessage: 'O consumo está dentro do limite.',
  coverage: { estimatedCompetenceCount: 0, undefinedNatureCount: 0, undefinedNatureAmount: '0.00', noCards: false }, ...extra,
})

const execution = (targets: FinancialBudgetTargetExecution[]): FinancialBudgetExecution => ({
  budgetId: budget.id, competence: budget.competence, targets,
  categories: { planned: '1000.00', consumed: '300.00', realized: '300.00', committed: '0.00', projectedConsumption: '300.00', available: '700.00', availableProjected: '700.00', exceededAmount: '0.00', exceededProjected: '0.00',
    consumptionPercentage: '30.00', projectedPercentage: '30.00', status: 'WithinBudget', projectedStatus: 'WithinBudget', alertMessage: '', approachingCount: 0, reachedCount: 0, exceededCount: 0, targetCount: 1 },
})

const baseRuns = [
  run('t-fixed', 'FixedExpenses', '600.00', '540.00', { available: '60.00', availableProjected: '60.00', consumptionPercentage: '90.00', status: 'ApproachingLimit', projectedStatus: 'ApproachingLimit' }),
  run('t-cards', 'AllCards', '5000.00', '4200.00', { creditCardCommitted: '4200.00', realized: '0.00', available: '800.00', availableProjected: '800.00', consumptionPercentage: '84.00' }),
  run('t-housing', 'Category', '1000.00', '300.00', { categoryId: moradia.id, available: '700.00', availableProjected: '700.00', consumptionPercentage: '30.00' }),
]

const composition = (page: number, totalCount = 12): BudgetComposition => ({
  budgetId: budget.id, competence: budget.competence, targetId: 't-cards', groupingType: 'AllCards',
  amounts: { planned: '5000.00', consumed: '4200.00', realized: '0.00', creditCardCommitted: '4200.00', committed: '0.00', projectedConsumption: '4200.00', available: '800.00', availableProjected: '800.00', exceededAmount: '0.00', exceededProjected: '0.00', consumptionPercentage: '84.00', projectedPercentage: '84.00' },
  status: 'WithinBudget', coverage: { estimatedCompetenceCount: 1, undefinedNatureCount: 0, undefinedNatureAmount: '0.00', noCards: false }, page, pageSize: 10, totalCount,
  items: [
    { origin: 'CardInstallment', recordId: `r${page}`, date: '2026-10-05', amount: '300.00', state: 'CardLaunched', categoryId: lazer.id, categoryPath: 'Lazer', expenseNature: 'Fixed', description: `Streaming ${page}`, cardId: 'c1', cardName: 'Black', accountName: null, invoiceCompetence: '2026-10', competenceReliable: false },
    { origin: 'CardAdjustment', recordId: `a${page}`, date: '2026-10-06', amount: '-30.00', state: 'CardLaunched', categoryId: null, categoryPath: null, expenseNature: 'Undefined', description: 'Estorno', cardId: 'c1', cardName: 'Black', accountName: null, invoiceCompetence: '2026-10', competenceReliable: true },
  ],
  cards: [{ cardId: 'c1', cardName: 'Black', consumed: '3000.00', count: 4 }, { cardId: 'c2', cardName: 'Nubank', consumed: '1200.00', count: 2 }],
})

type Call = { url: string; init?: RequestInit }

function mockApi(options: { budgets?: FinancialBudget[]; runs?: FinancialBudgetTargetExecution[]; saveStatus?: number; failList?: boolean } = {}) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input); calls.push({ url, init })
    if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
    if (url.includes('/categories')) return Response.json([moradia, energia, lazer, salario, velha])
    if (init?.method === 'POST' || init?.method === 'PUT') {
      if (options.saveStatus) return Response.json({ title: 'Já existe uma meta de despesas fixas neste orçamento: edite o limite em vez de criar outra.' }, { status: options.saveStatus })
      return Response.json(budget, { status: init.method === 'POST' ? 201 : 200 })
    }
    if (init?.method === 'DELETE') return new Response(null, { status: 204 })
    if (url.includes('/composition')) return Response.json(composition(Number(new URL(url, 'http://x').searchParams.get('page'))))
    if (url.includes('/execution')) return Response.json(execution(options.runs ?? baseRuns))
    if (options.failList) return new Response('x', { status: 500 })
    return Response.json(options.budgets ?? [budget])
  }))
  return calls
}

const writes = (calls: Call[]) => calls.filter((c) => c.init?.method === 'POST' || c.init?.method === 'PUT')
const body = (call: Call) => JSON.parse(String(call.init!.body)) as Record<string, unknown>

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers() })

describe('FinancialBudgetsPage — metas fixas, todos os cartões e categorias', () => {
  it('apresenta os três grupos, cada meta com limite, consumido e disponível, e navega entre competências', async () => {
    const calls = mockApi()
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" />)

    const fixedCard = await screen.findByRole('article', { name: 'Meta Despesas fixas' })
    const headings = screen.getAllByRole('heading', { level: 4 }).map((h) => h.textContent)
    expect(headings.slice(0, 3)).toEqual(['Despesas fixas', 'Todos os cartões', 'Categorias'])
    expect(within(fixedCard).getByText('Limite R$ 600,00')).toBeTruthy()
    expect(fixedCard.textContent).toMatch(/Consumido\s*R\$\s540,00/)
    expect(fixedCard.textContent).toMatch(/Disponível\s*R\$\s60,00/)
    expect(within(fixedCard).getByText('Perto do limite')).toBeTruthy() // o estado é texto, não só cor
    expect(within(fixedCard).getByRole('progressbar', { name: 'Consumo de Despesas fixas' }).getAttribute('aria-valuenow')).toBe('90')
    const cards = screen.getByRole('article', { name: 'Meta Todos os cartões' })
    expect(cards.textContent).toMatch(/Consumido\s*R\$\s4\.200,00/)
    expect(cards.textContent).toMatch(/Disponível\s*R\$\s800,00/)
    expect(screen.getByText(/Faturas com vencimento em/)).toBeTruthy()
    expect(screen.getByText(/olham os mesmos gastos por ângulos diferentes/)).toBeTruthy() // as perspectivas não são somadas

    fireEvent.click(screen.getByRole('button', { name: 'Próxima competência' }))
    await waitFor(() => expect(calls.some((c) => c.url.includes('competence=2026-11'))).toBe(true))
  })

  it('mostra o excesso em texto, o limite zero sem divisão e os avisos de dados incompletos', async () => {
    mockApi({ runs: [
      run('t-fixed', 'FixedExpenses', '0.00', '10.00', { available: '-10.00', availableProjected: '-10.00', exceededAmount: '10.00', exceededProjected: '10.00', consumptionPercentage: null, status: 'Exceeded', projectedStatus: 'Exceeded',
        coverage: { estimatedCompetenceCount: 0, undefinedNatureCount: 2, undefinedNatureAmount: '55.00', noCards: false } }),
      run('t-cards', 'AllCards', '5000.00', '0.00', { coverage: { estimatedCompetenceCount: 0, undefinedNatureCount: 0, undefinedNatureAmount: '0.00', noCards: true } }),
      run('t-housing', 'Category', '1000.00', '0.00', { categoryId: moradia.id, committed: '200.00', projectedConsumption: '200.00', available: '1000.00', availableProjected: '800.00', coverage: { estimatedCompetenceCount: 3, undefinedNatureCount: 0, undefinedNatureAmount: '0.00', noCards: false } }),
    ] })
    const review = vi.fn()
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" onReviewClassification={review} />)

    const fixedCard = await screen.findByRole('article', { name: 'Meta Despesas fixas' })
    expect(within(fixedCard).getByText('Limite excedido')).toBeTruthy()
    expect(fixedCard.textContent).toMatch(/Excedido em\s*R\$\s10,00/)
    expect(within(fixedCard).getByText('Limite zero: qualquer consumo excede o limite.')).toBeTruthy()
    expect(within(fixedCard).getByText('Sem referência')).toBeTruthy()
    expect(fixedCard.textContent).toMatch(/2 despesa\(s\) do mês sem tipo definido \(R\$\s55,00\) não entram nas fixas\./)
    fireEvent.click(within(fixedCard).getByRole('button', { name: 'Revisar classificação' }))
    expect(review).toHaveBeenCalledWith({ from: '2026-10-01', to: '2026-10-31' })

    const cards = screen.getByRole('article', { name: 'Meta Todos os cartões' })
    expect(cards.textContent).toContain('Nenhum cartão cadastrado')
    expect(cards.textContent).not.toContain('Sem movimento') // ausência de dado não vira zero confirmado

    const housing = screen.getByRole('article', { name: 'Meta Moradia' })
    expect(housing.textContent).toMatch(/Previsto adicional R\$\s200,00 · projetado R\$\s200,00 · disponível projetado R\$\s800,00/)
    expect(housing.textContent).toContain('3 compra(s) de cartão com competência estimada')
  })

  it('sem metas, diz isso e "Nova meta" cria o orçamento do mês e a meta, identificando a competência e sem escolha de cartão', async () => {
    const calls = mockApi({ budgets: [] })
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" />)
    expect(await screen.findByText(/Nenhuma meta definida para/)).toBeTruthy()

    fireEvent.click(screen.getAllByRole('button', { name: 'Nova meta' })[0])
    const form = screen.getByRole('form', { name: 'Nova meta' })
    expect(within(form).getByText('outubro de 2026', { exact: false })).toBeTruthy()
    expect(within(form).queryByLabelText(/cartão individual|Cartão/)).toBeNull()
    fireEvent.change(within(form).getByLabelText('Valor do limite'), { target: { value: '5.000,00' } })
    fireEvent.change(within(form).getByLabelText('Tipo de meta'), { target: { value: 'AllCards' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Adicionar meta' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(2))
    expect(writes(calls)[0].url).toBe('/api/financial-workspaces/f1/budgets')
    expect(body(writes(calls)[0])).toEqual({ competence: '2026-10', description: null })
    expect(writes(calls)[1].url).toBe('/api/financial-workspaces/f1/budgets/budget-1/targets')
    expect(body(writes(calls)[1])).toEqual({ groupingType: 'AllCards', plannedAmount: '5000.00' })
  })

  it('meta por categoria: oferece só categorias de despesa disponíveis sem meta, com "Categoria / Subcategoria", e envia centavos exatos', async () => {
    const calls = mockApi()
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" />)
    await screen.findByRole('article', { name: 'Meta Moradia' })

    fireEvent.click(screen.getAllByRole('button', { name: 'Nova meta' })[0])
    const form = screen.getByRole('form', { name: 'Nova meta' })
    const type = within(form).getByLabelText('Tipo de meta') as HTMLSelectElement
    expect([...type.options].filter((o) => o.disabled).map((o) => o.textContent)).toEqual(['Despesas fixas (já definida)', 'Todos os cartões (já definida)'])
    fireEvent.change(type, { target: { value: 'Category' } })
    const labels = [...(within(form).getByLabelText('Categoria de despesa') as HTMLSelectElement).options].map((o) => o.textContent)
    expect(labels).toContain('Moradia / Energia')
    expect(labels).toContain('Lazer')
    expect(labels).not.toContain('Salário') // receita fora
    expect(labels).not.toContain('Moradia') // já tem meta
    expect(labels.some((label) => label?.includes('Academia'))).toBe(false) // categoria inativa não é oferecida

    fireEvent.change(within(form).getByLabelText('Categoria de despesa'), { target: { value: energia.id } })
    fireEvent.change(within(form).getByLabelText('Valor do limite'), { target: { value: '1234,56' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Adicionar meta' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(body(writes(calls)[0])).toMatchObject({ groupingType: 'Category', categoryId: energia.id, plannedAmount: '1234.56' })
  })

  it('mostra o conflito devolvido pela API ao repetir uma meta consolidada', async () => {
    mockApi({ budgets: [{ ...budget, targets: [housingTarget] }], runs: [baseRuns[2]], saveStatus: 409 })
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" />)
    await screen.findByRole('article', { name: 'Meta Moradia' })

    fireEvent.click(screen.getAllByRole('button', { name: 'Nova meta' })[0])
    fireEvent.change(screen.getByLabelText('Valor do limite'), { target: { value: '100' } })
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar meta' }))

    expect(await screen.findByText(/Já existe uma meta de despesas fixas/)).toBeTruthy()
  })

  it('edita o limite e arquiva com confirmação que explica o impacto', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" />)
    const card = await screen.findByRole('article', { name: 'Meta Todos os cartões' })

    fireEvent.click(within(card).getByRole('button', { name: 'Editar' }))
    fireEvent.change(within(card).getByLabelText('Valor do limite de Todos os cartões'), { target: { value: '4.500,00' } })
    fireEvent.click(within(card).getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(writes(calls)[0].init!.method).toBe('PUT')
    expect(body(writes(calls)[0])).toEqual({ name: 'Todos os cartões', plannedAmount: '4500.00' })

    fireEvent.click((await screen.findAllByRole('button', { name: 'Arquivar meta' }))[0])
    await waitFor(() => expect(calls.some((c) => c.init?.method === 'DELETE')).toBe(true))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('sem cancelar despesas'))
  })

  it('abre a composição paginada no servidor, com participação por cartão, competência estimada e estorno', async () => {
    const calls = mockApi()
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" />)
    const card = await screen.findByRole('article', { name: 'Meta Todos os cartões' })

    fireEvent.click(within(card).getByRole('button', { name: 'Abrir composição' }))
    const region = await within(card).findByRole('region', { name: 'Composição da meta' })

    expect(calls.some((c) => c.url.endsWith('/targets/t-cards/composition?page=1&pageSize=10'))).toBe(true)
    expect(region.textContent).toContain('12 registro(s) compõem esta meta')
    expect(within(region).getByRole('list', { name: 'Participação de cada cartão (informativa)' }).textContent).toContain('Nubank')
    expect(within(region).getByText('Streaming 1')).toBeTruthy()
    expect(within(region).getByText('Competência estimada')).toBeTruthy()
    expect(region.textContent).toMatch(/−R\$\s30,00/) // estorno reduz o consumo
    expect(within(region).queryByLabelText(/teto/i)).toBeNull() // nenhum campo de teto por cartão

    fireEvent.click(within(region).getByRole('button', { name: 'Próxima' }))
    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/composition?page=2&pageSize=10'))).toBe(true))
    expect(await within(card).findByText('Streaming 2')).toBeTruthy()
  })

  it('vindo da Visão Geral com o grupo de cartões, já abre a composição dessa meta', async () => {
    const calls = mockApi()
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" initialGroup="cards" />)

    await screen.findByRole('region', { name: 'Composição da meta' })

    expect(calls.some((c) => c.url.includes('/targets/t-cards/composition'))).toBe(true)
  })

  it('mostra erro de carregamento sem inventar valores', async () => {
    mockApi({ failList: true })
    render(<FinancialBudgetsPage workspaceId="f1" initialCompetence="2026-10" />)

    expect(await screen.findByRole('alert')).toBeTruthy()
    expect(screen.queryByRole('article')).toBeNull()
  })
})
