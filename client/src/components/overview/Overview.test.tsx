import { useState } from 'react'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Overview, type OverviewNavigation, type OverviewSection, type OverviewSelection } from './Overview'
import type { BudgetControl, DashboardOverview, ExpenseDistribution, FinancialDecision } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { SixMonthPlan } from '../../api/monthlyPlan'
import { planActual } from '../planActualFixtures'
import { sixMonths } from '../monthlyPlanFixtures'
import { overview as baseOverview } from './overviewFixtures'
import { decision } from './decisionFixtures'
import { budgetControl, group } from './budgetControlFixtures'

type Overrides = { plan?: PlanActualDashboard | Response; overview?: DashboardOverview | Response; evolution?: SixMonthPlan | Response }
let calls: string[] = []

const distribution = (over: Partial<ExpenseDistribution> = {}): ExpenseDistribution => ({
  state: 'Complete', competence: '2026-10', total: '1200.00', reference: '1200.00', reasons: [],
  categories: [{ categoryId: 'h', name: 'Moradia', amount: '700.00', percentage: '58.3' }, { categoryId: 'f', name: 'Alimentação', amount: '300.00', percentage: '25.0' }, { categoryId: null, name: 'Sem categoria', amount: '200.00', percentage: '16.7' }],
  ...over,
})
const withSummary = (over: Partial<DashboardOverview> = {}): DashboardOverview => ({ ...baseOverview, decision: decision(), budgetControl: budgetControl(), expenseDistribution: distribution(), ...over })
const overview = withSummary()
const answer = (value: unknown) => value instanceof Response ? value.clone() : new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
function mockApi(overrides: Overrides = {}) {
  calls = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    calls.push(url)
    if (url.includes('/financial-dashboard/plan-actual')) return answer(overrides.plan ?? planActual)
    if (url.includes('/financial-dashboard/overview')) return answer(overrides.overview ?? overview)
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
const control = (changes: Partial<BudgetControl>) => withSummary({ budgetControl: budgetControl(changes) })
const withDecision = (changes: Partial<FinancialDecision>) => withSummary({ decision: decision(changes) })
const loaded = () => screen.findByText('-R$ 1.000,00')
const overdraftPlan = (changes: Partial<PlanActualDashboard['overdraft']> = {}) => plan({ overdraft: {
  ...planActual.overdraft, eligible: true, state: 'Complete', usedAtStart: '3000.00', usedLatest: '1000.00', effectiveRecovery: '2000.00', projectedRecovery: '2600.00', contractedLimit: '5000.00', ...changes,
} })

beforeEach(() => { navigations.length = 0 })
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('Visão Geral — composição do dashboard Folio', () => {
  it('segue a hierarquia do Folio: saldo e resumo, evolução, distribuição e orçamento, lançamentos e, na coluna de apoio, contas, compromissos e metas', async () => {
    mockApi(); renderPage()
    await loaded()
    expect(screen.getByRole('heading', { level: 2 }).textContent).toContain('Outubro de 2026')
    expect(screen.getAllByRole('heading', { level: 3 }).map(title)).toEqual([
      'Receitas do mês', 'Despesas do mês', 'Fechamento projetado', 'Evolução financeira', 'Distribuição das despesas', 'Orçamento do mês', 'Últimos lançamentos', 'Contas', 'Próximos compromissos', 'Metas',
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

describe('Visão Geral — distribuição das despesas', () => {
  it('lista as categorias com valores e percentuais do servidor, inclusive "Sem categoria", e a barra usa os mesmos percentuais', async () => {
    mockApi(); renderPage()
    const block = await findCard('Distribuição das despesas')
    await waitFor(() => expect(text(block)).toContain('Moradia'))
    expect(within(block).getAllByRole('listitem').map(item => text(item))).toEqual(['MoradiaR$ 700,0058,3%', 'AlimentaçãoR$ 300,0025,0%', 'Sem categoriaR$ 200,0016,7%'])
    expect(text(block)).toContain('Total gastoR$ 1.200,00')
    expect([...block.querySelectorAll<HTMLElement>('.ov-stack span')].map(segment => segment.style.width)).toEqual(['58.3%', '25%', '16.7%'])
    fireEvent.click(within(block).getByRole('button', { name: 'Ver lançamentos ›' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'transactions', context: { transactions: { from: '2026-10-01', to: '2026-10-16', accountId: 'pf' } } })
  })

  it('total que não confere com Despesas do mês é dito, sem inventar categoria; percentuais impossíveis não aparecem', async () => {
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ state: 'Partial', total: '1000.00', reference: '1200.00', reasons: ['TotalDiffersFromExpenses'] }) }) }); renderPage()
    const partial = await findCard('Distribuição das despesas')
    await waitFor(() => expect(text(partial)).toContain('O total difere de Despesas do mês (R$ 1.200,00)'))
    expect(within(partial).getAllByRole('listitem')).toHaveLength(3)
    cleanup()
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ total: '400.00', reference: '400.00', reasons: ['PercentagesNotRepresentative'],
      categories: [{ categoryId: 'h', name: 'Moradia', amount: '700.00', percentage: null }, { categoryId: 'f', name: 'Alimentação', amount: '-300.00', percentage: null }] }) }) }); renderPage()
    const negative = await findCard('Distribuição das despesas')
    await waitFor(() => expect(text(negative)).toContain('-R$ 300,00'))
    expect(negative.querySelector('.ov-stack')).toBeNull(); expect(text(negative)).not.toContain('%')
    expect(text(negative)).toContain('Devoluções superam as compras')
  })

  it('mês sem despesas e bloco indisponível', async () => {
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ total: '0.00', reference: '0.00', categories: [] }) }) }); renderPage()
    expect(text(await findCard('Distribuição das despesas'))).toMatch(/Nenhuma despesa realizada em Outubro de 2026/)
    cleanup()
    mockApi({ overview: withSummary({ expenseDistribution: distribution({ state: 'Unavailable', total: null, categories: [] }) }) }); renderPage()
    expect(await screen.findByText('Não foi possível agrupar as despesas deste mês.')).toBeTruthy()
  })
})

describe('Visão Geral — orçamento do mês', () => {
  it('meta efetiva do orçamento recorrente: percentual usado, origem, grupos e margem do servidor', async () => {
    mockApi({ overview: control({ totalTargetOrigin: 'Inherited', totalTargetFrom: '2026-09' }) }); renderPage()
    const budget = await findCard('Orçamento do mês')
    await waitFor(() => expect(text(budget)).toContain('40,0% usado · meta R$ 12.500,00'))
    expect(text(budget)).toContain('Meta herdada do padrão, em vigor desde Setembro de 2026')
    expect(text(budget)).toContain('Cartões de créditoR$ 2.000,00 / R$ 5.000,00'); expect(text(budget)).toContain('Margem projetada de R$ 5.000,00')
    fireEvent.click(within(budget).getByRole('button', { name: 'Ver orçamento ›' }))
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('meta não configurada leva a configurar o orçamento', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ state: 'TargetNotDefined', totalTargetOrigin: 'Undefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' } }) }); renderPage()
    const budget = await findCard('Orçamento do mês')
    fireEvent.click(await within(budget).findByRole('button', { name: 'Configurar orçamento' }))
    expect(text(budget)).toContain('Meta não configurada')
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('conta isolada não recebe a meta global e oferece ver todas as contas', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ scopeKind: 'account', targetsAttributable: false, total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, status: 'NoTarget' },
      cards: group('Cards', { state: 'TargetNotAttributable', target: null }), outsideCards: group('OutsideCards', { state: 'TargetNotAttributable', target: null }) }) }); renderPage()
    const budget = await findCard('Orçamento do mês')
    const showAll = await within(budget).findByRole('button', { name: 'Ver todas as contas ›' })
    expect(text(budget)).toContain('Meta global'); expect(text(budget)).not.toContain('R$ 12.500,00')
    fireEvent.click(showAll)
    await waitFor(() => expect(calls.some(url => url.includes('scope=all'))).toBe(true))
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
    mockApi({ overview: withSummary({ classification: { state: 'Complete', needsReview: 0, notEvaluated: 0 }, decision: { ...base, commitments: { ...base.commitments, overdue: '0.00' }, recovery: { ...base.recovery, gapToTarget: '100.00' } }, budgetControl: budgetControl({ pressures: [], recommendations: [] }) }) }); renderPage()
    await loaded()
    expect(screen.queryByRole('region', { name: 'Pontos de atenção' })).toBeNull()
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
