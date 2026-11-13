import { useState } from 'react'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Overview, type OverviewNavigation, type OverviewSection, type OverviewSelection } from './Overview'
import type { BudgetCardItem, BudgetCards, BudgetCategories, BudgetCategoryLine, BudgetControl, DashboardOverview, ExpenseCategoryShare, ExpenseDistribution, FinancialDecision } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { SixMonthPlan } from '../../api/monthlyPlan'
import { planActual } from '../planActualFixtures'
import { sixMonths } from '../monthlyPlanFixtures'
import { overview as baseOverview, monthlyResult } from './overviewFixtures'
import { decision } from './decisionFixtures'
import { budgetControl, group } from './budgetControlFixtures'

type Overrides = { plan?: PlanActualDashboard | Response; overview?: DashboardOverview | Response; evolution?: SixMonthPlan | Response; all?: ExpenseDistribution | Response; budgetCards?: BudgetCards | Response; budgetCategories?: BudgetCategories | Response; review?: unknown | Response; classification?: unknown | Response }
const emptyReview = { items: [], total: 0, page: 1, pageSize: 25, counts: {} }
const emptyClassification = { kind: 'category', from: null, to: null, wholeBudget: true, items: [], total: 0, page: 1, pageSize: 25, unclassifiableCount: 0 }
let calls: string[] = []

const share = (categoryId: string | null, name: string, amount: string, percentage: string | null, kind: ExpenseCategoryShare['kind'] = 'Category', categoryCount = 1, gross = amount): ExpenseCategoryShare =>
  ({ categoryId, name, amount, percentage, kind, categoryCount, gross })
const distribution = (over: Partial<ExpenseDistribution> = {}): ExpenseDistribution => ({
  state: 'Complete', competence: '2026-10', total: '1200.00', reference: '1200.00', reasons: [],
  categories: [share('h', 'Moradia', '700.00', '58.3'), share('f', 'Alimentação', '300.00', '25.0'), share(null, 'Sem categoria', '200.00', '16.7', 'Uncategorized', 0)], categoryCount: 2, gross: '1200.00', adjustments: '0.00', reconciliation: null,
  ...over,
})
const cardItem = (over: Partial<BudgetCardItem> = {}): BudgetCardItem => ({ cardId: 'k1', name: 'Nubank', projected: '3000.00', confirmed: '2000.00', pendingKnown: '500.00', estimated: '500.00', invoiceState: 'Open',
  closing: '2026-10-25', due: '2026-11-02', datesEstimated: false, quality: 'Projected', paymentState: 'Unpaid', ...over })
const budgetCards = (over: Partial<BudgetCards> = {}): BudgetCards => ({ state: 'Complete', competence: '2026-10', total: '3500.00', allocated: '3000.00', foodApplied: '500.00', foodReserve: '2000.00', official: '3500.00',
  cards: [cardItem()], reasons: [], ...over })
const line = (name: string, projected: string, over: Partial<BudgetCategoryLine> = {}): BudgetCategoryLine => ({ categoryId: name, name, kind: 'Category', categoryCount: 1, realized: projected, committed: '0.00', estimated: '0.00', projected,
  children: [], sources: [{ group: 'Payment', key: 'Cards', amount: '0.00' }, { group: 'Payment', key: 'Bank', amount: projected }, { group: 'Nature', key: 'Variable', amount: projected }], ...over })
const budgetCategories = (over: Partial<BudgetCategories> = {}): BudgetCategories => ({ state: 'Complete', competence: '2026-10', scope: 'PF', target: '12500.00', realized: '5000.00', committed: '1500.00', estimated: '1000.00', projected: '7500.00',
  categories: [line('Moradia', '3500.00', { realized: '3000.00', committed: '500.00', children: [{ categoryId: 'r', name: 'Aluguel', projected: '3000.00' }, { categoryId: 'm', name: 'Moradia', projected: '500.00' }] }), line('Alimentação', '2200.00'), line('Transporte', '800.00')],
  allCategories: [line('Moradia', '3500.00'), line('Alimentação', '2200.00'), line('Transporte', '800.00'), line('Lazer', '100.00')], categoryCount: 4,
  unallocatedRealized: '0.00', unallocatedCommitted: '0.00', unallocatedEstimated: '1000.00', reasons: [], ...over })
const withSummary = (over: Partial<DashboardOverview> = {}): DashboardOverview => ({ ...baseOverview, decision: decision(), budgetControl: budgetControl(), pfBudgetControl: budgetControl({ scopeKind: 'pf' }), expenseDistribution: distribution(), monthlyResult, ...over })
const overview = withSummary()
const answer = (value: unknown) => value instanceof Response ? value.clone() : new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
function mockApi(overrides: Overrides = {}) {
  calls = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    calls.push(url)
    if (url.includes('/financial-dashboard/plan-actual')) return answer(overrides.plan ?? planActual)
    if (url.includes('/financial-dashboard/overview')) return answer(overrides.overview ?? overview)
    if (url.includes('/financial-dashboard/budget-categories')) return answer(overrides.budgetCategories ?? budgetCategories())
    if (url.includes('/financial-dashboard/budget-cards')) return answer(overrides.budgetCards ?? budgetCards())
    if (url.includes('/financial-dashboard/expense-distribution')) return answer(overrides.all ?? distribution())
    if (url.includes('/monthly-plan/six-months')) return answer(overrides.evolution ?? sixMonths)
    if (url.includes('/pluggy-integration/review')) return answer(overrides.review ?? emptyReview)
    if (url.includes('/pluggy-integration/classification-review')) return answer(overrides.classification ?? emptyClassification)
    return new Response('{}', { status: 404 })
  }))
}

const navigations: { section: OverviewSection; context?: OverviewNavigation }[] = []
function Harness({ initial = '2026-10' }: { initial?: string }) {
  const [selection, setSelection] = useState<OverviewSelection>({ competence: initial, scope: { kind: 'primary' } })
  return <Overview workspaceId="w" selection={selection} onSelectionChange={setSelection} onNavigate={(section, context) => { navigations.push({ section, context }) }} />
}
const renderPage = (initial?: string) => render(<Harness initial={initial} />)
const HINT = ': ver despesas por categoria▾'
const plain = (value: string | null | undefined) => (value ?? '').replace(/[\u00a0\u202f]/g, ' ')
const text = (element: Element | null) => plain(element?.textContent)
/** Texto da linha do orçamento sem a composição (realizado, comprometido, estimado), que tem asserções próprias. */
const summary = (element: Element) => { const copy = element.cloneNode(true) as Element; copy.querySelectorAll('.ov-goal-row__parts').forEach(parts => parts.remove()); return text(copy) }
const title = (heading: Element) => [...heading.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join('').trim()
const card = (name: string) => screen.getByRole('region', { name })
const findCard = (name: string) => screen.findByRole('region', { name })
const plan = (changes: Partial<PlanActualDashboard>): PlanActualDashboard => ({ ...planActual, ...changes })
const control = (changes: Partial<BudgetControl>) => withSummary({ pfBudgetControl: budgetControl({ scopeKind: 'pf', ...changes }) })
const withDecision = (changes: Partial<FinancialDecision>) => withSummary({ decision: decision(changes) })
const loaded = () => screen.findAllByText('-R$ 1.000,00')
const overdraftPlan = (changes: Partial<PlanActualDashboard['overdraft']> = {}) => plan({ overdraft: {
  ...planActual.overdraft, eligible: true, state: 'Complete', usedAtStart: '3000.00', usedLatest: '1000.00', effectiveRecovery: '2000.00', projectedRecovery: '2600.00', contractedLimit: '5000.00', ...changes,
} })

beforeEach(() => { navigations.length = 0 })
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const accountOverdraft = {
  accountId: 'a1', name: 'Conta de teste', state: 'Complete', contractedLimit: '5000.00',
  usedLatest: '1200.00', observedAt: '2026-10-12T11:00:00Z', observedAsOf: '2026-10-12',
  usedAtStart: null, startAsOf: null, startApproximate: false, effectiveRecovery: null, direction: null,
  fullyRecovered: false, projectedUsedAtEnd: '1800.00', projectedRecovery: null,
  projectedRecoveryFromNow: null, projectedBalanceAtEnd: '-1800.00', exceedsLimit: false, reasons: [],
}

it('mostra o cheque especial entre as ações, antes do resumo, e abre a recuperação na mesma conta e competência', async () => {
  mockApi({ plan: overdraftPlan({ accounts: [accountOverdraft] }) })
  renderPage()
  const actions = await screen.findByRole('region', { name: 'Próximas ações' })
  const first = await within(actions).findByText('Cheque especial em uso · Conta de teste')
  const item = first.closest('li')!
  expect(text(item)).toContain('R$ 1.200,00 utilizados na posição de 12/10/2026')
  expect(text(item)).toContain('Prioridade alta')
  expect(actions.compareDocumentPosition(card('Resumo do mês')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  expect(screen.queryByRole('region', { name: 'Situação do cheque especial' })).toBeNull() // um único ponto de entrada
  fireEvent.click(within(item).getByRole('button', { name: 'Ver recuperação' }))
  expect(navigations.at(-1)).toEqual({
    section: 'planning',
    context: { planningTab: 'six', competence: '2026-10', recoveryScope: { kind: 'account', accountId: 'a1' } },
  })
})

it('distingue risco projetado de uso observado e preserva ausência de observação', async () => {
  mockApi({ plan: overdraftPlan({ accounts: [
    { ...accountOverdraft, usedLatest: '0.00' },
    { ...accountOverdraft, accountId: 'a2', name: 'Outra conta', state: 'Unavailable', observedAt: null, observedAsOf: null, projectedUsedAtEnd: null },
  ] }) })
  renderPage()
  const actions = await screen.findByRole('region', { name: 'Próximas ações' })
  fireEvent.click(await within(actions).findByRole('button', { name: /Ver todas as \d+ ações/ }))
  expect(text(actions)).toContain('Risco projetado de uso do cheque especial · Conta de teste')
  expect(text(actions)).toContain('Posição do cheque especial indisponível · Outra conta')
  expect(text(actions)).not.toContain('Cheque especial em uso')
})

describe('Visão Geral — composição do dashboard Folio', () => {
  it('segue a ordem da T14.3: resultado, composição, saldo e cheque especial, orçamento, próximas ações, evolução, lançamentos e, na coluna de apoio, contas, compromissos e metas', async () => {
    mockApi(); renderPage()
    await loaded()
    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('Outubro de 2026')
    expect(screen.getAllByRole('heading', { level: 3 }).map(title)).toEqual([
      'Próximas ações', 'Configure seu orçamento', 'Resumo do mês', 'Resultado do mês', 'Composição do fluxo de caixa', 'Orçamento mensal', 'Evolução financeira', 'Últimos lançamentos', 'Contas', 'Próximos compromissos', 'Metas',
    ])
    const order = ['Próximas ações', 'Resumo do mês', 'Resultado do mês', 'Composição do fluxo de caixa', 'Saldo e cheque especial', 'Orçamento mensal', 'Evolução financeira'].map(name => document.body.innerHTML.indexOf(name))
    expect(order.every((index, position) => index > 0 && (position === 0 || index > order[position - 1]))).toBe(true)
    expect(within(screen.getByRole('complementary', { name: 'Contas, compromissos e metas' })).getAllByRole('heading').map(title)).toEqual(['Contas', 'Próximos compromissos', 'Metas'])
  })

  it('os painéis antigos não existem, nem escondidos em expansões', async () => {
    mockApi(); renderPage()
    await loaded()
    for (const legacy of ['Minha situação financeira', 'Meu orçamento', 'Minha recuperação financeira', 'Onde preciso agir', 'Análises detalhadas', 'Fluxo de caixa completo',
      'Vou conseguir pagar minhas contas?', 'Margem segura para gastar à vista', 'Compromissos restantes', 'Recuperação detalhada', 'Receitas e despesas por competência']) expect(screen.queryByText(legacy)).toBeNull()
    // As expansões são só: ponte do saldo, entradas, saídas e a tabela de valores do gráfico; todas recolhidas.
    const details = [...document.querySelectorAll('details')]
    expect(details.map(detail => text(detail.querySelector('summary')))).toEqual(['Resultado de caixa, composição e cheque especial', 'Do saldo observado ao fechamento projetado', 'Entradas · previstas no mês' + 'R$ 2.450,00', 'Saídas · previstas no mês' + 'R$ 1.500,00', 'Ver valores em tabela'])
    expect(details.every(detail => !detail.open)).toBe(true) // o resumo é o topo; o resto começa recolhido
  })

  it('cada indicador do resultado tem uma única representação e o saldo observado aparece nas duas seções pedidas', async () => {
    mockApi(); renderPage()
    await loaded()
    const result = card('Resultado do mês')
    for (const kpi of ['Entradas de caixa', 'Saídas de caixa', 'Saldo bancário observado', 'Saldo projetado no fechamento']) expect(within(result).getAllByRole('article', { name: kpi })).toHaveLength(1)
    expect(screen.queryByText('Receitas do mês')).toBeNull(); expect(screen.queryByText('Despesas do mês')).toBeNull()
  })

  it('carrega o resumo, a visão geral e a evolução, uma vez cada', async () => {
    mockApi(); renderPage()
    await loaded()
    await screen.findByRole('img', { name: /Receitas e despesas de out\/26 a mar\/27/ })
    expect(calls.filter(url => url.includes('/plan-actual'))).toHaveLength(1)
    expect(calls.filter(url => url.includes('/financial-dashboard/overview'))).toHaveLength(1)
    expect(calls.filter(url => url.includes('/six-months'))).toHaveLength(1) // evolução e guia de configuração dividem a mesma leitura
    expect(calls.filter(url => url.includes('/six-months'))[0]).toContain('from=2026-07')
    expect(calls.some(url => url.includes('overdraft-recovery') || url.includes('/monthly-plan/projection'))).toBe(false)
  })
})

describe('Visão Geral — resultado do mês', () => {
  const article = (name: string) => within(card('Resultado do mês')).getByRole('article', { name })

  it('mês corrente: destaca o resultado de caixa projetado, separa o realizado até hoje e o que ainda falta, e rotula cada fluxo', async () => {
    mockApi(); renderPage()
    await loaded()
    const result = card('Resultado do mês')
    expect(text(result)).toContain('Resultado de caixa projetado para o fechamento'); expect(text(result)).toContain('R$ 950,00'); expect(text(result)).toContain('▲ Positivo')
    expect(text(result)).toMatch(/Realizado até hoje-R\$ 450,00/); expect(text(result)).toMatch(/Ainda previsto no mêsR\$ 1\.400,00/)
    expect(text(article('Entradas de caixa'))).toBe('Entradas de caixaRealizadoR$ 450,00A receberR$ 2.000,00Previsto no mêsR$ 2.450,00')
    expect(text(article('Saídas de caixa'))).toBe('Saídas de caixaRealizadoR$ 900,00A pagarR$ 600,00Previsto no mêsR$ 1.500,00')
    expect(text(article('Saldo bancário observado'))).toContain('-R$ 1.000,00'); expect(text(article('Saldo bancário observado'))).toContain('Posição do banco em 04/11/2026'); expect(text(article('Saldo bancário observado'))).toContain('Saldo negativo')
    expect(text(article('Saldo projetado no fechamento'))).toContain('R$ 1.200,00'); expect(text(article('Saldo projetado no fechamento'))).toContain('Projeção: não é o saldo do banco')
  })

  it('o resultado orçamentário é outro número, com rótulo próprio, e não se confunde com o de caixa', async () => {
    mockApi(); renderPage()
    await loaded()
    const budget = within(card('Resultado do mês')).getByLabelText('Resultado orçamentário')
    expect(text(budget)).toContain('Resultado orçamentário (projetado): receitas menos consumo da competência'); expect(text(budget)).toContain('R$ 1.250,00')
    expect(text(budget)).toContain('Não é o resultado de caixa')
    expect(text(await screen.findByRole('region', { name: /Evolução financeira/ }))).toContain('Resultado orçamentário de Outubro de 2026')
  })

  it('mês encerrado destaca o fechamento realizado e não oferece ponte nem previsões; mês futuro não simula realizado', async () => {
    const past: typeof monthlyResult = { ...monthlyResult, periodKind: 'Past', highlight: 'Realized', status: 'Negative', result: { realized: '-300.00', pending: '0.00', projected: '-300.00' },
      inflows: { realized: '1000.00', pending: '0.00', projected: '1000.00' }, outflows: { realized: '1300.00', pending: '0.00', projected: '1300.00' }, bridge: { ...monthlyResult.bridge, state: 'Unavailable', reasons: ['ClosedMonth'] } }
    mockApi({ plan: plan({ periodKind: 'Past' }), overview: withSummary({ monthlyResult: past }) }); renderPage('2026-08')
    expect(await screen.findByText('Resultado de caixa do mês (realizado)')).toBeTruthy()
    const result = card('Resultado do mês')
    expect(text(result)).toContain('▼ Negativo'); expect(text(result)).toContain('-R$ 300,00'); expect(text(result)).toContain('Saldo no fechamento')
    expect(text(within(result).getByRole('article', { name: 'Entradas de caixa' }))).toBe('Entradas de caixaRealizadoR$ 1.000,00')
    expect(within(result).queryByText('Do saldo observado ao fechamento projetado')).toBeNull(); expect(screen.queryByRole('article', { name: 'Saldo projetado no fechamento' })).toBeNull()
    cleanup()
    const future: typeof monthlyResult = { ...monthlyResult, periodKind: 'Future', inflows: { realized: null, pending: '3000.00', projected: '3000.00' }, outflows: { realized: null, pending: '2000.00', projected: '2000.00' },
      result: { realized: null, pending: '1000.00', projected: '1000.00' }, budget: { ...monthlyResult.budget, realized: null } }
    mockApi({ plan: plan({ periodKind: 'Future' }), overview: withSummary({ monthlyResult: future }) }); renderPage('2026-12')
    expect(await screen.findByText('Resultado de caixa projetado')).toBeTruthy()
    expect(text(within(card('Resultado do mês')).getByRole('article', { name: 'Entradas de caixa' }))).toBe('Entradas de caixaA receberR$ 3.000,00Previsto no mêsR$ 3.000,00')
    expect(text(card('Resultado do mês'))).toContain('Mês futuro: só há projeção, nada foi realizado.'); expect(text(card('Resultado do mês'))).not.toContain('Realizado até hoje')
  })

  it('resultado negativo é dito em texto e com símbolo, sem depender da cor', async () => {
    mockApi({ overview: withSummary({ monthlyResult: { ...monthlyResult, status: 'Negative', result: { realized: '-450.00', pending: '-100.00', projected: '-550.00' } } }) }); renderPage()
    await loaded()
    expect(text(card('Resultado do mês'))).toContain('▼ Negativo'); expect(text(card('Resultado do mês'))).toContain('-R$ 550,00')
  })

  it('a ponte do saldo mostra o observado, as entradas e saídas depois da referência e o fechamento', async () => {
    mockApi(); renderPage()
    await loaded()
    const bridge = card('Resultado do mês').querySelector('details.ov-bridge') as HTMLElement
    expect(text(bridge)).toContain('Saldo observado em 04/11/2026-R$ 1.000,00'); expect(text(bridge)).toContain('+ Entradas previstas depois da referênciaR$ 2.000,00')
    expect(text(bridge)).toContain('− Saídas previstas depois da referênciaR$ 600,00'); expect(text(bridge)).toContain('Saldo projetado no fechamentoR$ 400,00')
    expect(text(bridge)).not.toContain('Diferença não explicada')
  })

  it('ponte que não fecha é parcial, mostra a diferença como está e não inventa ajuste', async () => {
    const partial = { ...monthlyResult, bridge: { ...monthlyResult.bridge, state: 'Partial' as const, unreconciled: '300.00', reasons: ['BridgeNotReconciled'] } }
    mockApi({ overview: withSummary({ monthlyResult: partial }) }); renderPage()
    await loaded()
    const bridge = card('Resultado do mês').querySelector('details.ov-bridge') as HTMLElement
    expect(text(bridge.querySelector('summary'))).toContain('Parcial'); expect(text(bridge)).toContain('Diferença não explicada pelos dadosR$ 300,00'); expect(text(bridge)).toContain('a projeção é parcial')
    expect(text(bridge)).not.toMatch(/ajuste/i)
  })

  it('sem resultado do servidor, diz que está indisponível e não mostra R$ 0,00 nem composição inventada', async () => {
    mockApi({ overview: withSummary({ monthlyResult: null }) }); renderPage()
    await loaded()
    expect(text(card('Resultado do mês'))).toContain('O resultado do mês não está disponível agora'); expect(text(card('Resultado do mês'))).not.toContain('R$ 0,00')
    expect(text(card('Composição do fluxo de caixa'))).toContain('A composição não está disponível agora')
  })

  it('durante a carga mostra esqueletos neutros por seção, sem valores nem "Carregando…" em vermelho', async () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})))
    renderPage()
    expect(card('Resultado do mês').querySelectorAll('.ov-skeleton').length).toBeGreaterThan(0)
    expect(card('Composição do fluxo de caixa').querySelectorAll('.ov-skeleton').length).toBeGreaterThan(0)
    expect(card('Saldo e cheque especial').querySelectorAll('.ov-skeleton').length).toBeGreaterThan(0)
    expect(document.body.textContent).not.toMatch(/R\$/); expect(card('Resultado do mês').getAttribute('aria-busy')).toBe('true')
    expect(document.querySelector('.ov-negative')).toBeNull()
  })

  it('ao trocar a competência não mostra o mês anterior: esqueleto até a resposta nova, e resposta atrasada de outro mês é descartada', async () => {
    const answers = new Map<string, (response: Response) => void>()
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/financial-dashboard/overview')) {
        if (url.includes('competence=2026-10')) return Promise.resolve(answer(overview))
        return new Promise<Response>(resolve => answers.set(url, resolve))
      }
      if (url.includes('/financial-dashboard/plan-actual')) return Promise.resolve(answer(planActual))
      if (url.includes('/monthly-plan/six-months')) return Promise.resolve(answer(sixMonths))
      return Promise.resolve(new Response('{}', { status: 404 }))
    }))
    renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    await waitFor(() => expect(answers.size).toBe(1))
    expect(card('Resultado do mês').querySelectorAll('.ov-skeleton').length).toBeGreaterThan(0)
    expect(text(card('Resultado do mês'))).not.toContain('R$ 950,00')
    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    await waitFor(() => expect(answers.size).toBe(2))
    const [first, second] = [...answers.values()]
    first(answer(withSummary({ monthlyResult: { ...monthlyResult, result: { realized: null, pending: '1.00', projected: '777.00' } } })))
    await new Promise(resolve => setTimeout(resolve, 30))
    expect(text(card('Resultado do mês'))).not.toContain('R$ 777,00')
    second(answer(withSummary({ monthlyResult: { ...monthlyResult, result: { realized: null, pending: '1.00', projected: '555.00' } } })))
    await waitFor(() => expect(text(card('Resultado do mês'))).toContain('R$ 555,00'))
  })
})

describe('Visão Geral — composição do fluxo de caixa', () => {
  it('abre entradas e saídas nas partes, uma única vez cada, com o pagamento da fatura como saída e a fatura devida em Cartões', async () => {
    mockApi(); renderPage()
    await loaded()
    const composition = card('Composição do fluxo de caixa')
    const [inflows, outflows] = [...composition.querySelectorAll('details')]
    expect([...inflows.querySelectorAll('li')].map(li => text(li.querySelector('div')))).toEqual(['Receitas recebidasRealizado', 'Receitas previstas a receberPrevisto'])
    expect([...outflows.querySelectorAll('li')].map(li => text(li.querySelector('div')))).toEqual(['Despesas pagas pela contaRealizado', 'Faturas de cartão pagasRealizado', 'Faturas de cartão ainda devidasPrevisto'])
  })

  it('cada parte leva à origem: Lançamentos do mês e da conta, Agenda no mês ou Cartões', async () => {
    mockApi(); renderPage()
    await loaded()
    const composition = card('Composição do fluxo de caixa')
    fireEvent.click(within(composition).getAllByRole('button', { name: 'Ver nos Lançamentos ›' })[0])
    expect(navigations.at(-1)).toEqual({ section: 'transactions', context: { transactions: { from: '2026-10-01', to: '2026-10-31', accountId: 'pf' } } })
    fireEvent.click(within(composition).getAllByRole('button', { name: 'Ver na Agenda ›' })[0]); expect(navigations.at(-1)).toEqual({ section: 'calendar', context: { month: '2026-10' } })
    fireEvent.click(within(composition).getByRole('button', { name: 'Ver em Cartões ›' })); expect(navigations.at(-1)).toEqual({ section: 'cards', context: undefined })
  })

  it('no consolidado avisa que as transferências internas são neutras; numa conta mostra as transferências à parte', async () => {
    mockApi({ overview: withSummary({ scopeKind: 'all', scopeAccountId: null }) }); renderPage()
    await loaded()
    await waitFor(() => expect(text(card('Composição do fluxo de caixa'))).toContain('Transferências entre contas do consolidado são neutras'))
    cleanup()
    const withTransfers = { ...monthlyResult, transfersReceived: '900.00', transfersSent: '100.00', components: [...monthlyResult.components,
      { code: 'TransfersReceived', side: 'Inflow' as const, status: 'Realized' as const, amount: '900.00', target: 'Entries' as const }, { code: 'TransfersSent', side: 'Outflow' as const, status: 'Realized' as const, amount: '100.00', target: 'Entries' as const }] }
    mockApi({ overview: withSummary({ monthlyResult: withTransfers }) }); renderPage()
    await loaded()
    const composition = card('Composição do fluxo de caixa')
    expect(text(composition)).toContain('Transferências recebidas'); expect(text(composition)).toContain('Transferências enviadas'); expect(text(composition)).not.toContain('são neutras')
  })
})

describe('Visão Geral — cheque especial e recuperação no bloco do saldo', () => {
  it('conta sem cheque especial: o componente não ocupa espaço', async () => {
    mockApi(); renderPage()
    await loaded()
    expect(screen.queryByLabelText('Cheque especial')).toBeNull()
  })

  it('conta com limite: limite utilizado e contratado, recuperação do mês, meta e projeção, compactos e com acesso ao Planejamento', async () => {
    mockApi({ plan: overdraftPlan() }); renderPage()
    const overdraft = await screen.findByLabelText('Cheque especial')
    expect(text(overdraft)).toContain('R$ 1.000,00 de R$ 5.000,00')
    expect(plain(within(overdraft).getByRole('meter').getAttribute('aria-valuetext'))).toBe('R$ 1.000,00 de R$ 5.000,00')
    // Na conta, recuperação e meta vêm da decisão do servidor (meta fixada).
    expect(text(overdraft)).toMatch(/Recuperado no mês\s*R\$ 500,00/); expect(text(overdraft)).toMatch(/Meta do mês\s*R\$ 2\.500,00/); expect(text(overdraft)).toMatch(/Projetado\s*R\$ 1\.000,00/)
    fireEvent.click(within(overdraft).getByRole('button', { name: 'Recuperação no Planejamento ›' }))
    expect(navigations.at(-1)).toEqual({ section: 'planning', context: { planningTab: 'projection' } })
    expect(screen.queryByRole('region', { name: /recuperação/i })).toBeNull() // não é outra seção do dashboard
  })

  it('meta provisória é dita provisória e meta ausente não inventa valor', async () => {
    const base = decision()
    mockApi({ plan: overdraftPlan(), overview: withDecision({ recovery: { ...base.recovery, state: 'Provisional', targetFixedAt: null } }) }); renderPage()
    expect(text(await screen.findByLabelText('Cheque especial'))).toMatch(/Meta provisória\s*R\$ 2\.500,00/)
    cleanup()
    mockApi({ plan: overdraftPlan(), overview: withDecision({ recovery: { ...base.recovery, state: 'NotRegistered', target: null, projectedRecovery: null, gapToTarget: null } }) }); renderPage()
    const none = await screen.findByLabelText('Cheque especial')
    await waitFor(() => expect(text(none)).toContain('Meta não configurada'))
    expect(text(none)).toContain('Ainda não é possível calcular'); expect(text(none)).not.toMatch(/R\$ 0,00/)
  })

  it('recuperação negativa é apresentada como aumento da utilização do limite', async () => {
    const base = decision()
    mockApi({ plan: overdraftPlan(), overview: withDecision({ recovery: { ...base.recovery, effectiveRecovery: '-300.00', projectedRecovery: '-800.00' } }) }); renderPage()
    const overdraft = await screen.findByLabelText('Cheque especial')
    await waitFor(() => expect(text(overdraft)).toMatch(/Aumento da utilização\s*R\$ 300,00/))
    expect(text(overdraft)).toMatch(/Aumento projetado\s*R\$ 800,00/); expect(text(overdraft)).not.toContain('Recuperado no mês'); expect(text(overdraft)).not.toContain('-R$')
  })

  it('consolidado: valores somados conta a conta pelo servidor e meta acompanhada por conta', async () => {
    const base = decision()
    mockApi({ plan: { ...overdraftPlan({ usedLatest: '1800.00', contractedLimit: '8000.00' }), scope: { ...planActual.scope, kind: 'all', accountId: null, accountName: null } },
      overview: withDecision({ recovery: { ...base.recovery, state: 'NotApplicable', reason: 'ConsolidatedScope', target: null, effectiveRecovery: null, projectedRecovery: null } }) }); renderPage()
    const overdraft = await screen.findByLabelText('Cheque especial')
    expect(text(overdraft)).toContain('R$ 1.800,00 de R$ 8.000,00')
    await waitFor(() => expect(text(overdraft)).toMatch(/Recuperado no mês\s*R\$ 2\.000,00/))
    expect(text(overdraft)).toMatch(/Meta do mês\s*Por conta/); expect(text(overdraft)).toMatch(/Projetado\s*R\$ 2\.600,00/)
  })
})

describe('Visão Geral — orçamento mensal', () => {
  const row = (name: string) => within(card('Orçamento mensal')).getByRole('listitem', { name })

  it('meta total, cartões e fora dos cartões com percentual projetado, total previsto / meta e diferença do servidor', async () => {
    mockApi(); renderPage()
    const budget = await findCard('Orçamento mensal')
    await waitFor(() => expect(text(budget)).toContain('Meta total'))
    expect(text(budget)).toContain('Orçamento PF · Todas as contas PF · Outubro de 2026')
    expect(summary(row('Meta total'))).toBe('Meta total: ver despesas por categoria▾60,0%R$ 7.500,00 / R$ 12.500,00R$ 5.000,00 abaixo da meta')
    expect(summary(row('Cartões de crédito'))).toBe('Cartões de crédito▾70,0%R$ 3.500,00 / R$ 5.000,00R$ 1.500,00 abaixo da meta')
    expect(summary(row('Despesas fora dos cartões'))).toBe('Despesas fora dos cartões53,3%R$ 4.000,00 / R$ 7.500,00R$ 3.500,00 abaixo da meta')
    // Total previsto vem pronto (efetivado + comprometido + estimado): o efetivado (R$ 5.000,00) não é somado de novo nem exibido à parte.
    expect(text(budget)).not.toContain('R$ 5.000,00 / ')
    expect(row('Meta total').className).toContain('ov-goal-row--within')
    fireEvent.click(within(budget).getByRole('button', { name: 'Ver detalhes ›' }))
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('meta ultrapassada: vermelho, texto acima de 100% e barra limitada a 100%', async () => {
    mockApi({ overview: control({ cards: group('Cards', { projected: '5400.00', margin: '0.00', excess: '400.00', projectedPercent: '108.0', status: 'Exceeded' }) }) }); renderPage()
    await findCard('Orçamento mensal')
    const cards = await waitFor(() => row('Cartões de crédito'))
    expect(cards.className).toContain('ov-goal-row--over')
    expect(text(cards)).toContain('108,0%'); expect(text(cards)).toContain('R$ 400,00 acima da meta')
    expect((cards.querySelector('.ov-track__done') as HTMLElement).style.width).toBe('min(100%, 108.0%)')
  })

  it('meta igual a zero: sem percentual fictício e o gasto aparece acima da meta', async () => {
    mockApi({ overview: control({ outsideCards: group('OutsideCards', { target: '0.00', projected: '4000.00', margin: '0.00', excess: '4000.00', realizedPercent: null, projectedPercent: null, status: 'Exceeded' }) }) }); renderPage()
    await findCard('Orçamento mensal')
    const outside = await waitFor(() => row('Despesas fora dos cartões'))
    expect(summary(outside)).toBe('Despesas fora dos cartões—R$ 4.000,00 / R$ 0,00R$ 4.000,00 acima da meta')
  })

  it('competência sem meta: gastos visíveis, sem percentual nem meta zero, e acesso para configurar', async () => {
    const base = budgetControl()
    const none = { target: null, margin: null, excess: null, projectedPercent: null, realizedPercent: null, status: 'NoTarget' as const, state: 'TargetNotDefined' as const }
    mockApi({ overview: control({ state: 'TargetNotDefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, realizedPercent: null, status: 'NoTarget' },
      cards: group('Cards', none), outsideCards: group('OutsideCards', none) }) }); renderPage()
    const budget = await findCard('Orçamento mensal')
    await waitFor(() => expect(summary(row('Meta total'))).toBe('Meta total' + HINT + 'R$ 7.500,00Meta não configurada'))
    expect(summary(budget)).not.toMatch(/%/); expect(summary(budget)).not.toContain('R$ 0,00')
    fireEvent.click(within(budget).getByRole('button', { name: 'Configurar orçamento' }))
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('meta herdada ou exceção do mês: a meta exibida é a efetiva que o servidor resolveu', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ totalTargetOrigin: 'Customized', total: { ...base.total, target: '9000.00', margin: '1500.00', projectedPercent: '83.3' } }) }); renderPage()
    await findCard('Orçamento mensal')
    await waitFor(() => expect(summary(row('Meta total'))).toBe('Meta total: ver despesas por categoria▾83,3%R$ 7.500,00 / R$ 9.000,00R$ 1.500,00 abaixo da meta'))
  })

  it('meta complementar de despesas fixas: uma linha com o valor efetivo, sem barra nem soma', async () => {
    mockApi(); renderPage()
    const budget = await findCard('Orçamento mensal')
    await waitFor(() => expect(text(budget)).toContain('Meta complementar: despesas fixasR$ 600,00'))
    expect(within(budget).getAllByRole('progressbar')).toHaveLength(3)
    cleanup()
    mockApi({ overview: withSummary({ budget: { ...overview.budget, fixed: null } }) }); renderPage()
    await waitFor(() => expect(text(card('Orçamento mensal'))).toContain('Meta total'))
    expect(text(card('Orçamento mensal'))).not.toContain('Meta complementar')
  })

  it('o orçamento PF não depende da conta escolhida: mesma meta e mesmos valores com a conta PJ ou o consolidado, enquanto as categorias acompanham a conta', async () => {
    mockApi(); renderPage()
    await waitFor(() => expect(summary(row('Meta total'))).toContain('R$ 12.500,00'))
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'account:pj' } })
    await waitFor(() => expect(calls.some(url => url.includes('accountId=pj'))).toBe(true))
    expect(text(card('Orçamento mensal'))).toContain('Orçamento PF · Todas as contas PF'); expect(summary(row('Meta total'))).toContain('R$ 12.500,00')
  })

  it('meta total ausente: a meta de cartões continua comparável e nada é inventado para o resto', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ state: 'TargetNotDefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, realizedPercent: null, status: 'NoTarget' },
      outsideCards: group('OutsideCards', { state: 'TargetNotDefined', target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' }) }) }); renderPage()
    await findCard('Orçamento mensal')
    await waitFor(() => expect(summary(row('Meta total'))).toBe('Meta total' + HINT + 'R$ 7.500,00Meta não configurada'))
    expect(summary(row('Cartões de crédito'))).toContain('70,0%'); expect(summary(row('Despesas fora dos cartões'))).toBe('Despesas fora dos cartõesR$ 4.000,00Meta não configurada')
    expect(text(card('Orçamento mensal'))).not.toContain('R$ 12.500,00')
  })
})

describe('Visão Geral — accordion do orçamento mensal e previsão por categoria', () => {
  const totalToggle = () => within(card('Orçamento mensal')).getByRole('button', { name: /Meta total: ver despesas por categoria/ })
  const cardsToggle = () => within(card('Orçamento mensal')).getByRole('button', { name: /Cartões de crédito/ })
  const ready = async () => { renderPage(); return waitFor(() => totalToggle()) }

  it('os dois painéis começam recolhidos, sem consultas, e são mutuamente exclusivos; o resumo continua visível', async () => {
    mockApi(); const total = await ready()
    expect(total.getAttribute('aria-expanded')).toBe('false'); expect(cardsToggle().getAttribute('aria-expanded')).toBe('false')
    expect(calls.some(url => url.includes('/budget-categories') || url.includes('/budget-cards'))).toBe(false)
    fireEvent.click(total); expect(await screen.findByText('Previsão por categoria')).toBeTruthy()
    expect(total.getAttribute('aria-expanded')).toBe('true')
    fireEvent.click(cardsToggle()); expect(await screen.findByText(/Faturas de outubro/)).toBeTruthy()
    expect(total.getAttribute('aria-expanded')).toBe('false'); expect(screen.queryByText('Previsão por categoria')).toBeNull() // abrir cartões recolhe categorias
    fireEvent.click(total); expect(await screen.findByText('Previsão por categoria')).toBeTruthy(); expect(screen.queryByText(/Faturas de outubro/)).toBeNull()
    expect(text(card('Orçamento mensal'))).toContain('R$ 7.500,00 / R$ 12.500,00') // o resumo nunca some
  })

  it('lista as categorias com total previsto, legenda e componentes não alocados; o card independente de categorias não existe', async () => {
    mockApi(); fireEvent.click(await ready())
    const panel = await waitFor(() => document.getElementById('ov-card-details-total') as HTMLElement)
    await waitFor(() => expect(text(panel)).toContain('Moradia'))
    expect(text(panel)).toContain('Efetivado'); expect(text(panel)).toContain('Comprometido'); expect(text(panel)).toContain('Estimado')
    expect(within(panel).getAllByRole('button', { expanded: false }).filter(button => button.classList.contains('ov-catfc__row')).map(button => text(button))).toEqual(['▾MoradiaR$ 3.500,00', '▾AlimentaçãoR$ 2.200,00', '▾TransporteR$ 800,00'])
    expect(text(panel)).toContain('Estimativas não alocadas a categoriasR$ 1.000,00'); expect(text(panel)).toContain('Total previsto PFR$ 7.500,00')
    expect(text(panel)).not.toContain('Conciliação parcial'); expect(screen.queryByRole('region', { name: 'Despesas por categoria' })).toBeNull()
    expect(calls.filter(url => url.includes('/budget-categories?competence=2026-10'))).toHaveLength(1)
  })

  it('cada categoria expande a composição (efetivado, comprometido, estimado, subcategorias e perspectivas) e recolhe', async () => {
    mockApi(); fireEvent.click(await ready())
    const row = await screen.findByRole('button', { name: /Moradia/ })
    fireEvent.click(row)
    const detail = document.getElementById(row.getAttribute('aria-controls') as string) as HTMLElement
    expect(text(detail)).toContain('EfetivadoR$ 3.000,00'); expect(text(detail)).toContain('ComprometidoR$ 500,00'); expect(text(detail)).toContain('EstimadoR$ 0,00')
    expect(text(within(detail).getByRole('list', { name: 'Subcategorias de Moradia' }))).toBe('AluguelR$ 3.000,00MoradiaR$ 500,00')
    expect(text(within(detail).getByRole('list', { name: 'Composição de Moradia' }))).toBe('Fora dos cartõesR$ 3.500,00Despesas variáveisR$ 3.500,00')
    fireEvent.click(row); expect(document.getElementById(row.getAttribute('aria-controls') as string)).toBeNull()
  })

  it('"Ver todas as categorias" expande a lista completa no próprio painel, sem modal', async () => {
    mockApi(); fireEvent.click(await ready())
    const all = await screen.findByRole('button', { name: 'Ver todas as categorias' })
    expect(screen.queryByText('Lazer')).toBeNull(); fireEvent.click(all)
    expect(await screen.findByText('Lazer')).toBeTruthy(); expect(screen.queryByRole('dialog')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Mostrar menos' })); expect(screen.queryByText('Lazer')).toBeNull()
  })

  it('o que as categorias não discriminam fica explícito e a conciliação é parcial; falha de carga permite nova tentativa', async () => {
    mockApi({ budgetCategories: budgetCategories({ state: 'Partial', unallocatedRealized: '300.00', unallocatedCommitted: '-50.00', reasons: ['RealizedNotDiscriminated', 'CommittedNotDiscriminated'] }) })
    fireEvent.click(await ready())
    const panel = await waitFor(() => document.getElementById('ov-card-details-total') as HTMLElement)
    await waitFor(() => expect(text(panel)).toContain('Efetivado não discriminado por categoriaR$ 300,00'))
    expect(text(panel)).toContain('Comprometido não discriminado por categoria-R$ 50,00'); expect(text(panel)).toContain('Conciliação parcial')
    cleanup(); mockApi({ budgetCategories: new Response('{}', { status: 500 }) })
    fireEvent.click(await ready()); expect(await screen.findByRole('button', { name: 'Recarregar' })).toBeTruthy()
  })

  it('conta isolada (meta global) não oferece o detalhamento por categoria', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ scopeKind: 'account', targetsAttributable: false, total: { ...base.total, targetDefined: false, target: null, status: 'NoTarget' as const } }) }); renderPage()
    await waitFor(() => expect(text(card('Orçamento mensal'))).toContain('Meta total'))
    expect(within(card('Orçamento mensal')).queryByRole('button', { name: /ver despesas por categoria/ })).toBeNull()
  })
})

describe('Visão Geral — detalhamento dos cartões no orçamento', () => {
  const toggle = () => within(card('Orçamento mensal')).getByRole('button', { name: /Cartões de crédito/ })
  const open = async () => { renderPage(); const button = await waitFor(() => toggle()); fireEvent.click(button); return button }

  it('começa recolhido, sem consulta, com a linha igual; expande e recolhe pelo botão com aria-expanded', async () => {
    mockApi(); renderPage()
    const button = await waitFor(() => toggle())
    expect(button.getAttribute('aria-expanded')).toBe('false'); expect(calls.some(url => url.includes('/budget-cards'))).toBe(false)
    expect(text(within(card('Orçamento mensal')).getByRole('listitem', { name: 'Cartões de crédito' }))).toContain('70,0%')
    fireEvent.click(button)
    expect(button.getAttribute('aria-expanded')).toBe('true'); expect(button.getAttribute('aria-controls')).toBe('ov-card-details-cards')
    expect(await screen.findByText('Nubank')).toBeTruthy(); expect(calls.filter(url => url.includes('/budget-cards?competence=2026-10'))).toHaveLength(1)
    fireEvent.click(button)
    expect(screen.queryByText('Nubank')).toBeNull(); expect(text(card('Orçamento mensal'))).toContain('70,0%') // só o detalhamento some; os indicadores ficam
  })

  it('cada cartão mostra valor, estado da fatura, pagamento, datas e qualidade, sem barra individual, e leva a Cartões', async () => {
    mockApi({ budgetCards: budgetCards({ cards: [
      cardItem({ cardId: 'a', name: 'Itaú', projected: '4000.00', invoiceState: 'Closed', quality: 'Confirmed', paymentState: 'Partial' }),
      cardItem({ cardId: 'b', name: 'Nubank', invoiceState: 'ClosingUnconfirmed', paymentState: 'Unpaid', datesEstimated: true }),
      cardItem({ cardId: 'c', name: 'Inter', projected: '200.00', invoiceState: 'Future', closing: null, due: null, quality: 'Incomplete', paymentState: null }),
    ] }) })
    await open()
    const rows = await within(card('Orçamento mensal')).findAllByRole('button', { name: /Ver em Cartões/ })
    expect(rows.map(row => text(row))).toEqual([
      'ItaúFechada · paga em parte · Fecha 25/10 · Vence 02/11Confirmada pelo bancoR$ 4.000,00',
      'NubankFechamento não confirmado · não paga · Fecha 25/10 · Vence 02/11Datas estimadasR$ 3.000,00',
      'InterFutura · Datas ainda não conhecidas⚠ Dados parciaisR$ 200,00',
    ])
    expect(within(card('Orçamento mensal')).getAllByRole('progressbar')).toHaveLength(3) // só as três barras do orçamento
    fireEvent.click(rows[0]); expect(navigations.at(-1)).toEqual({ section: 'cards', context: undefined })
  })

  it('previsão alimentar adicional dentro da meta em linha separada e detalhamento parcial preservando o total oficial; cartão sem pagador é dito', async () => {
    mockApi({ budgetCards: budgetCards({ state: 'Partial', official: '3600.00', reasons: ['UnexplainedDifference', 'MissingPayerAccount'] }) })
    await open()
    expect(await screen.findByText('Previsão alimentar adicional dentro da meta')).toBeTruthy()
    const panel = document.getElementById('ov-card-details-cards') as HTMLElement
    expect(text(panel)).toContain('R$ 500,00'); expect(text(panel)).toContain('o total oficial dos cartões é R$ 3.600,00'); expect(text(panel)).toContain('cartão sem conta pagadora')
  })

  it('sem complemento aplicado, a reserva aparece como informação secundária e não entra na soma', async () => {
    mockApi({ budgetCards: budgetCards({ foodApplied: '0.00', foodReserve: '2000.00', allocated: '3000.00', total: '3000.00' }) })
    await open()
    const panel = await waitFor(() => document.getElementById('ov-card-details-cards') as HTMLElement)
    await waitFor(() => expect(text(panel)).toContain('Reserva alimentarR$ 2.000,00 dentro da meta dos cartões.'))
    expect(text(panel)).not.toContain('Previsão alimentar adicional')
  })

  it('competência sem cartões e falha de carregamento com nova tentativa', async () => {
    mockApi({ budgetCards: budgetCards({ cards: [], foodApplied: '0.00', foodReserve: null, allocated: '0.00', total: '0.00' }) })
    await open(); expect(await screen.findByText('Nenhum cartão com gasto nesta competência.')).toBeTruthy()
    cleanup(); mockApi({ budgetCards: new Response('{}', { status: 500 }) })
    await open(); expect(await screen.findByRole('button', { name: 'Recarregar' })).toBeTruthy()
  })
})

describe('Visão Geral — pontos de atenção', () => {
  it('risco de caixa ou da meta fica visível e objetivo; secundários levam à tela onde se resolvem', async () => {
    mockApi(); renderPage()
    const alerts = await screen.findByRole('region', { name: 'Próximas ações' })
    const items = () => within(alerts).getAllByRole('listitem')
    expect(text(items()[0])).toContain('Há compromissos vencidos e não pagos.'); expect(text(items()[0])).toContain('R$ 100,00 em aberto.'); expect(text(items()[0])).toContain('Prioridade alta')
    expect(text(items()[1])).toContain('A recuperação projetada está abaixo da meta.')
    expect(items()).toHaveLength(3)
    fireEvent.click(within(alerts).getByRole('button', { name: /Ver todas as 6 ações/ }))
    expect(items()).toHaveLength(6)
    fireEvent.click(within(alerts).getByRole('button', { name: 'Revisar classificação ›' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'automation', context: { review: { kind: 'category' } } })
  })

  it('"Ver vencimentos" abre a Agenda na competência selecionada', async () => {
    mockApi(); renderPage('2026-10')
    const alerts = await screen.findByRole('region', { name: 'Próximas ações' })
    fireEvent.click(within(alerts).getByRole('button', { name: 'Ver vencimentos' }))
    expect(navigations.at(-1)).toEqual({ section: 'calendar', context: { month: '2026-10' } })
  })

  it('pendências da fila viram um único atalho "Requer sua decisão", que substitui os avisos separados de revisão e classificação', async () => {
    mockApi({ review: { ...emptyReview, items: [{ postingId: 'a', accounted: false, description: 'PIX', reason: null, linkDisplayName: 'Conta', allowedResolutions: [] }], total: 1 }, classification: { ...emptyClassification, items: [{ action: 'Transaction', recordId: 'k', description: 'Loja', amount: '10.00', date: '2026-10-02' }], total: 2 } })
    renderPage()
    const alerts = await screen.findByRole('region', { name: 'Próximas ações' })
    fireEvent.click(await within(alerts).findByRole('button', { name: /Ver todas as \d+ ações/ }))
    const shortcut = (await within(alerts).findByText('3 pendências requerem sua decisão.')).closest('li') as HTMLElement
    expect(text(shortcut)).toContain('Atenção')
    expect(text(shortcut)).toContain('1 fora dos totais até a revisão.')
    expect(within(alerts).getAllByRole('listitem').filter(item => /sua decisão/.test(text(item)))).toHaveLength(1)
    expect(within(alerts).queryByRole('button', { name: 'Revisar classificação ›' })).toBeNull()
    fireEvent.click(within(shortcut).getByRole('button', { name: 'Ver o que requer sua decisão ›' }))
    expect(navigations.at(-1)).toEqual({ section: 'automation', context: { automationTab: 'decisions' } })
  })

  it('se a fila falha, a lista avisa que pode estar incompleta e mantém os avisos antigos por fonte', async () => {
    mockApi({ review: new Response('{}', { status: 500 }), classification: new Response('{}', { status: 500 }) }); renderPage()
    const alerts = await screen.findByRole('region', { name: 'Próximas ações' })
    expect(await within(alerts).findByRole('alert')).toBeTruthy()
    fireEvent.click(await within(alerts).findByRole('button', { name: /Ver todas as \d+ ações/ }))
    expect(within(alerts).getByRole('button', { name: 'Revisar classificação ›' })).toBeTruthy()
  })

  it('sem pontos de atenção, mostra uma mensagem compacta (e nunca uma lista vazia)', async () => {
    const base = decision()
    mockApi({ overview: withSummary({ classification: { state: 'Complete', needsReview: 0, notEvaluated: 0 }, decision: { ...base, commitments: { ...base.commitments, overdue: '0.00' }, recovery: { ...base.recovery, gapToTarget: '100.00' } }, budgetControl: budgetControl({ pressures: [], recommendations: [] }), pfBudgetControl: budgetControl({ scopeKind: 'pf', pressures: [], recommendations: [] }) }) }); renderPage()
    await loaded()
    const actions = await screen.findByRole('region', { name: 'Próximas ações' })
    expect(text(actions)).toContain('Nenhuma ação prioritária identificada com os dados atuais.')
    expect(within(actions).queryAllByRole('listitem')).toHaveLength(0)
  })

  it('falha de uma fonte avisa que a lista pode estar incompleta e oferece "Tentar novamente"', async () => {
    mockApi({ overview: new Response('{}', { status: 500 }) }); renderPage()
    const actions = await screen.findByRole('region', { name: 'Próximas ações' })
    expect(await within(actions).findByRole('alert')).toBeTruthy()
    expect(text(actions)).toContain('pode estar incompleta')
    expect(within(actions).getByRole('button', { name: 'Tentar novamente' })).toBeTruthy()
    expect(text(actions)).not.toContain('Nenhuma ação prioritária')
  })
})

describe('Visão Geral — ações prioritárias ao trocar o recorte', () => {
  it('trocar a competência rapidamente nunca exibe ações do recorte anterior, nem quando a resposta antiga chega depois', async () => {
    const pending: ((response: Response) => void)[] = []
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/financial-dashboard/overview')) {
        if (url.includes('competence=2026-10')) return Promise.resolve(answer(overview))
        return new Promise<Response>(resolve => pending.push(resolve))
      }
      if (url.includes('/financial-dashboard/plan-actual')) return Promise.resolve(answer(planActual))
      if (url.includes('/monthly-plan/six-months')) return Promise.resolve(answer(sixMonths))
      return Promise.resolve(new Response('{}', { status: 404 }))
    }))
    renderPage(); await loaded()
    const actions = () => screen.getByRole('region', { name: 'Próximas ações' })
    await waitFor(() => expect(text(actions())).toContain('Há compromissos vencidos e não pagos.'))
    const base = decision()
    const calm = withSummary({ decision: { ...base, commitments: { ...base.commitments, overdue: '0.00' }, recovery: { ...base.recovery, gapToTarget: '100.00' } }, budgetControl: budgetControl({ pressures: [], recommendations: [] }), pfBudgetControl: budgetControl({ scopeKind: 'pf', pressures: [], recommendations: [] }) })

    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    await waitFor(() => expect(pending).toHaveLength(1))
    expect(text(actions())).not.toContain('Há compromissos vencidos') // a ação do mês anterior sai assim que o recorte muda
    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    await waitFor(() => expect(pending).toHaveLength(2))
    pending[1](answer(calm)) // a resposta mais recente chega primeiro
    await waitFor(() => expect(text(actions())).not.toContain('Verificando pontos de atenção'))
    pending[0](answer(overview)) // a resposta antiga, com a ação, chega por último
    await new Promise(resolve => setTimeout(resolve, 30))
    expect(text(actions())).not.toContain('Há compromissos vencidos')
  })
})

describe('Visão Geral — alertas do orçamento PF', () => {
  it('o alerta de cartões acima da meta é o do orçamento PF e não muda ao trocar a conta (PF, PJ ou consolidado)', async () => {
    const pf = budgetControl({ scopeKind: 'pf' }); const bank = budgetControl({ scopeKind: 'account', pressures: [], recommendations: [] })
    mockApi({ overview: withSummary({ budgetControl: bank, pfBudgetControl: pf }) }); renderPage()
    const alerts = await screen.findByRole('region', { name: 'Próximas ações' })
    fireEvent.click(within(alerts).getByRole('button', { name: /Ver todas as \d+ ações/ }))
    const cards = () => within(screen.getByRole('region', { name: 'Próximas ações' })).getAllByText(/Cartões acima da meta mensal/)
    expect(cards()).toHaveLength(1)
    expect(text(cards()[0].closest('li'))).toContain('Excesso de R$ 400,00 sobre a meta.')
    for (const choice of ['account:pj', 'all']) {
      fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: choice } })
      await waitFor(() => expect(calls.some(url => url.includes(choice === 'all' ? 'scope=all' : 'accountId=pj'))).toBe(true))
      expect(text(within(screen.getByRole('region', { name: 'Próximas ações' })).getByText(/Cartões acima da meta mensal/).closest('li'))).toContain('Excesso de R$ 400,00')
    }
  })
})

describe('Visão Geral — contas, compromissos, metas e lançamentos', () => {
  it('contas com saldo de hoje; saldo indisponível dito como tal', async () => {
    mockApi(); renderPage()
    const accounts = await findCard('Contas')
    await waitFor(() => expect(text(accounts)).toContain('Conta A'))
    expect(text(accounts)).toContain('-R$ 150,25'); expect(text(accounts)).toContain('Sem saldo inicial configurado')
    fireEvent.click(within(accounts).getByRole('button', { name: 'Ver contas ›' }))
    expect(navigations.at(-1)?.section).toBe('accounts')
  })

  it('compromissos: vencidos primeiro, cada um leva à Agenda no mês do vencimento, com os totais do servidor', async () => {
    mockApi(); renderPage()
    const upcoming = await findCard('Próximos compromissos')
    const rows = await within(upcoming).findAllByRole('button', { name: /R\$/ })
    expect(plain(rows[0].getAttribute('aria-label'))).toMatch(/^Internet, 10\/10\/2026, R\$ 120,40, vencido$/)
    fireEvent.click(within(upcoming).getByRole('button', { name: /Financiamento - parcela 4/ }))
    expect(navigations.at(-1)).toEqual({ section: 'calendar', context: { month: '2026-11' } })
    expect(text(upcoming)).toContain('Agenda até 15/11/2026 (7)R$ 900,07'); expect(text(upcoming)).toContain('1 vencido(s) · R$ 120,40')
  })

  it('metas ativas aparecem compactas; sem metas ativas o bloco não ocupa espaço', async () => {
    mockApi(); renderPage()
    const goals = await findCard('Metas')
    expect(text(goals)).toContain('Reserva de emergência'); expect(text(goals)).toContain('25,0%')
    fireEvent.click(within(goals).getByRole('button', { name: 'Reserva de emergência' }))
    expect(navigations.at(-1)).toEqual({ section: 'goals', context: { goalId: 'g1' } })
    cleanup()
    mockApi({ overview: withSummary({ goals: { ...overview.goals, items: [], activeCount: 0 } }) }); renderPage()
    await loaded()
    expect(screen.queryByRole('region', { name: 'Metas' })).toBeNull()
  })

  it('últimos lançamentos em tabela, com receita e despesa ditas em texto, e "Ver todos" no período e conta', async () => {
    mockApi(); renderPage()
    const recent = await findCard('Últimos lançamentos')
    const rows = await within(recent).findAllByRole('row')
    expect(text(rows[1])).toContain('Mercado'); expect(text(rows[1])).toContain('Despesa − R$ 45,90')
    expect(text(rows[2])).toContain('Receita + R$ 2.000,00'); expect(text(rows[2])).toContain('Aguardando classificação')
    fireEvent.click(within(recent).getByRole('button', { name: 'Ver todos ›' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'transactions', context: { transactions: { from: '2026-10-01', to: '2026-10-16', accountId: 'pf' } } })
  })
})

describe('Visão Geral — filtros', () => {
  it('as setas e o campo trocam a competência; o seletor troca a conta; tudo recarrega no mesmo recorte', async () => {
    mockApi(); renderPage()
    await loaded()
    fireEvent.click(screen.getByRole('button', { name: 'Mês anterior' }))
    await waitFor(() => expect(calls.filter(url => url.includes('competence=2026-09')).length).toBe(2))
    expect((screen.getByLabelText('Competência') as HTMLInputElement).value).toBe('2026-09')
    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    await waitFor(() => expect(calls.some(url => url.includes('competence=2026-11'))).toBe(true))
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'account:pj' } })
    await waitFor(() => expect(calls.filter(url => url.includes('scope=account') && url.includes('accountId=pj')).length).toBe(3)) // plan-actual, overview e UM planejamento de seis meses, compartilhado pela evolução e pelo guia de configuração
  })

  it('lista a conta principal, o consolidado e cada conta; novo lançamento reutiliza Lançamentos', async () => {
    mockApi(); renderPage()
    const select = await screen.findByLabelText('Conta ou consolidado')
    await waitFor(() => expect(within(select).getAllByRole('option').length).toBeGreaterThanOrEqual(4))
    const options = within(select).getAllByRole('option').map(option => option.textContent)
    expect(options[0]).toBe('Conta principal · Conta A'); expect(options[1]).toBe('Todas as contas (consolidado)'); expect(options).toContain('Conta B · PJ')
    fireEvent.click(screen.getByRole('button', { name: '+ Novo lançamento' }))
    expect(navigations.at(-1)).toEqual({ section: 'transactions', context: { newTransaction: true } })
  })
})

describe('Visão Geral — estados e falhas', () => {
  it('falha de plan-actual: o cheque especial diz que falhou e recarrega só ele; o saldo do banco e o resultado continuam válidos', async () => {
    mockApi({ plan: new Response('{}', { status: 500 }) }); renderPage()
    const retry = await screen.findByRole('button', { name: 'Recarregar o cheque especial' })
    expect(text(card('Saldo e cheque especial'))).toContain('-R$ 1.000,00')
    expect(text(card('Resultado do mês'))).toContain('R$ 950,00') // a falha do plan-actual não derruba o resultado
    const before = calls.length
    fireEvent.click(retry)
    await waitFor(() => expect(calls.length).toBe(before + 1))
    expect(calls.at(-1)).toContain('/financial-dashboard/plan-actual')
  })

  it('falha da visão geral: o saldo cai para o calculado, com esse nome, e a recarga é só dela', async () => {
    mockApi({ overview: new Response('{}', { status: 500 }) }); renderPage()
    const retry = await screen.findByRole('button', { name: 'Recarregar a Visão Geral' })
    expect(text(card('Saldo e cheque especial'))).toContain('Saldo calculado')
    const before = calls.length
    fireEvent.click(retry)
    await waitFor(() => expect(calls.length).toBe(before + 1))
    expect(calls.at(-1)).toContain('/financial-dashboard/overview')
  })

  it('blocos indisponíveis dentro da resposta são ditos como indisponíveis, nunca vazios ou zero', async () => {
    mockApi({ overview: withSummary({ commitments: { ...overview.commitments, state: 'Unavailable' }, recent: { ...overview.recent, state: 'Unavailable', items: [], totalCount: 0 } }) }); renderPage()
    expect(await screen.findByText('Não foi possível carregar os compromissos da Agenda.')).toBeTruthy()
    expect(screen.getByText('Não foi possível carregar os lançamentos.')).toBeTruthy()
  })

  it('não apresenta dados fictícios: sem resposta da API, nenhum valor financeiro aparece', async () => {
    mockApi({ plan: new Response('{}', { status: 500 }), overview: new Response('{}', { status: 500 }), evolution: new Response('{}', { status: 500 }) }); renderPage()
    await waitFor(() => expect(screen.getAllByRole('alert').length).toBeGreaterThanOrEqual(3))
    expect(screen.queryByText(/R\$/)).toBeNull()
  })
})

describe('Visão Geral — saldo e cheque especial (T14.3)', () => {
  const limit = { contractedLimit: '5000.00', used: '1000.00', available: '4000.00', utilizationPercent: '20.0', exceedsLimit: false, projectedUsed: '400.00', projectedAvailable: '4600.00', projectedPercent: '8.0' }

  it('mostra limite contratado, utilizado, disponível, utilização, projeção do fechamento e diz que limite disponível não é saldo próprio', async () => {
    mockApi({ plan: overdraftPlan(), overview: withSummary({ monthlyResult: { ...monthlyResult, overdraft: limit } }) }); renderPage()
    const overdraft = await screen.findByLabelText('Cheque especial')
    await waitFor(() => expect(text(overdraft)).toContain('Limite disponível'))
    expect(text(overdraft)).toContain('R$ 1.000,00 de R$ 5.000,00'); expect(text(overdraft)).toMatch(/Limite disponívelR\$ 4\.000,00/); expect(text(overdraft)).toMatch(/Utilização20,0%/)
    expect(text(overdraft)).toMatch(/Utilizado no fechamentoR\$ 400,00 \(8,0%\)/); expect(text(overdraft)).toMatch(/Disponível no fechamentoR\$ 4\.600,00/)
    expect(text(overdraft)).toContain('Limite disponível é crédito do banco, não saldo próprio.')
    expect(text(card('Saldo e cheque especial'))).toContain('Saldo bancário observado'); expect(text(card('Saldo e cheque especial'))).toContain('Posição do banco em 04/11/2026')
  })

  it('meta cumprida só quando a projeção a cumpre; abaixo da meta é dito em texto', async () => {
    const base = decision()
    mockApi({ plan: overdraftPlan(), overview: withDecision({ recovery: { ...base.recovery, band: 'Met' } }) }); renderPage()
    expect(text(await screen.findByLabelText('Cheque especial'))).toContain('✓ Meta cumprida')
    cleanup()
    mockApi({ plan: overdraftPlan(), overview: withDecision({ recovery: { ...base.recovery, band: 'Critical' } }) }); renderPage()
    await waitFor(() => expect(text(screen.getByLabelText('Cheque especial'))).toContain('Abaixo da meta'))
  })

  it('meta pendente por cobertura insuficiente diz "Aguardando dados para definir a meta" com o motivo, sem valor inventado', async () => {
    const base = decision()
    mockApi({ plan: overdraftPlan(), overview: withDecision({ recovery: { ...base.recovery, state: 'Pending', reason: 'CardCoverageIncomplete', target: null, projectedRecovery: null, gapToTarget: null } }) }); renderPage()
    const overdraft = await screen.findByLabelText('Cheque especial')
    await waitFor(() => expect(text(overdraft)).toContain('Aguardando dados para definir a meta: há faturas de cartão sem conta pagadora identificada.'))
    expect(text(overdraft)).not.toContain('Meta cumprida'); expect(text(overdraft)).not.toMatch(/Meta do mês\s*R\$/)
  })

  it('o orçamento PF diz que considera todas as contas PF, e o orçamento detalha realizado, comprometido e estimado', async () => {
    mockApi(); renderPage()
    const budget = await findCard('Orçamento mensal')
    await waitFor(() => expect(text(budget)).toContain('O orçamento PF considera todas as contas PF, independentemente da conta escolhida no topo.'))
    expect(text(within(budget).getByLabelText('Composição de Cartões de crédito'))).toBe('Consumo realizadoR$ 2.000,00Consumo comprometidoR$ 500,00Consumo estimadoR$ 1.000,00')
  })
})

describe('Visão Geral — resumo do mês no topo (T14.3)', () => {
  const summary = () => card('Resumo do mês')

  it('mostra os dois saldos em destaque, receitas recebidas, consumo realizado e o orçamento PF com barra e "Restam"', async () => {
    mockApi(); renderPage()
    await loaded()
    const top = summary()
    expect(text(within(top).getByRole('article', { name: 'Saldo bancário observado' }))).toContain('-R$ 1.000,00'); expect(text(top)).toContain('Posição bancária de 04/11/2026')
    expect(text(within(top).getByRole('article', { name: 'Saldo projetado no fim do mês' }))).toContain('R$ 1.200,00'); expect(text(top)).toContain('Conta A · Fechamento de Outubro de 2026 · Projeção')
    expect(text(within(top).getByRole('article', { name: 'Receitas recebidas' }))).toContain('R$ 450,00')
    expect(text(within(top).getByRole('article', { name: 'Consumo realizado' }))).toContain('R$ 1.200,00'); expect(text(top)).toContain('Conta A · Outubro de 2026 · Realizado, inclui compras no cartão'); expect(text(top)).toContain('Conta A · Posição bancária de 04/11/2026')
    const budget = within(top).getByRole('group', { name: 'Orçamento PF do mês' })
    expect(text(budget)).toContain('60,0% da meta'); expect(text(budget)).toContain('Consumo projetado R$ 7.500,00 · Meta R$ 12.500,00'); expect(text(budget)).toContain('Restam R$ 5.000,00')
    expect(text(budget)).toContain('Todas as contas PF · Outubro de 2026 · Meta mensal · Independente da conta escolhida no topo'); expect(within(budget).getByRole('progressbar').getAttribute('aria-valuenow')).toBe('60')
    fireEvent.click(within(budget).getByRole('button', { name: 'Ver orçamento ›' })); expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('meta ultrapassada diz "acima da meta"; sem meta não inventa percentual; o consumo não é chamado de saída de caixa', async () => {
    const base = budgetControl()
    mockApi({ overview: withSummary({ pfBudgetControl: budgetControl({ scopeKind: 'pf', total: { ...base.total, status: 'Exceeded', excess: '400.00', margin: '0.00', projectedPercent: '103.2' } }) }) }); renderPage()
    await loaded()
    await waitFor(() => expect(text(summary())).toContain('R$ 400,00 acima da meta'))
    expect(text(summary())).not.toContain('Restam'); expect(text(summary())).not.toMatch(/Saídas de caixa/)
    cleanup()
    mockApi({ overview: withSummary({ pfBudgetControl: budgetControl({ scopeKind: 'pf', state: 'TargetNotDefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' } }) }) }); renderPage()
    await waitFor(() => expect(text(summary())).toContain('Meta não configurada'))
    expect(text(summary())).not.toMatch(/%/)
  })

  it('o resultado de caixa, a composição e o cheque especial ficam logo abaixo, recolhidos, e abrem pelo resumo', async () => {
    mockApi(); renderPage()
    await loaded()
    const more = document.querySelector('details.ov-more') as HTMLDetailsElement
    expect(more.open).toBe(false); expect(text(more.querySelector('summary'))).toBe('Resultado de caixa, composição e cheque especial')
    expect(more.querySelector('.ov-result')).toBeTruthy(); expect(more.querySelector('.ov-composition')).toBeTruthy(); expect(more.querySelector('.ov-balance-card')).toBeTruthy()
  })

  it('carregando: esqueletos nos indicadores, sem valor; erro: só o resumo recarrega', async () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})))
    renderPage()
    expect(summary().querySelectorAll('.ov-skeleton').length).toBeGreaterThan(0); expect(text(summary())).not.toMatch(/R\$/)
    cleanup(); vi.unstubAllGlobals()
    mockApi({ overview: new Response('{}', { status: 500 }) }); renderPage()
    expect(await within(summary()).findByRole('button', { name: 'Recarregar o resumo' })).toBeTruthy()
  })
})
