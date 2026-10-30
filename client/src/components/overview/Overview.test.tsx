import { useState } from 'react'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Overview, type OverviewNavigation, type OverviewSection, type OverviewSelection } from './Overview'
import type { DashboardOverview } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { SixMonthPlan } from '../../api/monthlyPlan'
import { planActual } from '../planActualFixtures'
import { sixMonths } from '../monthlyPlanFixtures'
import { overview } from './overviewFixtures'

type Overrides = { plan?: PlanActualDashboard | Response; overview?: DashboardOverview | Response; evolution?: SixMonthPlan | Response }
let calls: string[] = []

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
const indicators = () => within(screen.getByRole('region', { name: 'Indicadores do mês' }))

beforeEach(() => { navigations.length = 0 })
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const plan = (changes: Partial<PlanActualDashboard>): PlanActualDashboard => ({ ...planActual, ...changes })

describe('Visão Geral — indicadores', () => {
  it('mostra um único conjunto de quatro indicadores, cada um com o seu significado e a sua referência de tempo', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo atual' })
    const articles = indicators().getAllByRole('article').map(article => article.getAttribute('aria-label'))
    expect(articles).toEqual(['Saldo atual', 'Receitas do mês', 'Despesas do mês', 'Fechamento projetado'])
    const balance = indicators().getByRole('article', { name: 'Saldo atual' })
    expect(within(balance).getByText('R$ 3.000,00')).toBeTruthy()
    expect(within(balance).getByText(/Posição em 16\/10\/2026/)).toBeTruthy()
    expect(within(indicators().getByRole('article', { name: 'Receitas do mês' })).getByText('R$ 450,00')).toBeTruthy()
    expect(within(indicators().getByRole('article', { name: 'Despesas do mês' })).getByText('R$ 1.200,00')).toBeTruthy()
    expect(within(indicators().getByRole('article', { name: 'Fechamento projetado' })).getByText('R$ 1.200,00')).toBeTruthy()
    // Cada valor aparece uma vez no resumo principal: nenhum indicador repetido em outro painel de totais.
    expect(screen.getAllByText('Saldo atual')).toHaveLength(1)
  })

  it('mês encerrado não fala em projeção; mês futuro avisa que só há compromissos conhecidos', async () => {
    mockApi({ plan: plan({ periodKind: 'Past' }) }); renderPage('2026-08')
    expect(await screen.findByRole('article', { name: 'Saldo no fechamento' })).toBeTruthy()
    expect(screen.queryByRole('article', { name: 'Fechamento projetado' })).toBeNull()
    expect(screen.getByText(/Mês encerrado · valor realizado no último dia/)).toBeTruthy()
    cleanup()
    mockApi({ plan: plan({ periodKind: 'Future' }) }); renderPage('2026-12')
    expect(await screen.findByRole('article', { name: 'Fechamento projetado' })).toBeTruthy()
    expect(screen.getByText(/Mês futuro · considera só compromissos conhecidos/)).toBeTruthy()
  })

  it('saldo indisponível não vira zero e o fechamento explica por que não foi calculado', async () => {
    mockApi({ plan: plan({ situation: { ...planActual.situation, state: 'Unavailable', realizedBalance: null, partialRealizedBalance: null, consideredAccounts: 0, projectedClosing: null, reasons: ['InsufficientHistory'] } }) })
    renderPage()
    const balance = await screen.findByRole('article', { name: 'Saldo atual' })
    expect(within(balance).getByText('Indisponível')).toBeTruthy()
    expect(within(balance).queryByText('R$ 0,00')).toBeNull()
    const closing = indicators().getByRole('article', { name: 'Fechamento projetado' })
    expect(within(closing).getByText('Indisponível')).toBeTruthy()
    expect(within(closing).getByText(/Histórico insuficiente/)).toBeTruthy()
  })

  it('saldo parcial é identificado como parcial e saldo negativo é dito em texto, não só pela cor', async () => {
    mockApi({ plan: plan({ situation: { ...planActual.situation, realizedBalance: null, partialRealizedBalance: '-80.00', consideredAccounts: 1, unavailableAccounts: 1, projectedClosing: '-500.00' } }) })
    renderPage()
    const balance = await screen.findByRole('article', { name: 'Saldo atual' })
    expect(within(balance).getByText('-R$ 80,00')).toBeTruthy()
    expect(within(balance).getByText('Parcial')).toBeTruthy()
    expect(within(balance).getByText('Saldo negativo')).toBeTruthy()
    expect(within(balance).getByText(/1 sem saldo calculável/)).toBeTruthy()
    expect(within(indicators().getByRole('article', { name: 'Fechamento projetado' })).getByText('Saldo negativo')).toBeTruthy()
  })
})

describe('Visão Geral — avisos', () => {
  it('a fila de classificação é um aviso próprio e não vira falha de sincronização', async () => {
    mockApi(); renderPage()
    const notice = await screen.findByText(/3 lançamento\(s\) pendentes de classificação/)
    expect(notice.closest('p')?.className).toContain('ov-notice--info')
    expect(screen.queryByText(/sincroniza/i)).toBeNull()
    fireEvent.click(within(notice.closest('p')!).getByRole('button', { name: 'Ver lançamentos' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'transactions' })
  })

  it('problemas de sincronização e de revisão aparecem em linguagem direta, com a explicação em detalhe expansível', async () => {
    mockApi({ plan: plan({
      sync: { autoEnabled: false, autoIntervalHours: 24, oldestSuccessAt: '2026-10-10T12:00:00Z', latestSuccessAt: '2026-10-10T12:00:00Z', sameMoment: true, stale: true, state: 'Failed', nextAttemptAt: null, connections: [{ connectionId: 'c1', label: 'Banco', status: 'Failed', lastSuccessAt: '2026-10-10T12:00:00Z', lastAttemptAt: null, nextAttemptAt: null, consentExpiringSoon: false }] },
      coverage: { ...planActual.coverage, complete: false, reviewCount: 2, reviewAffectsConfidence: true, reasons: ['MovementsInReview'] },
    }) })
    renderPage()
    expect(await screen.findByText(/A última sincronização com o banco falhou/)).toBeTruthy()
    expect(screen.getByText(/2 movimentação\(ões\) do banco aguardam revisão/)).toBeTruthy()
    expect(screen.getByText(/Projeção incompleta/)).toBeTruthy()
    const details = screen.getByText('Por que alguns valores são parciais').closest('details')!
    expect(details.open).toBe(false)
    expect(within(details).getByText(/movimentações do banco em revisão/)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Sincronizar agora' })).toBeTruthy()
  })

  it('sem conta principal avisa e leva para Contas', async () => {
    mockApi({ plan: plan({ scope: { ...planActual.scope, kind: 'all', primaryAccountId: null, primaryAccountName: null, primaryMissing: true } }) })
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Definir em Contas' }))
    expect(navigations.at(-1)?.section).toBe('accounts')
  })
})

describe('Visão Geral — filtros', () => {
  it('trocar a competência e a conta recarrega todos os blocos com o mesmo recorte', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo atual' })
    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2026-09' } })
    await waitFor(() => expect(calls.filter(url => url.includes('competence=2026-09'))).toHaveLength(2)) // plan-actual e overview
    expect(calls.some(url => url.includes('/monthly-plan/six-months') && url.includes('from=2026-06'))).toBe(true) // evolução: 3 meses antes + mês + 2
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'all' } })
    await waitFor(() => expect(calls.filter(url => url.includes('scope=all')).length).toBeGreaterThanOrEqual(3))
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'account:pj' } })
    await waitFor(() => expect(calls.filter(url => url.includes('scope=account') && url.includes('accountId=pj')).length).toBeGreaterThanOrEqual(3))
  })

  it('lista a conta principal, o consolidado e cada conta no seletor', async () => {
    mockApi(); renderPage()
    const select = await screen.findByLabelText('Conta ou consolidado')
    await waitFor(() => expect(within(select).getAllByRole('option').length).toBeGreaterThanOrEqual(4))
    const options = within(select).getAllByRole('option').map(option => option.textContent)
    expect(options[0]).toBe('Conta principal · Conta A')
    expect(options[1]).toBe('Todas as contas (consolidado)')
    expect(options).toContain('Conta B · PJ')
  })
})

describe('Visão Geral — blocos', () => {
  it('contas: principal identificada, saldo negativo em texto e saldo indisponível diferente de zero', async () => {
    mockApi(); renderPage()
    const accounts = await screen.findByRole('region', { name: 'Contas' })
    expect(within(accounts).getByText(/PF · Principal · Selecionada/)).toBeTruthy()
    expect(within(accounts).getByText('-R$ 150,25')).toBeTruthy()
    expect(within(accounts).getByText('negativo')).toBeTruthy()
    expect(within(accounts).getByText('Saldo indisponível')).toBeTruthy()
    expect(within(accounts).getByText('Sem saldo inicial configurado')).toBeTruthy()
    expect(within(accounts).queryByText('R$ 0,00')).toBeNull()
    fireEvent.click(within(accounts).getByRole('button', { name: /Conta A: R\$\s3\.000,00/ }))
    expect(navigations.at(-1)?.section).toBe('accounts')
  })

  it('compromissos: vencidos separados dos próximos, com o horizonte e a abertura da Agenda no mês do item', async () => {
    mockApi(); renderPage()
    const block = await screen.findByRole('region', { name: 'Próximos compromissos' })
    expect(within(block).getByText(/próximos 30 dias \(até 15\/11\/2026\)/)).toBeTruthy()
    const overdue = within(block).getByRole('heading', { name: /Vencidos/ }).closest('.ov-group') as HTMLElement
    expect(within(overdue).getByText('Internet')).toBeTruthy()
    expect(within(overdue).getByText(/venceu há 6 dias/)).toBeTruthy()
    const upcoming = within(block).getByRole('heading', { name: /Próximos 30 dias/ }).closest('.ov-group') as HTMLElement
    expect(within(upcoming).getByText(/vence hoje/)).toBeTruthy()
    expect(within(upcoming).getByText(/vence em 20 dias/)).toBeTruthy()
    expect(within(block).getByText(/\+ 5 na Agenda/)).toBeTruthy() // 7 no total, 2 exibidos
    fireEvent.click(within(upcoming).getByRole('button', { name: /Financiamento - parcela 4/ }))
    expect(navigations.at(-1)).toEqual({ section: 'calendar', context: { month: '2026-11' } })
    fireEvent.click(within(block).getByRole('button', { name: 'Abrir Agenda' }))
    expect(navigations.at(-1)).toEqual({ section: 'calendar', context: { month: '2026-10' } })
  })

  it('compromissos vazios dizem que não há nada pendente', async () => {
    const empty = { count: 0, total: '0.00', items: [] }
    mockApi({ overview: { ...overview, commitments: { ...overview.commitments, overdue: empty, upcoming: empty } } }); renderPage()
    expect(await screen.findByText(/Nenhuma despesa pendente na Agenda neste horizonte/)).toBeTruthy()
  })

  it('orçamento: fixas, todos os cartões e categorias aparecem à parte, cada meta com limite, consumido e disponível', async () => {
    mockApi(); renderPage()
    const budget = await screen.findByRole('region', { name: /Orçamento de/ })
    expect(within(budget).getByText(/1 meta\(s\) de categoria excedida\(s\)/)).toBeTruthy()
    const fixed = within(budget).getByRole('progressbar', { name: 'Consumo de Despesas fixas' })
    expect(fixed.getAttribute('aria-valuetext')).toBe('90,0%')
    expect(within(budget).getByText(/R\$ 540,00 de R\$ 600,00 · 90,0%/)).toBeTruthy()
    expect(within(budget).getByText('Disponível R$ 60,00')).toBeTruthy()
    expect(within(budget).getByText(/R\$ 4\.200,00 de R\$ 5\.000,00 · 84,0%/)).toBeTruthy() // todos os cartões somados, um limite só
    expect(within(budget).getByText(/Faturas com vencimento em/)).toBeTruthy()
    expect(within(budget).getByText(/R\$ 1\.200,00 de R\$ 1\.000,00 · 120,0%/)).toBeTruthy()
    expect(within(budget).getByText('Excedido em R$ 200,00')).toBeTruthy()
    expect(within(budget).getByText('Excedido')).toBeTruthy() // o estado vem em texto
    expect(within(budget).getByText(/Dados incompletos: competência de cartão estimada/)).toBeTruthy()
    expect(within(budget).getByText(/seus valores não são somados/)).toBeTruthy()
    expect(within(budget).queryByText(/Total do orçamento/i)).toBeNull() // nenhuma soma entre perspectivas
  })

  it('orçamento: cada grupo abre o orçamento da competência já nele', async () => {
    mockApi(); renderPage()
    const budget = await screen.findByRole('region', { name: /Orçamento de/ })
    const open = within(budget).getAllByRole('button', { name: 'Abrir composição' })

    fireEvent.click(open[0])
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10', budgetGroup: 'fixed' } })
    fireEvent.click(open[1])
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10', budgetGroup: 'cards' } })
    fireEvent.click(open[2])
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10', budgetGroup: 'categories' } })
    fireEvent.click(within(budget).getByRole('button', { name: 'Abrir orçamento' }))
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('orçamento: sem metas no mês mostra o estado vazio com a ação correspondente', async () => {
    mockApi({ overview: { ...overview, budget: { state: 'Complete', hasBudget: false, budgetId: null, competence: '2026-10', fixed: null, cards: null, categories: { count: 0, planned: null, consumed: null, exceededCount: 0, approachingCount: 0, targets: [] } } } })
    renderPage()
    const budget = await screen.findByRole('region', { name: /Orçamento de/ })
    expect(await within(budget).findByText(/Nenhuma meta definida para/)).toBeTruthy()
    fireEvent.click(within(budget).getByRole('button', { name: 'Definir metas' }))
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10' } })
  })

  it('metas: nome, acumulado, alvo e progresso, com acesso à página de metas', async () => {
    mockApi(); renderPage()
    const goals = await screen.findByRole('region', { name: 'Metas' })
    expect(within(goals).getByText('Reserva de emergência')).toBeTruthy()
    expect(within(goals).getByText(/R\$ 2\.500,00 de R\$ 10\.000,00/)).toBeTruthy()
    expect(within(goals).getByRole('progressbar', { name: 'Progresso de Reserva de emergência' }).getAttribute('aria-valuenow')).toBe('25')
    fireEvent.click(within(goals).getByRole('button', { name: 'Ver metas' }))
    expect(navigations.at(-1)?.section).toBe('goals')
  })

  it('últimos lançamentos: período, escopo e exclusão de pagamento de fatura ficam explícitos; classificação pendente é marcada', async () => {
    mockApi(); renderPage()
    const recent = await screen.findByRole('region', { name: 'Últimos lançamentos' })
    expect(within(recent).getByText(/Outubro de 2026 \(até 16\/10\/2026\) · Conta principal · Conta A · sem pagamentos de fatura/)).toBeTruthy()
    expect(within(recent).getByText('− R$ 45,90')).toBeTruthy()
    expect(within(recent).getByText('+ R$ 2.000,00')).toBeTruthy()
    expect(within(recent).getByText('Aguardando classificação')).toBeTruthy()
    expect(within(recent).getByText(/Mostrando 2 de 9 lançamentos do período/)).toBeTruthy()
    fireEvent.click(within(recent).getByRole('button', { name: 'Ver todos' }))
    expect(navigations.at(-1)).toEqual({ section: 'transactions', context: { transactions: { from: '2026-10-01', to: '2026-10-16', accountId: 'pf' }, newTransaction: false } })
  })

  it('ação principal: novo lançamento reutiliza o fluxo de Lançamentos', async () => {
    mockApi(); renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Novo lançamento' }))
    expect(navigations.at(-1)).toEqual({ section: 'transactions', context: { newTransaction: true } })
  })

  it('evolução: separa realizado de projetado e oferece a alternativa em tabela', async () => {
    mockApi(); renderPage()
    const chart = await screen.findByRole('img', { name: /Receitas e despesas de out\/26 a mar\/27/ })
    expect(chart.getAttribute('aria-label')).toMatch(/dez\/26: receitas R\$\s0,00, despesas R\$\s5\.000,00 \(projeção\)/)
    const section = chart.closest('section')!
    expect(within(section).getByText('Projetado')).toBeTruthy()
    const table = within(section).getByRole('table', { hidden: true })
    expect(within(table).getAllByRole('row', { hidden: true })).toHaveLength(7) // cabeçalho + 6 meses
    expect(within(table).getByText('Mês corrente')).toBeTruthy()
    expect(within(table).getAllByText('Mês futuro').length).toBe(5)
  })

  it('evolução sem movimento diz isso em vez de desenhar barras vazias', async () => {
    const zero = { ...sixMonths, months: sixMonths.months.map(month => ({ ...month, projectedIncome: '0.00', realizedIncome: '0.00', projectedExpense: '0.00', realizedExpense: '0.00' })) }
    mockApi({ evolution: zero }); renderPage()
    expect(await screen.findByText('Sem receitas nem despesas neste período.')).toBeTruthy()
  })
})

describe('Visão Geral — estados e falhas', () => {
  it('mostra carregamento por bloco e preenche cada um quando chega', async () => {
    mockApi(); renderPage()
    expect(screen.getByText('Carregando indicadores…')).toBeTruthy()
    expect(await screen.findByRole('article', { name: 'Saldo atual' })).toBeTruthy()
    expect(await screen.findByRole('region', { name: 'Contas' })).toBeTruthy()
  })

  it('a falha de um bloco não derruba os outros e a recarga é só dele', async () => {
    mockApi({ overview: new Response('{}', { status: 500 }) }); renderPage()
    expect(await screen.findByRole('article', { name: 'Saldo atual' })).toBeTruthy() // indicadores seguem válidos
    expect(await screen.findByRole('img', { name: /Receitas e despesas/ })).toBeTruthy() // evolução também
    const alert = await screen.findByRole('button', { name: 'Recarregar' })
    const before = calls.length
    fireEvent.click(alert)
    await waitFor(() => expect(calls.length).toBe(before + 1))
    expect(calls.at(-1)).toContain('/financial-dashboard/overview')
  })

  it('um bloco indisponível dentro da resposta é dito como indisponível, nunca como vazio ou zero', async () => {
    mockApi({ overview: { ...overview, accounts: { ...overview.accounts, state: 'Unavailable', items: [] }, goals: { ...overview.goals, state: 'Unavailable', items: [], activeCount: 0 },
      commitments: { ...overview.commitments, state: 'Unavailable' }, recent: { ...overview.recent, state: 'Unavailable', items: [], totalCount: 0 }, budget: { ...overview.budget, state: 'Unavailable', hasBudget: false, fixed: null, cards: null, categories: { count: 0, planned: null, consumed: null, exceededCount: 0, approachingCount: 0, targets: [] } } } })
    renderPage()
    expect(await screen.findByText('Não foi possível carregar as contas.')).toBeTruthy()
    expect(screen.getByText('Não foi possível carregar as metas.')).toBeTruthy()
    expect(screen.getByText('Não foi possível carregar os compromissos da Agenda.')).toBeTruthy()
    expect(screen.getByText('Não foi possível carregar os lançamentos.')).toBeTruthy()
    expect(screen.getByText(/Não foi possível carregar o orçamento deste mês/)).toBeTruthy()
    expect(screen.queryByText(/Nenhum orçamento definido/)).toBeNull()
  })

  it('estados vazios orientam: contas, metas e lançamentos', async () => {
    mockApi({ overview: { ...overview, accounts: { ...overview.accounts, items: [] }, goals: { ...overview.goals, items: [], activeCount: 0 }, recent: { ...overview.recent, items: [], totalCount: 0 } } })
    renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Cadastrar conta' }))
    expect(navigations.at(-1)?.section).toBe('accounts')
    fireEvent.click(screen.getByRole('button', { name: 'Criar meta' }))
    expect(navigations.at(-1)?.section).toBe('goals')
  })

  it('não apresenta dados fictícios: sem resposta da API, nenhum valor financeiro aparece', async () => {
    mockApi({ plan: new Response('{}', { status: 500 }), overview: new Response('{}', { status: 500 }), evolution: new Response('{}', { status: 500 }) })
    renderPage()
    await waitFor(() => expect(screen.getAllByRole('alert').length).toBeGreaterThanOrEqual(3))
    expect(screen.queryByText(/R\$/)).toBeNull()
  })
})
