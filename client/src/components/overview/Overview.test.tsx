import { useState } from 'react'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Overview, type OverviewNavigation, type OverviewSection, type OverviewSelection } from './Overview'
import type { BudgetCardItem, BudgetCards, BudgetControl, DashboardOverview, ExpenseCategoryShare, ExpenseDistribution, FinancialDecision } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { SixMonthPlan } from '../../api/monthlyPlan'
import { planActual } from '../planActualFixtures'
import { sixMonths } from '../monthlyPlanFixtures'
import { overview as baseOverview } from './overviewFixtures'
import { decision } from './decisionFixtures'
import { budgetControl, group } from './budgetControlFixtures'

type Overrides = { plan?: PlanActualDashboard | Response; overview?: DashboardOverview | Response; evolution?: SixMonthPlan | Response; all?: ExpenseDistribution | Response; budgetCards?: BudgetCards | Response }
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
const withSummary = (over: Partial<DashboardOverview> = {}): DashboardOverview => ({ ...baseOverview, decision: decision(), budgetControl: budgetControl(), pfBudgetControl: budgetControl({ scopeKind: 'pf' }), expenseDistribution: distribution(), ...over })
const overview = withSummary()
const answer = (value: unknown) => value instanceof Response ? value.clone() : new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
function mockApi(overrides: Overrides = {}) {
  calls = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    calls.push(url)
    if (url.includes('/financial-dashboard/plan-actual')) return answer(overrides.plan ?? planActual)
    if (url.includes('/financial-dashboard/overview')) return answer(overrides.overview ?? overview)
    if (url.includes('/financial-dashboard/budget-cards')) return answer(overrides.budgetCards ?? budgetCards())
    if (url.includes('/financial-dashboard/expense-distribution')) return answer(overrides.all ?? distribution())
    if (url.includes('/monthly-plan/six-months')) return answer(overrides.evolution ?? sixMonths)
    return new Response('{}', { status: 404 })
  }))
}

const navigations: { section: OverviewSection; context?: OverviewNavigation }[] = []
function Harness({ initial = '2026-10' }: { initial?: string }) {
  const [selection, setSelection] = useState<OverviewSelection>({ competence: initial, scope: { kind: 'primary' } })
  return <Overview workspaceId="w" selection={selection} onSelectionChange={setSelection} onNavigate={(section, context) => { navigations.push({ section, context }) }} />
}
const renderPage = (initial?: string) => render(<Harness initial={initial} />)
const plain = (value: string | null | undefined) => (value ?? '').replace(/[\u00a0\u202f]/g, ' ')
const text = (element: Element | null) => plain(element?.textContent)
const title = (heading: Element) => [...heading.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join('').trim()
const card = (name: string) => screen.getByRole('region', { name })
const findCard = (name: string) => screen.findByRole('region', { name })
const plan = (changes: Partial<PlanActualDashboard>): PlanActualDashboard => ({ ...planActual, ...changes })
const control = (changes: Partial<BudgetControl>) => withSummary({ pfBudgetControl: budgetControl({ scopeKind: 'pf', ...changes }) })
const withDecision = (changes: Partial<FinancialDecision>) => withSummary({ decision: decision(changes) })
const loaded = () => screen.findByText('-R$ 1.000,00')
const overdraftPlan = (changes: Partial<PlanActualDashboard['overdraft']> = {}) => plan({ overdraft: {
  ...planActual.overdraft, eligible: true, state: 'Complete', usedAtStart: '3000.00', usedLatest: '1000.00', effectiveRecovery: '2000.00', projectedRecovery: '2600.00', contractedLimit: '5000.00', ...changes,
} })

beforeEach(() => { navigations.length = 0 })
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('Visão Geral — composição do dashboard Folio', () => {
  it('segue a hierarquia do Folio: saldo e resumo, evolução, orçamento e categorias, lançamentos e, na coluna de apoio, contas, compromissos e metas', async () => {
    mockApi(); renderPage()
    await loaded()
    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('Outubro de 2026')
    expect(screen.getAllByRole('heading', { level: 3 }).map(title)).toEqual([
      'Receitas do mês', 'Despesas do mês', 'Fechamento projetado', 'Evolução financeira', 'Orçamento mensal', 'Despesas por categoria', 'Últimos lançamentos', 'Contas', 'Próximos compromissos', 'Metas',
    ])
    expect(within(screen.getByRole('complementary', { name: 'Contas, compromissos e metas' })).getAllByRole('heading').map(title)).toEqual(['Contas', 'Próximos compromissos', 'Metas'])
  })

  it('os painéis antigos não existem, nem escondidos em expansões', async () => {
    mockApi(); renderPage()
    await loaded()
    for (const legacy of ['Minha situação financeira', 'Meu orçamento', 'Minha recuperação financeira', 'Onde preciso agir', 'Análises detalhadas', 'Fluxo de caixa completo',
      'Vou conseguir pagar minhas contas?', 'Margem segura para gastar à vista', 'Compromissos restantes', 'Recuperação detalhada', 'Receitas e despesas por competência']) expect(screen.queryByText(legacy)).toBeNull()
    // A única expansão é a tabela de valores do gráfico.
    expect([...document.querySelectorAll('details')].map(detail => text(detail.querySelector('summary')))).toEqual(['Ver valores em tabela'])
  })

  it('cada KPI tem uma única representação', async () => {
    mockApi(); renderPage()
    await loaded()
    for (const kpi of ['Receitas do mês', 'Despesas do mês', 'Fechamento projetado']) expect(screen.getAllByText(kpi)).toHaveLength(1)
    expect(screen.getAllByText('Saldo bancário')).toHaveLength(1)
    expect(screen.getAllByText('-R$ 1.000,00')).toHaveLength(1)
  })

  it('carrega o resumo, a visão geral e a evolução, uma vez cada', async () => {
    mockApi(); renderPage()
    await loaded()
    await screen.findByRole('img', { name: /Receitas e despesas de out\/26 a mar\/27/ })
    expect(calls.filter(url => url.includes('/plan-actual'))).toHaveLength(1)
    expect(calls.filter(url => url.includes('/financial-dashboard/overview'))).toHaveLength(1)
    expect(calls.filter(url => url.includes('/six-months') && url.includes('from=2026-07'))).toHaveLength(1)
    expect(calls.some(url => url.includes('overdraft-recovery') || url.includes('/monthly-plan/projection'))).toBe(false)
  })
})

describe('Visão Geral — saldo e resumo do mês', () => {
  it('o saldo bancário é a informação principal; receitas, despesas e fechamento vêm de plan-actual', async () => {
    mockApi(); renderPage()
    await loaded()
    const hero = card('Saldo e resumo do mês')
    expect(text(hero)).toContain('Saldo bancário'); expect(text(hero)).toContain('Saldo negativo'); expect(text(hero)).toContain('Posição do banco em 04/11/2026')
    expect(text(screen.getByRole('article', { name: 'Receitas do mês' }))).toContain('R$ 450,00')
    expect(text(screen.getByRole('article', { name: 'Receitas do mês' }))).toContain('Previsto a receber R$ 2.000,00')
    expect(text(screen.getByRole('article', { name: 'Despesas do mês' }))).toContain('R$ 1.200,00')
    expect(text(screen.getByRole('article', { name: 'Fechamento projetado' }))).toContain('R$ 1.200,00')
  })

  it('sem posição do banco, mostra o saldo calculado com esse nome, nunca zero', async () => {
    const base = decision()
    mockApi({ overview: withDecision({ context: { ...base.context, bankBalance: null, bankPositionDate: null } }) }); renderPage()
    const hero = await findCard('Saldo e resumo do mês')
    await waitFor(() => expect(text(hero)).toContain('Saldo calculado'))
    expect(text(hero)).toContain('O banco ainda não informou o saldo'); expect(text(hero)).toContain('R$ 3.000,00')
  })

  it('mês encerrado não fala em projeção; mês futuro avisa que não é o saldo do banco', async () => {
    mockApi({ plan: plan({ periodKind: 'Past' }) }); renderPage('2026-08')
    expect(await screen.findByRole('article', { name: 'Saldo no fechamento' })).toBeTruthy()
    expect(screen.queryByRole('article', { name: 'Fechamento projetado' })).toBeNull()
    cleanup()
    mockApi({ plan: plan({ periodKind: 'Future' }) }); renderPage('2026-12')
    expect(text(await screen.findByRole('article', { name: 'Fechamento projetado' }))).toContain('Projeção: não é o saldo do banco')
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

describe('Visão Geral — orçamento mensal e despesas por categoria', () => {
  it('os dois cards ficam um abaixo do outro, orçamento primeiro, na mesma área', async () => {
    mockApi(); renderPage()
    const budget = await findCard('Orçamento mensal')
    const area = budget.parentElement as HTMLElement
    expect(area.className).toBe('ov-budget-area')
    expect([...area.children].map(child => child.getAttribute('aria-labelledby'))).toEqual(['ov-budget-title', 'ov-distribution-title'])
  })
})

describe('Visão Geral — orçamento mensal', () => {
  const row = (name: string) => within(card('Orçamento mensal')).getByRole('listitem', { name })

  it('meta total, cartões e fora dos cartões com percentual projetado, total previsto / meta e diferença do servidor', async () => {
    mockApi(); renderPage()
    const budget = await findCard('Orçamento mensal')
    await waitFor(() => expect(text(budget)).toContain('Meta total'))
    expect(text(budget)).toContain('Orçamento PF · Todas as contas PF · Outubro de 2026')
    expect(text(row('Meta total'))).toBe('Meta total60,0%R$ 7.500,00 / R$ 12.500,00R$ 5.000,00 abaixo da meta')
    expect(text(row('Cartões de crédito'))).toBe('Cartões de crédito▾70,0%R$ 3.500,00 / R$ 5.000,00R$ 1.500,00 abaixo da meta')
    expect(text(row('Fora dos cartões'))).toBe('Fora dos cartões53,3%R$ 4.000,00 / R$ 7.500,00R$ 3.500,00 abaixo da meta')
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
    const outside = await waitFor(() => row('Fora dos cartões'))
    expect(text(outside)).toBe('Fora dos cartões—R$ 4.000,00 / R$ 0,00R$ 4.000,00 acima da meta')
  })

  it('competência sem meta: gastos visíveis, sem percentual nem meta zero, e acesso para configurar', async () => {
    const base = budgetControl()
    const none = { target: null, margin: null, excess: null, projectedPercent: null, realizedPercent: null, status: 'NoTarget' as const, state: 'TargetNotDefined' as const }
    mockApi({ overview: control({ state: 'TargetNotDefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, realizedPercent: null, status: 'NoTarget' },
      cards: group('Cards', none), outsideCards: group('OutsideCards', none) }) }); renderPage()
    const budget = await findCard('Orçamento mensal')
    await waitFor(() => expect(text(row('Meta total'))).toBe('Meta totalR$ 7.500,00Meta não configurada'))
    expect(text(budget)).not.toMatch(/%/); expect(text(budget)).not.toContain('R$ 0,00')
    fireEvent.click(within(budget).getByRole('button', { name: 'Configurar orçamento' }))
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('meta herdada ou exceção do mês: a meta exibida é a efetiva que o servidor resolveu', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ totalTargetOrigin: 'Customized', total: { ...base.total, target: '9000.00', margin: '1500.00', projectedPercent: '83.3' } }) }); renderPage()
    await findCard('Orçamento mensal')
    await waitFor(() => expect(text(row('Meta total'))).toBe('Meta total83,3%R$ 7.500,00 / R$ 9.000,00R$ 1.500,00 abaixo da meta'))
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
    await waitFor(() => expect(text(row('Meta total'))).toContain('R$ 12.500,00'))
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'account:pj' } })
    await waitFor(() => expect(calls.some(url => url.includes('accountId=pj'))).toBe(true))
    expect(text(card('Orçamento mensal'))).toContain('Orçamento PF · Todas as contas PF'); expect(text(row('Meta total'))).toContain('R$ 12.500,00')
    expect(text(card('Despesas por categoria'))).toContain('Conta A · Somente gastos já efetivados')
  })

  it('meta total ausente: a meta de cartões continua comparável e nada é inventado para o resto', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ state: 'TargetNotDefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, realizedPercent: null, status: 'NoTarget' },
      outsideCards: group('OutsideCards', { state: 'TargetNotDefined', target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' }) }) }); renderPage()
    await findCard('Orçamento mensal')
    await waitFor(() => expect(text(row('Meta total'))).toBe('Meta totalR$ 7.500,00Meta não configurada'))
    expect(text(row('Cartões de crédito'))).toContain('70,0%'); expect(text(row('Fora dos cartões'))).toBe('Fora dos cartõesR$ 4.000,00Meta não configurada')
    expect(text(card('Orçamento mensal'))).not.toContain('R$ 12.500,00')
  })
})

describe('Visão Geral — despesas por categoria', () => {
  it('total gasto, barra empilhada com os percentuais do servidor e as linhas, com "Sem categoria" separado', async () => {
    mockApi(); renderPage()
    const block = await findCard('Despesas por categoria')
    await waitFor(() => expect(text(block)).toContain('Moradia'))
    expect(text(block)).toContain('Somente gastos já efetivados'); expect(text(block)).toContain('Total gastoR$ 1.200,00')
    expect(within(block).getAllByRole('listitem').map(item => text(item))).toEqual(['MoradiaR$ 700,0058,3%', 'AlimentaçãoR$ 300,0025,0%', 'Sem categoriaR$ 200,0016,7%'])
    expect([...block.querySelectorAll<HTMLElement>('.ov-stack span')].map(segment => segment.style.flexBasis)).toEqual(['58.3%', '25%', '16.7%'])
    expect(block.querySelector('.ov-stack span:last-child')?.className).toBe('ov-cat-none')
  })

  it('mais de cinco categorias: as cinco maiores, "Outras categorias" e "Sem categoria", na ordem do servidor e com cores distintas', async () => {
    const rows = [...['Moradia', 'Alimentação', 'Transporte', 'Saúde', 'Educação'].map((name, i) => share(`c${i}`, name, `${900 - i * 100}.00`, `${18 - i}.0`)),
      share(null, 'Outras categorias', '1200.00', '24.0', 'Others', 3), share(null, 'Sem categoria', '1000.00', '20.0', 'Uncategorized', 0)]
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ total: '5000.00', reference: '5000.00', categories: rows, categoryCount: 8 }) }) }); renderPage()
    const block = await findCard('Despesas por categoria')
    await waitFor(() => expect(within(block).getAllByRole('listitem')).toHaveLength(7))
    expect(within(block).getAllByRole('listitem').map(item => text(item.querySelector('.ov-distribution__name')))).toEqual(['Moradia', 'Alimentação', 'Transporte', 'Saúde', 'Educação', 'Outras categorias (3 categorias)', 'Sem categoria'])
    const colors = [...block.querySelectorAll('.ov-stack span')].map(segment => segment.className)
    expect(new Set(colors.slice(0, 5)).size).toBe(5); expect(colors.slice(5)).toEqual(['ov-cat-other', 'ov-cat-none'])
  })

  it('"Ver todas" carrega a distribuição completa da mesma competência e conta, sob demanda, num diálogo que fecha com Esc', async () => {
    const all = distribution({ categories: [...distribution().categories.slice(0, 2), share('x', 'Lazer', '50.00', '4.2'), distribution().categories[2]], categoryCount: 3 })
    mockApi({ all }); renderPage()
    const block = await findCard('Despesas por categoria')
    expect(calls.some(url => url.includes('/expense-distribution'))).toBe(false)
    fireEvent.click(await within(block).findByRole('button', { name: 'Ver todas ›' }))
    const dialog = await screen.findByRole('dialog', { name: 'Todas as categorias' })
    await waitFor(() => expect(text(dialog)).toContain('Lazer'))
    expect(calls.filter(url => url.includes('/expense-distribution'))).toEqual(['/api/financial-workspaces/w/financial-dashboard/expense-distribution?competence=2026-10&scope=primary'])
    expect(document.activeElement?.textContent).toBe('Fechar')
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(document.activeElement?.textContent).toBe('Ver todas ›')
  })

  it('total que não confere é parcial, explica a diferença por bloco e não inventa categoria', async () => {
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ state: 'Partial', total: '1000.00', reference: '1200.00', reasons: ['TotalDiffersFromExpenses', 'BankBasisDiffers', 'CardBasisDiffers'],
      reconciliation: [{ bucket: 'Bank', distributed: '700.00', reference: '600.00', difference: '100.00' }, { bucket: 'Cards', distributed: '300.00', reference: '600.00', difference: '-300.00' }] }) }) }); renderPage()
    const partial = await findCard('Despesas por categoria')
    await waitFor(() => expect(text(partial)).toContain('Resultado parcial: o total difere das despesas do mês (R$ 1.200,00).'))
    expect(text(partial)).toContain('Banco: o indicador abate toda devolução recebida'); expect(text(partial)).toContain('Aqui R$ 700,00, no indicador R$ 600,00.')
    expect(text(partial)).toContain('Cartões: o indicador usa o total do banco nas faturas fechadas'); expect(within(partial).getAllByRole('listitem').filter(item => item.closest('.ov-distribution__list'))).toHaveLength(3)
  })

  it('devoluções e estornos aparecem à parte e a barra continua com os gastos, mesmo com líquido negativo', async () => {
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ total: '700.00', reference: '700.00', gross: '1100.00', adjustments: '-400.00',
      categories: [share('h', 'Moradia', '900.00', '81.8', 'Category', 1, '1000.00'), share('f', 'Alimentação', '-200.00', '9.1', 'Category', 1, '100.00')] }) }) }); renderPage()
    const block = await findCard('Despesas por categoria')
    await waitFor(() => expect(text(block)).toContain('Devoluções e estornos-R$ 400,00'))
    expect(block.querySelectorAll('.ov-stack span')).toHaveLength(2)
    expect(within(block).getAllByRole('listitem').map(item => text(item))).toEqual(['MoradiaR$ 1.000,00líquido R$ 900,0081,8%', 'AlimentaçãoR$ 100,00líquido -R$ 200,009,1%'])
    expect(text(block)).toContain('Total gastoR$ 700,00'); expect(text(block)).not.toContain('Resultado parcial')
  })

  it('mês sem despesas e bloco indisponível', async () => {
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ total: '0.00', reference: '0.00', categories: [], categoryCount: 0 }) }) }); renderPage()
    const empty = await findCard('Despesas por categoria')
    await waitFor(() => expect(text(empty)).toMatch(/Nenhuma despesa efetivada em Outubro de 2026/))
    expect(within(empty).queryByRole('button', { name: 'Ver todas ›' })).toBeNull()
    cleanup()
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ state: 'Unavailable', total: null, categories: [] }) }) }); renderPage()
    expect(await screen.findByText('Não foi possível agrupar as despesas deste mês.')).toBeTruthy()
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
    const rows = await screen.findAllByRole('button', { name: /Ver em Cartões/ })
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
    const alerts = await screen.findByRole('region', { name: 'Pontos de atenção' })
    const items = () => within(alerts).getAllByRole('listitem')
    expect(text(items()[0])).toContain('Há compromissos vencidos e não pagos. R$ 100,00 em aberto.')
    expect(text(items()[1])).toContain('A recuperação projetada está abaixo da meta.')
    expect(items()).toHaveLength(3)
    fireEvent.click(within(alerts).getByRole('button', { name: /Ver todos os 6 pontos de atenção/ }))
    expect(items()).toHaveLength(6)
    fireEvent.click(within(alerts).getByRole('button', { name: 'Revisar classificação ›' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'automation', context: { review: { kind: 'category' } } })
  })

  it('sem pontos de atenção, o bloco não ocupa espaço', async () => {
    const base = decision()
    mockApi({ overview: withSummary({ classification: { state: 'Complete', needsReview: 0, notEvaluated: 0 }, decision: { ...base, commitments: { ...base.commitments, overdue: '0.00' }, recovery: { ...base.recovery, gapToTarget: '100.00' } }, budgetControl: budgetControl({ pressures: [], recommendations: [] }), pfBudgetControl: budgetControl({ scopeKind: 'pf', pressures: [], recommendations: [] }) }) }); renderPage()
    await loaded()
    expect(screen.queryByRole('region', { name: 'Pontos de atenção' })).toBeNull()
  })
})

describe('Visão Geral — alertas do orçamento PF', () => {
  it('o alerta de cartões acima da meta é o do orçamento PF e não muda ao trocar a conta (PF, PJ ou consolidado)', async () => {
    const pf = budgetControl({ scopeKind: 'pf' }); const bank = budgetControl({ scopeKind: 'account', pressures: [], recommendations: [] })
    mockApi({ overview: withSummary({ budgetControl: bank, pfBudgetControl: pf }) }); renderPage()
    const alerts = await screen.findByRole('region', { name: 'Pontos de atenção' })
    fireEvent.click(within(alerts).getByRole('button', { name: /Ver todos os \d+ pontos de atenção/ }))
    const cards = () => within(screen.getByRole('region', { name: 'Pontos de atenção' })).getAllByText(/Cartões acima da meta mensal/)
    expect(cards()).toHaveLength(1)
    expect(text(cards()[0].closest('li'))).toContain('Excesso de R$ 400,00 sobre a meta.')
    for (const choice of ['account:pj', 'all']) {
      fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: choice } })
      await waitFor(() => expect(calls.some(url => url.includes(choice === 'all' ? 'scope=all' : 'accountId=pj'))).toBe(true))
      expect(text(within(screen.getByRole('region', { name: 'Pontos de atenção' })).getByText(/Cartões acima da meta mensal/).closest('li'))).toContain('Excesso de R$ 400,00')
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
    await waitFor(() => expect(calls.filter(url => url.includes('scope=account') && url.includes('accountId=pj')).length).toBe(3)) // plan-actual, overview e evolução
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
  it('falha de plan-actual: o resumo diz que falhou e recarrega só ele; o saldo do banco continua válido', async () => {
    mockApi({ plan: new Response('{}', { status: 500 }) }); renderPage()
    const retry = await screen.findByRole('button', { name: 'Recarregar o resumo' })
    expect(text(card('Saldo e resumo do mês'))).toContain('-R$ 1.000,00')
    const before = calls.length
    fireEvent.click(retry)
    await waitFor(() => expect(calls.length).toBe(before + 1))
    expect(calls.at(-1)).toContain('/financial-dashboard/plan-actual')
  })

  it('falha da visão geral: o saldo cai para o calculado, com esse nome, e a recarga é só dela', async () => {
    mockApi({ overview: new Response('{}', { status: 500 }) }); renderPage()
    const retry = await screen.findByRole('button', { name: 'Recarregar a Visão Geral' })
    expect(text(card('Saldo e resumo do mês'))).toContain('Saldo calculado')
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
