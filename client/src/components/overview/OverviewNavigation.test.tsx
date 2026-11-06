import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PersonalFinance } from '../PersonalFinance'
import { planActual } from '../planActualFixtures'
import { sixMonths } from '../monthlyPlanFixtures'
import { overview } from './overviewFixtures'
import { projection } from '../projectionFixtures'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

/** Abre uma seção das análises detalhadas pelo título (só ela é montada, e só então faz as suas consultas). */
const openSection = async (title: string) => fireEvent.click(await screen.findByText(title, { selector: '.ov-disclosure__title' }))

let calls: string[] = []
function mockApp() {
  calls = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input); calls.push(url)
    const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } })
    if (url === '/auth/csrf') return json({ token: 'csrf' })
    if (url === '/api/me/financial-workspace') return json({ id: 'w', ownerUserId: 'u', name: 'Meu orçamento', type: 'Personal', isActive: true })
    if (url.includes('/financial-dashboard/plan-actual')) return json(planActual)
    if (url.includes('/financial-dashboard/overview')) return json(overview)
    if (url.includes('/monthly-plan/six-months')) return json(sixMonths)
    if (url.includes('/monthly-plan/food-provision')) return json({ monthlyAmount: '2000.00', amountConfigured: false, categoryIds: [], categoriesInferred: false, monthAmounts: {} })
    if (url.includes('/monthly-plan/income-forecast')) return json({ exists: false, isActive: false, accountId: null, accountName: null, hourlyRate: '0.00', hoursPerDay: '8', paymentDay: 5, paymentOffsetMonths: 1, taxReservePercent: '0.00', operationalReserve: '0.00', months: [], accounts: [] })
    if (url.includes('/monthly-plan/projection')) { const query = new URL(url, 'http://x').searchParams; return json(projection(query.get('from')!, Number(query.get('months')))) }
    if (url.includes('/transactions?')) return json({ items: [], page: 1, pageSize: 20, totalCount: 0 })
    if (url.includes('/accounts') || url.includes('/categories')) return json([])
    if (url.includes('/financial-calendar/balances')) return json({ from: '2026-11-01', through: '2026-11-30', accountId: null, openingBalance: null, unavailableReason: null, days: [], risk: { minimumProjectedBalance: null, firstNegativeProjectedDate: null, endingProjectedBalance: null } })
    if (url.includes('/financial-calendar/monthly-projection')) return json({ from: '2026-11', months: [] })
    if (url.includes('/financial-calendar')) return json({ from: '2026-11-01', through: '2026-11-30', items: [], summary: { realizedIncome: '0.00', realizedExpense: '0.00', pendingIncome: '0.00', pendingExpense: '0.00', overdueIncome: '0.00', overdueExpense: '0.00' } })
    return json([])
  }))
}

describe('Visão Geral → detalhe (R01)', () => {
  it('Novo lançamento abre a página de Lançamentos já com o formulário', async () => {
    mockApp(); render(<PersonalFinance />)
    fireEvent.click(await screen.findByRole('button', { name: 'Novo lançamento' }))
    expect(await screen.findByRole('heading', { level: 3, name: 'Novo lançamento' })).toBeTruthy()
    expect(screen.getByRole('heading', { level: 1, name: 'Lançamentos' })).toBeTruthy()
  })

  it('o card de recuperação do limite abre o Planejamento na Projeção financeira', async () => {
    mockApp(); render(<PersonalFinance />)
    await openSection('Recuperação detalhada')
    const card = await screen.findByRole('region', { name: 'Recuperação do limite projetada' })
    fireEvent.click(within(card).getByRole('button', { name: 'Abrir a Projeção financeira' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Planejamento' })).toBeTruthy()
    expect(screen.getByRole('tab', { name: 'Projeção financeira' }).getAttribute('aria-selected')).toBe('true')
    expect(await screen.findByLabelText('Indicadores do cenário Atual')).toBeTruthy()
    await waitFor(() => expect(calls.some(url => url.includes('/monthly-plan/projection?') && url.includes('months=6'))).toBe(true))
  })

  it('o compromisso abre a Agenda no mês do vencimento', async () => {
    mockApp(); render(<PersonalFinance />)
    await openSection('Próximos compromissos por vencimento')
    const block = await screen.findByRole('region', { name: 'Próximos compromissos' })
    fireEvent.click(within(block).getByRole('button', { name: /Financiamento - parcela 4/ }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Agenda' })).toBeTruthy()
    await waitFor(() => expect(calls.some(url => url.includes('/financial-calendar?') && url.includes('from=2026-11-01'))).toBe(true))
  })

  it('Ver todos leva os lançamentos do período e da conta da Visão Geral', async () => {
    mockApp(); render(<PersonalFinance />)
    await openSection('Últimos lançamentos')
    const recent = await screen.findByRole('region', { name: 'Últimos lançamentos' })
    fireEvent.click(within(recent).getByRole('button', { name: 'Ver todos' }))
    await screen.findByRole('heading', { level: 1, name: 'Lançamentos' })
    await waitFor(() => expect(calls.some(url => url.includes('/transactions?') && url.includes('from=2026-10-01') && url.includes('to=2026-10-16') && url.includes('accountId=pf'))).toBe(true))
  })

  it('o orçamento abre na competência selecionada e o menu não oferece destinos retirados', async () => {
    mockApp(); render(<PersonalFinance />)
    await screen.findByRole('heading', { level: 1, name: 'Visão Geral' })
    fireEvent.change(await screen.findByLabelText('Competência'), { target: { value: '2026-09' } })
    await openSection('Metas por categoria e despesas fixas')
    const budget = await screen.findByRole('region', { name: /Orçamento de setembro/i })
    fireEvent.click(within(budget).getByRole('button', { name: 'Abrir orçamento' }))
    expect(await screen.findByRole('heading', { level: 1, name: 'Orçamento' })).toBeTruthy()
    await waitFor(() => expect(calls.some(url => url.includes('/budgets') && url.includes('competence=2026-09'))).toBe(true))
    const nav = screen.getByRole('navigation', { name: 'Navegação do orçamento pessoal' })
    for (const retired of [/Documentos/, /Inbox/, /Importa/, /Concilia/, /Confer/, /E-?mail/i]) expect(within(nav).queryByRole('button', { name: retired })).toBeNull()
  })
})
