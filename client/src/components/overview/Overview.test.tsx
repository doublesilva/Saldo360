import { useState } from 'react'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Overview, type OverviewNavigation, type OverviewSection, type OverviewSelection } from './Overview'
import type { BudgetControl, DashboardOverview, FinancialDecision } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { SixMonthPlan } from '../../api/monthlyPlan'
import { planActual } from '../planActualFixtures'
import { sixMonths } from '../monthlyPlanFixtures'
import { overview as baseOverview } from './overviewFixtures'
import { decision } from './decisionFixtures'
import { budgetControl, group } from './budgetControlFixtures'

type Overrides = { plan?: PlanActualDashboard | Response; overview?: DashboardOverview | Response; evolution?: SixMonthPlan | Response }
let calls: string[] = []

const withSummary = (over: Partial<DashboardOverview> = {}): DashboardOverview => ({ ...baseOverview, decision: decision(), budgetControl: budgetControl(), ...over })
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
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
const area = (name: string) => screen.getByRole('region', { name })
const openSection = (title: string) => fireEvent.click(screen.getByText(title, { selector: '.ov-disclosure__title' }))
const plan = (changes: Partial<PlanActualDashboard>): PlanActualDashboard => ({ ...planActual, ...changes })
const control = (changes: Partial<BudgetControl>) => withSummary({ budgetControl: budgetControl(changes) })
const withDecision = (changes: Partial<FinancialDecision>) => withSummary({ decision: decision(changes) })

beforeEach(() => { navigations.length = 0 })
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('Visão Geral — arquitetura de informação (T12.2)', () => {
  it('apresenta as quatro áreas na ordem situação, orçamento, recuperação e ação, antes das análises detalhadas', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    const headings = screen.getAllByRole('heading', { level: 2 }).map(heading => heading.textContent)
    expect(headings).toEqual(['Minha situação financeira', 'Meu orçamento', 'Minha recuperação financeira', 'Onde preciso agir', 'Análises detalhadas'])
  })

  it('mostra a competência e a conta analisadas no cabeçalho e no contexto da situação', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    expect(screen.getAllByText(/Outubro de 2026/).length).toBeGreaterThanOrEqual(2)
    expect(text(area('Minha situação financeira'))).toContain('Conta principal · Conta A')
    expect((screen.getByLabelText('Competência') as HTMLInputElement).value).toBe('2026-10')
  })

  it('não repete indicadores no resumo: receitas, despesas e o saldo calculado só existem nas análises detalhadas', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    for (const repeated of ['Saldo atual', 'Receitas do mês', 'Despesas do mês', 'Evolução financeira', 'Próximos compromissos', 'Últimos lançamentos']) expect(screen.queryByText(repeated, { selector: 'h3, h4, [aria-label]' })).toBeNull()
    expect(screen.getAllByText('Compromissos restantes')).toHaveLength(1)
    expect(screen.getAllByText('Fechamento projetado')).toHaveLength(1)
    expect(screen.getAllByRole('article').map(article => article.getAttribute('aria-label'))).toEqual([
      'Saldo bancário observado', 'Compromissos restantes', 'Fechamento projetado', 'Vou conseguir pagar minhas contas?', 'Orçamento mensal', 'Meta de recuperação',
    ])
  })

  it('as análises detalhadas começam fechadas, sem conteúdo montado nem consultas de gráficos e horizontes longos', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    const titles = ['Fluxo de caixa completo', 'Receitas e despesas por competência', 'Evolução financeira', 'Recuperação detalhada', 'Metas por categoria e despesas fixas', 'Próximos compromissos por vencimento', 'Últimos lançamentos', 'Metas e objetivos']
    expect(screen.getAllByText(/./, { selector: '.ov-disclosure__title' }).map(title => title.textContent).slice(-8)).toEqual(titles)
    for (const detail of document.querySelectorAll('#ov-details-title ~ details, .ov-details-area details')) expect((detail as HTMLDetailsElement).open).toBe(false)
    expect(screen.queryByRole('region', { name: 'Próximos compromissos' })).toBeNull()
    expect(screen.queryByRole('img', { name: /Receitas e despesas/ })).toBeNull()
    expect(calls.filter(url => url.includes('/financial-dashboard/'))).toHaveLength(2)
    expect(calls.some(url => url.includes('six-months') || url.includes('/projection'))).toBe(false)
  })

  it('abrir a evolução faz a consulta só então; trocar a competência não leva dados antigos', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    openSection('Evolução financeira')
    expect(await screen.findByRole('img', { name: /Receitas e despesas de out\/26 a mar\/27/ })).toBeTruthy()
    expect(calls.filter(url => url.includes('/monthly-plan/six-months') && url.includes('from=2026-07'))).toHaveLength(1)
    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2026-09' } })
    await waitFor(() => expect(calls.some(url => url.includes('/monthly-plan/six-months') && url.includes('from=2026-06'))).toBe(true))
  })

  it('cada seção das análises detalhadas abre com o seu conteúdo e leva ao módulo correspondente', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })

    openSection('Próximos compromissos por vencimento')
    const commitments = within(area('Próximos compromissos'))
    expect(commitments.getByText('Internet')).toBeTruthy()
    fireEvent.click(commitments.getByRole('button', { name: /Financiamento - parcela 4/ }))
    expect(navigations.at(-1)).toEqual({ section: 'calendar', context: { month: '2026-11' } })

    openSection('Últimos lançamentos')
    const recent = within(area('Últimos lançamentos'))
    expect(recent.getByText(/Mostrando 2 de 9 lançamentos do período/)).toBeTruthy()
    fireEvent.click(recent.getByRole('button', { name: 'Ver todos' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'transactions', context: { transactions: { from: '2026-10-01', to: '2026-10-16', accountId: 'pf' } } })

    openSection('Metas e objetivos')
    fireEvent.click(within(area('Metas')).getByRole('button', { name: 'Ver metas' }))
    expect(navigations.at(-1)?.section).toBe('goals')

    openSection('Metas por categoria e despesas fixas')
    const budget = within(area('Orçamento de Outubro de 2026'))
    expect(budget.getByText('Excedido em R$ 200,00')).toBeTruthy()
    fireEvent.click(budget.getAllByRole('button', { name: 'Abrir composição' })[2])
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10', budgetGroup: 'categories' } })

    openSection('Receitas e despesas por competência')
    expect(screen.getByRole('article', { name: 'Receitas do mês' })).toBeTruthy(); expect(screen.getByRole('article', { name: 'Despesas do mês' })).toBeTruthy()
  })

  it('o resumo leva a Agenda, Contas, Cartões, Orçamento e Planejamento', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    fireEvent.click(within(screen.getByRole('article', { name: 'Compromissos restantes' })).getByRole('button', { name: 'Ver na Agenda' }))
    fireEvent.click(within(screen.getByRole('article', { name: 'Saldo bancário observado' })).getByRole('button', { name: 'Ver contas' }))
    fireEvent.click(within(area('Meu orçamento')).getByRole('button', { name: 'Abrir orçamento completo' }))
    fireEvent.click(within(area('Minha recuperação financeira')).getByRole('button', { name: /Ver projeção de 6, 13, 24 ou 36 meses/ }))
    const actions = within(area('Onde preciso agir'))
    fireEvent.click(actions.getByRole('button', { name: 'Consultar faturas' }))
    expect(navigations.map(item => item.section)).toEqual(['calendar', 'accounts', 'budgets', 'planning', 'cards'])
    expect(navigations[0].context).toEqual({ month: '2026-10' })
    expect(navigations[2].context).toEqual({ competence: '2026-10', budgetGroup: undefined })
    expect(navigations[3].context).toEqual({ planningTab: 'projection' })
  })
})

describe('Visão Geral — situação, cobertura e saldos', () => {
  it('saldo negativo é dito em texto; o fechamento projetado vem de plan-actual', async () => {
    mockApi(); renderPage()
    const balance = await screen.findByRole('article', { name: 'Saldo bancário observado' })
    expect(text(balance)).toContain('-R$ 1.000,00'); expect(text(balance)).toContain('Saldo negativo')
    expect(text(screen.getByRole('article', { name: 'Fechamento projetado' }))).toContain('R$ 1.200,00')
  })

  it('saldo positivo e saldo bancário indisponível', async () => {
    const base = decision()
    mockApi({ overview: withSummary({ decision: { ...base, context: { ...base.context, bankBalance: '800.00', overdraftUsed: null } } }) }); renderPage()
    expect(text(await screen.findByRole('article', { name: 'Saldo bancário observado' }))).toContain('R$ 800,00')
    cleanup()
    mockApi({ overview: withSummary({ decision: { ...base, context: { ...base.context, bankBalance: null, bankPositionDate: null } } }) }); renderPage()
    const balance = await screen.findByRole('article', { name: 'Saldo bancário observado' })
    expect(text(balance)).toContain('Sem posição do banco'); expect(text(balance)).not.toMatch(/R\$ 0,00/)
  })

  it('mês encerrado não fala em projeção; mês futuro avisa que não é saldo do banco', async () => {
    mockApi({ plan: plan({ periodKind: 'Past' }) }); renderPage('2026-08')
    expect(await screen.findByRole('article', { name: 'Saldo no fechamento' })).toBeTruthy()
    expect(screen.queryByRole('article', { name: 'Fechamento projetado' })).toBeNull()
    cleanup()
    mockApi({ plan: plan({ periodKind: 'Future' }) }); renderPage('2026-12')
    expect(text(await screen.findByRole('article', { name: 'Fechamento projetado' }))).toContain('Projetado: não é o saldo do banco')
  })

  it('cobertura insuficiente e margem segura indisponível: a conclusão e o alerta ficam visíveis, e a margem nunca vira zero', async () => {
    const base = decision()
    mockApi({ overview: withDecision({
      coverage: { ...base.coverage, state: 'Insufficient', firstBeyondLimitDate: '2026-10-22', firstNegativeDate: '2026-10-20', deficit: '500.00' },
      safeMargin: { ...base.safeMargin, state: 'Unavailable', amount: null, bindingConstraint: null, constraints: [], reasons: ['ConsolidatedScope'] },
    }) }); renderPage()
    const coverage = await screen.findByRole('article', { name: 'Vou conseguir pagar minhas contas?' })
    expect(text(coverage)).toContain('Há risco de insuficiência em 22/10/2026.'); expect(text(coverage)).toContain('Insuficiente')
    expect(text(screen.getByLabelText('Margem segura para gastar à vista'))).toContain('ainda não é possível calcular')
    const alert = within(area('Onde preciso agir')).getByRole('listitem', { name: 'Há risco de insuficiência em 22/10/2026.' })
    expect(text(alert)).toContain('Déficit estimado de R$ 500,00.'); expect(text(alert)).toContain('Risco de caixa ou da meta')
    expect(screen.queryByText(/Detalhes de cobertura/)?.closest('details')?.open).toBe(false)
  })

  it('um alerta de risco abre o detalhe correspondente (cobertura)', async () => {
    const base = decision()
    mockApi({ overview: withDecision({ coverage: { ...base.coverage, state: 'CoveredWithCredit' } }) }); renderPage()
    const alert = await screen.findByRole('listitem', { name: 'Os pagamentos dependem de utilização do cheque especial.' })
    fireEvent.click(within(alert).getByRole('button', { name: 'Ver detalhe' }))
    expect(await screen.findByText('Obrigações consideradas')).toBeTruthy()
  })
})

describe('Visão Geral — orçamento', () => {
  it('meta herdada do padrão, personalizada ou não configurada, vinda do resolvedor único', async () => {
    mockApi({ overview: control({ totalTargetOrigin: 'Inherited', totalTargetFrom: '2026-09' }) }); renderPage()
    expect(text(await screen.findByRole('article', { name: 'Orçamento mensal' }))).toContain('Meta herdada do padrão, em vigor desde Setembro de 2026')
    cleanup()
    mockApi({ overview: control({ totalTargetOrigin: 'Customized' }) }); renderPage()
    expect(text(await screen.findByRole('article', { name: 'Orçamento mensal' }))).toContain('Meta personalizada neste mês')
  })

  it('competência sem meta configurada diz isso e leva a definir as metas', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ state: 'TargetNotDefined', totalTargetOrigin: 'Undefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' },
      cards: group('Cards', { state: 'TargetNotDefined', target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' }) }) }); renderPage()
    const summary = await screen.findByRole('article', { name: 'Orçamento mensal' })
    expect(text(summary)).toContain('Meta não configurada'); expect(text(summary)).not.toContain('Sem meta')
    fireEvent.click(within(summary).getByRole('button', { name: 'Definir metas' }))
    expect(navigations.at(-1)).toEqual({ section: 'budgets', context: { competence: '2026-10', budgetGroup: undefined } })
  })

  it('conta individual não recebe a meta global; o consolidado a mostra', async () => {
    const base = budgetControl()
    mockApi({ overview: control({ scopeKind: 'account', targetsAttributable: false, notes: ['TargetsGlobalOnly'], totalTargetOrigin: 'Inherited', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, status: 'NoTarget' },
      cards: group('Cards', { state: 'TargetNotAttributable', target: null, margin: null, excess: null, status: 'NoTarget' }), outsideCards: group('OutsideCards', { state: 'TargetNotAttributable', target: null, margin: null, excess: null, status: 'NoTarget' }) }) }); renderPage()
    const single = await screen.findByRole('article', { name: 'Orçamento mensal' })
    expect(text(single)).toContain('Meta global'); expect(text(single)).not.toContain('R$ 12.500,00'); expect(text(single)).not.toContain('herdada')
    cleanup()
    mockApi(); renderPage()
    expect(text(await screen.findByRole('article', { name: 'Orçamento mensal' }))).toContain('R$ 12.500,00')
  })
})

describe('Visão Geral — recuperação', () => {
  it('meta fixada e registrada, com a data e o aviso de que não é recalculada', async () => {
    mockApi(); renderPage()
    const recovery = await screen.findByRole('article', { name: 'Meta de recuperação' })
    expect(text(recovery)).toContain('Meta fixada e registrada'); expect(text(recovery)).toContain('Não é recalculada.')
    expect(text(recovery)).toMatch(/Meta de recuperação\s*R\$ 2\.500,00/); expect(text(recovery)).toMatch(/Cheque especial utilizado\s*R\$ 15\.000,00/)
    expect(text(recovery)).toMatch(/Recuperação realizada\s*R\$ 500,00/); expect(text(recovery)).toMatch(/Recuperação projetada\s*R\$ 1\.000,00/)
    expect(text(recovery)).toMatch(/Desvio em relação à meta\s*-R\$ 1\.500,00abaixo da meta/)
  })

  it('meta provisória nunca é apresentada como fixada, e a ausência de meta não inventa valor', async () => {
    const base = decision()
    mockApi({ overview: withDecision({ recovery: { ...base.recovery, state: 'Provisional', targetFixedAt: null } }) }); renderPage()
    const provisional = await screen.findByRole('article', { name: 'Meta de recuperação' })
    expect(text(provisional)).toContain('Meta provisória calculada'); expect(text(provisional)).not.toContain('Meta fixada'); expect(text(provisional)).not.toContain('Não é recalculada')
    cleanup()
    mockApi({ overview: withDecision({ recovery: { ...base.recovery, state: 'NotRegistered', target: null, targetFixedAt: null, gapToTarget: null, projectedRecovery: null } }) }); renderPage()
    const none = await screen.findByRole('article', { name: 'Meta de recuperação' })
    expect(text(none)).toContain('Ausência de meta'); expect(text(none)).toContain('Meta não configurada'); expect(text(none)).not.toMatch(/R\$ 0,00/)
    expect(text(none)).toContain('Ainda não é possível calcular')
  })

  it('o resumo não contradiz o detalhe: a recuperação detalhada só carrega ao abrir e fala de meta "não fixada"', async () => {
    mockApi(); renderPage()
    const summary = await screen.findByRole('article', { name: 'Meta de recuperação' })
    expect(calls.some(url => url.includes('overdraft-recovery') || url.includes('/projection'))).toBe(false)
    fireEvent.click(within(summary).getByRole('button', { name: 'Registrar meta e ver histórico' }))
    expect(await screen.findByRole('region', { name: 'Recuperação do limite projetada' })).toBeTruthy()
    await waitFor(() => expect(calls.some(url => url.includes('/monthly-plan/projection'))).toBe(true))
  })

  it('conta que não abriu negativa: explica por que não há meta, sem valores', async () => {
    const base = decision()
    mockApi({ overview: withDecision({ recovery: { ...base.recovery, state: 'NotApplicable', reason: 'AccountDidNotOpenNegative', openingUsed: null, target: null, gapToTarget: null } }) }); renderPage()
    const recovery = await screen.findByRole('article', { name: 'Meta de recuperação' })
    expect(text(recovery)).toContain('A conta não abriu a competência usando o cheque especial')
    expect(within(recovery).queryByText('Registrar meta e ver histórico')).toBeNull()
  })
})

describe('Visão Geral — onde agir', () => {
  it('mostra até três itens por prioridade e "Ver todas as recomendações" revela os demais', async () => {
    mockApi(); renderPage()
    const actions = await screen.findByRole('region', { name: 'Onde preciso agir' })
    const items = () => within(actions).getAllByRole('listitem').filter(item => item.classList.contains('ov-action'))
    expect(items()).toHaveLength(3)
    expect(text(items()[0])).toContain('Risco de caixa ou da meta'); expect(text(items()[0])).toContain('Há compromissos vencidos e não pagos.'); expect(text(items()[0])).toContain('R$ 100,00 em aberto.')
    expect(text(items()[1])).toContain('A recuperação projetada está abaixo da meta.')
    fireEvent.click(within(actions).getByRole('button', { name: 'Ver todas as recomendações (6)' }))
    expect(items()).toHaveLength(6)
    fireEvent.click(within(actions).getByRole('button', { name: 'Mostrar menos' }))
    expect(items()).toHaveLength(3)
  })

  it('alertas críticos (risco de caixa ou da meta) ficam sempre visíveis, mesmo acima de três', async () => {
    const base = decision()
    mockApi({ overview: withDecision({
      coverage: { ...base.coverage, state: 'Insufficient', firstBeyondLimitDate: '2026-10-22', deficit: '500.00' },
      recovery: { ...base.recovery, state: 'Provisional', gapToTarget: '-200.00' },
    }) }); renderPage()
    const actions = await screen.findByRole('region', { name: 'Onde preciso agir' })
    const critical = within(actions).getAllByRole('listitem').filter(item => item.classList.contains('ov-action--1'))
    expect(critical).toHaveLength(3)
    expect(within(actions).getByText('Faltam R$ 200,00 para a meta provisória.')).toBeTruthy()
  })

  it('agrupa avisos técnicos por consequência, com as causas em expansão e a ação de cada uma', async () => {
    mockApi({ plan: plan({
      sync: { autoEnabled: false, autoIntervalHours: 24, oldestSuccessAt: '2026-10-10T12:00:00Z', latestSuccessAt: '2026-10-10T12:00:00Z', sameMoment: true, stale: true, state: 'Failed', nextAttemptAt: null, connections: [{ connectionId: 'c1', label: 'Banco', status: 'Failed', lastSuccessAt: '2026-10-10T12:00:00Z', lastAttemptAt: null, nextAttemptAt: null, consentExpiringSoon: false }] },
      coverage: { ...planActual.coverage, complete: false, reviewCount: 2, reviewAffectsConfidence: true, reasons: ['MovementsInReview'] },
    }) }); renderPage()
    const actions = await screen.findByRole('region', { name: 'Onde preciso agir' })
    fireEvent.click(await within(actions).findByRole('button', { name: /Ver todas as recomendações/ }))
    const group = within(actions).getByRole('listitem', { name: 'Alguns valores podem estar desatualizados ou incompletos.' })
    expect(text(group)).toContain('Confiabilidade dos dados')
    for (const cause of [/A última sincronização com o banco falhou/, /2 movimentação\(ões\) do banco aguardam sua decisão/, /Projeção incompleta/, /movimentações do banco em revisão/]) expect(within(group).getByText(cause)).toBeTruthy()
    fireEvent.click(within(group.querySelector('.ov-action__causes') as HTMLElement).getByRole('button', { name: 'Ver conexões' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'automation', context: { automationTab: 'connections' } })
    expect(screen.getByRole('button', { name: 'Sincronizar agora' })).toBeTruthy()
  })

  it('a fila de classificação é uma pendência de orçamento e nunca falha de sincronização', async () => {
    mockApi(); renderPage()
    const actions = await screen.findByRole('region', { name: 'Onde preciso agir' })
    fireEvent.click(await within(actions).findByRole('button', { name: /Ver todas as recomendações/ }))
    const group = within(actions).getByRole('listitem', { name: 'O orçamento tem pendências de classificação ou de metas.' })
    expect(within(group).getByText(/3 lançamento\(s\) pendentes de classificação/)).toBeTruthy()
    expect(within(actions).queryByText(/sincroniza/i)).toBeNull()
    fireEvent.click(within(group.querySelector('.ov-action__causes') as HTMLElement).getByRole('button', { name: 'Revisar classificação' }))
    expect(navigations.at(-1)).toMatchObject({ section: 'automation', context: { review: { kind: 'category' } } })
  })

  it('sem conta principal avisa em Onde preciso agir e leva para Contas', async () => {
    mockApi({ plan: plan({ scope: { ...planActual.scope, kind: 'all', primaryAccountId: null, primaryAccountName: null, primaryMissing: true } }) }); renderPage()
    const actions = await screen.findByRole('region', { name: 'Onde preciso agir' })
    fireEvent.click(await within(actions).findByRole('button', { name: /Ver todas as recomendações/ }))
    fireEvent.click(await within(actions).findByRole('button', { name: 'Definir em Contas' }))
    expect(navigations.at(-1)?.section).toBe('accounts')
  })

  it('sem nada a fazer diz isso, e enquanto carrega diz que está verificando', async () => {
    const base = decision()
    mockApi({ overview: withSummary({ classification: { state: 'Complete', needsReview: 0, notEvaluated: 0 }, decision: { ...base, commitments: { ...base.commitments, overdue: '0.00' }, recovery: { ...base.recovery, gapToTarget: '100.00' } }, budgetControl: budgetControl({ pressures: [], recommendations: [] }) }) }); renderPage()
    expect(screen.getByText('Verificando alertas e recomendações…')).toBeTruthy()
    expect(await screen.findByText('Nenhuma ação necessária com os dados disponíveis.')).toBeTruthy()
  })
})

describe('Visão Geral — filtros', () => {
  it('trocar a competência e a conta recarrega o resumo com o mesmo recorte, sem tocar nos detalhes fechados', async () => {
    mockApi(); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2026-09' } })
    await waitFor(() => expect(calls.filter(url => url.includes('competence=2026-09'))).toHaveLength(2)) // plan-actual e overview
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'all' } })
    await waitFor(() => expect(calls.filter(url => url.includes('scope=all')).length).toBe(2))
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'account:pj' } })
    await waitFor(() => expect(calls.filter(url => url.includes('scope=account') && url.includes('accountId=pj')).length).toBe(2))
    expect(calls.some(url => url.includes('six-months'))).toBe(false)
  })

  it('lista a conta principal, o consolidado e cada conta no seletor', async () => {
    mockApi(); renderPage()
    const select = await screen.findByLabelText('Conta ou consolidado')
    await waitFor(() => expect(within(select).getAllByRole('option').length).toBeGreaterThanOrEqual(4))
    const options = within(select).getAllByRole('option').map(option => option.textContent)
    expect(options[0]).toBe('Conta principal · Conta A'); expect(options[1]).toBe('Todas as contas (consolidado)'); expect(options).toContain('Conta B · PJ')
  })

  it('ação principal: novo lançamento reutiliza o fluxo de Lançamentos', async () => {
    mockApi(); renderPage()
    fireEvent.click(await screen.findByRole('button', { name: 'Novo lançamento' }))
    expect(navigations.at(-1)).toEqual({ section: 'transactions', context: { newTransaction: true } })
  })
})

describe('Visão Geral — estados e falhas', () => {
  it('mostra carregamento por bloco e preenche cada um quando chega', async () => {
    mockApi(); renderPage()
    expect(screen.getByText('Carregando a situação financeira…')).toBeTruthy()
    expect(await screen.findByRole('article', { name: 'Saldo bancário observado' })).toBeTruthy()
    expect(await screen.findByRole('article', { name: 'Orçamento mensal' })).toBeTruthy()
  })

  it('falha de plan-actual: só o fechamento fica indisponível, o resto segue válido e a recarga é só dela', async () => {
    mockApi({ plan: new Response('{}', { status: 500 }) }); renderPage()
    const closing = await screen.findByRole('article', { name: 'Fechamento projetado' })
    expect(text(closing)).toContain('Ainda não é possível calcular')
    expect(text(screen.getByRole('article', { name: 'Saldo bancário observado' }))).toContain('-R$ 1.000,00')
    const before = calls.length
    fireEvent.click(await screen.findByRole('button', { name: 'Recarregar indicadores' }))
    await waitFor(() => expect(calls.length).toBe(before + 1))
    expect(calls.at(-1)).toContain('/financial-dashboard/plan-actual')
  })

  it('falha do overview: a situação cai para o saldo calculado, diz que a análise não está disponível e a recarga é só dela', async () => {
    mockApi({ overview: new Response('{}', { status: 500 }) }); renderPage()
    const alert = await screen.findByRole('button', { name: 'Recarregar a Visão Geral' })
    expect(text(await screen.findByRole('article', { name: 'Saldo calculado' }))).toContain('não é o saldo informado pelo banco')
    expect(text(screen.getByRole('article', { name: 'Análise de cobertura indisponível' }))).toContain('Ainda não é possível calcular')
    expect(screen.queryByRole('region', { name: 'Meu orçamento' })).toBeNull()
    const before = calls.length
    fireEvent.click(alert)
    await waitFor(() => expect(calls.length).toBe(before + 1))
    expect(calls.at(-1)).toContain('/financial-dashboard/overview')
  })

  it('um bloco indisponível dentro da resposta é dito como indisponível, nunca como vazio ou zero', async () => {
    mockApi({ overview: withSummary({ goals: { ...overview.goals, state: 'Unavailable', items: [], activeCount: 0 },
      commitments: { ...overview.commitments, state: 'Unavailable' }, recent: { ...overview.recent, state: 'Unavailable', items: [], totalCount: 0 }, budget: { ...overview.budget, state: 'Unavailable', hasBudget: false, fixed: null, cards: null, categories: { count: 0, planned: null, consumed: null, exceededCount: 0, approachingCount: 0, targets: [] } } }) })
    renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    for (const title of ['Metas e objetivos', 'Próximos compromissos por vencimento', 'Últimos lançamentos', 'Metas por categoria e despesas fixas']) openSection(title)
    expect(screen.getByText('Não foi possível carregar as metas.')).toBeTruthy()
    expect(screen.getByText('Não foi possível carregar os compromissos da Agenda.')).toBeTruthy()
    expect(screen.getByText('Não foi possível carregar os lançamentos.')).toBeTruthy()
    expect(screen.getByText(/Não foi possível carregar o orçamento deste mês/)).toBeTruthy()
  })

  it('estados vazios orientam: metas e lançamentos', async () => {
    mockApi({ overview: withSummary({ goals: { ...overview.goals, items: [], activeCount: 0 }, recent: { ...overview.recent, items: [], totalCount: 0 } }) }); renderPage()
    await screen.findByRole('article', { name: 'Saldo bancário observado' })
    openSection('Metas e objetivos')
    fireEvent.click(screen.getByRole('button', { name: 'Criar meta' }))
    expect(navigations.at(-1)?.section).toBe('goals')
  })

  it('não apresenta dados fictícios: sem resposta da API, nenhum valor financeiro aparece', async () => {
    mockApi({ plan: new Response('{}', { status: 500 }), overview: new Response('{}', { status: 500 }) })
    renderPage()
    await waitFor(() => expect(screen.getAllByRole('alert').length).toBeGreaterThanOrEqual(2))
    expect(screen.queryByText(/R\$/)).toBeNull()
  })
})
