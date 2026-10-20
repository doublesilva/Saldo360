import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialCalendarMonthlyProjectionResponse } from '../api/financialCalendar'
import { FinancialCalendarMonthlyPlanning } from './FinancialCalendar'

function projection(): FinancialCalendarMonthlyProjectionResponse {
  return {
    from: '2026-12',
    months: [
      {
        month: '2026-12', realizedIncome: '9999999999999.99', realizedExpense: '200.05',
        pendingIncome: '500.00', pendingExpense: '800.00', overdueIncome: '10.00', overdueExpense: '20.00',
        openingBalance: '1000.00', closingBalance: '-0.01', minimumProjectedBalance: '-500.25',
        firstNegativeProjectedDate: '2026-12-24', unavailableReason: null,
      },
      {
        month: '2027-01', realizedIncome: '0.00', realizedExpense: '0.00',
        pendingIncome: '100.00', pendingExpense: '50.00', overdueIncome: '0.00', overdueExpense: '0.00',
        openingBalance: '-0.01', closingBalance: '49.99', minimumProjectedBalance: '-0.01',
        firstNegativeProjectedDate: '2027-01-01', unavailableReason: null,
      },
      {
        month: '2027-02', realizedIncome: '0.00', realizedExpense: '0.00',
        pendingIncome: '0.00', pendingExpense: '0.00', overdueIncome: '0.00', overdueExpense: '0.00',
        openingBalance: '49.99', closingBalance: '49.99', minimumProjectedBalance: '49.99',
        firstNegativeProjectedDate: null, unavailableReason: null,
      },
      {
        month: '2027-03', realizedIncome: '0.00', realizedExpense: '0.00',
        pendingIncome: '0.00', pendingExpense: '0.00', overdueIncome: '0.00', overdueExpense: '0.00',
        openingBalance: null, closingBalance: null, minimumProjectedBalance: null,
        firstNegativeProjectedDate: null, unavailableReason: 'OpeningBalanceNotConfigured',
      },
      {
        month: '2027-04', realizedIncome: '0.00', realizedExpense: '0.00',
        pendingIncome: '0.00', pendingExpense: '0.00', overdueIncome: '0.00', overdueExpense: '0.00',
        openingBalance: '49.99', closingBalance: '49.99', minimumProjectedBalance: '49.99',
        firstNegativeProjectedDate: null, unavailableReason: null,
      },
      {
        month: '2027-05', realizedIncome: '0.00', realizedExpense: '0.00',
        pendingIncome: '0.00', pendingExpense: '0.00', overdueIncome: '0.00', overdueExpense: '0.00',
        openingBalance: '49.99', closingBalance: '49.99', minimumProjectedBalance: '49.99',
        firstNegativeProjectedDate: null, unavailableReason: null,
      },
    ],
  }
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('FinancialCalendarMonthlyPlanning', () => {
  it('mostra resumo do backend, cartões como subconjunto e comparação com meta sem recalcular dinheiro', async () => {
    const data = projection()
    data.summary = { totalIncome: '123.45', totalExpense: '987.65', lowestClosingBalance: '-12.34', lowestBalanceMonth: '2027-01', monthsOverBudget: 1, monthsWithNegativeBalance: 2 }
    data.months[0] = { ...data.months[0], periodKind: 'Current', budgetStatus: 'Exceeded', budgetTarget: '100.00', budgetConsumption: '120.01', cardPayments: '30.01', creditCardOutstanding: '40.02', creditCardProjectionComplete: false }
    data.months[1] = { ...data.months[1], periodKind: 'Future', budgetStatus: 'LimitReached' }
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(data)))
    render(<FinancialCalendarMonthlyPlanning familyId="family-1" from="2026-12" accountId="" onSelectMonth={vi.fn()} />)
    const summary = await screen.findByRole('region', { name: 'Resumo dos seis meses' })
    expect(within(summary).getByText(/123,45/)).toBeTruthy()
    expect(within(summary).getByText(/987,65/)).toBeTruthy()
    expect(screen.getByText('Meta ultrapassada')).toBeTruthy()
    expect(screen.getByText('Meta atingida')).toBeTruthy()
    expect(screen.getByText(/Mês atual · realizado/)).toBeTruthy()
    expect(screen.getByText(/Mês futuro · compromissos/)).toBeTruthy()
    expect(screen.getByText(/Cobertura parcial/)).toBeTruthy()
    expect(screen.getByText(/40,02/)).toBeTruthy()
  })

  it('renderiza seis competências, valores exatos, vencidas, risco e indisponibilidade', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      calls.push(String(input))
      return Response.json(projection())
    }))

    render(<FinancialCalendarMonthlyPlanning familyId="family-1" from="2026-12" accountId="" onSelectMonth={() => {}} />)
    expect(screen.getByText('Calculando planejamento financeiro…')).toBeTruthy()
    expect(await screen.findAllByRole('article')).toHaveLength(6)
    const december = screen.getByRole('button', { name: /Dezembro de 2026/ }).closest('article')!
    expect(within(december).getByText(/R\$\s9\.999\.999\.999\.999,99/)).toBeTruthy()
    expect(within(december).getByText(/Risco: primeiro saldo negativo em 24\/12\/2026/)).toBeTruthy()
    expect(within(december).getByText(/Pendências vencidas: receitas R\$\s10,00 · despesas R\$\s20,00/)).toBeTruthy()
    expect(december.className).toContain('monthly-planning-card--risk')
    expect(screen.getByText(/Saldo indisponível: evolução indisponível/)).toBeTruthy()
    const query = new URL(calls[0], 'http://x').searchParams
    expect(Object.fromEntries(query)).toEqual({ from: '2026-12' })
  })

  it('seleciona a competência para abrir o calendário mensal', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(projection())))
    const select = vi.fn()
    render(<FinancialCalendarMonthlyPlanning familyId="family-1" from="2026-12" accountId="" onSelectMonth={select} />)
    fireEvent.click(await screen.findByRole('button', { name: /Janeiro de 2027/ }))
    expect(select).toHaveBeenCalledWith('2027-01')
  })

  it('atualiza pelo filtro de conta e descarta resposta concorrente antiga', async () => {
    let resolveOld!: (response: Response) => void
    const old = new Promise<Response>((resolve) => { resolveOld = resolve })
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input)
      return url.includes('accountId=acc-2')
        ? Promise.resolve(Response.json({ ...projection(), months: projection().months.slice(0, 1) }))
        : old
    })
    vi.stubGlobal('fetch', fetchMock)
    const view = render(
      <FinancialCalendarMonthlyPlanning familyId="family-1" from="2026-12" accountId="acc-1" onSelectMonth={() => {}} />,
    )
    view.rerender(
      <FinancialCalendarMonthlyPlanning familyId="family-1" from="2026-12" accountId="acc-2" onSelectMonth={() => {}} />,
    )
    expect(await screen.findAllByRole('article')).toHaveLength(1)
    resolveOld(Response.json(projection()))
    await Promise.resolve()
    expect(screen.getAllByRole('article')).toHaveLength(1)
    expect(String(fetchMock.mock.calls[1][0])).toContain('accountId=acc-2')
  })

  it('apresenta erro da API sem inventar valores', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 503 })))
    render(<FinancialCalendarMonthlyPlanning familyId="family-1" from="2026-12" accountId="" onSelectMonth={() => {}} />)
    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('HTTP 503')
    expect(screen.queryByRole('article')).toBeNull()
  })
})
