import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { DashboardCardDetails, DashboardCommitments, DashboardDailyEvolution } from './DashboardDataPanels'
import { dashboard } from './dashboardFixtures'
import type { FinancialCalendarBalanceResponse } from '../api/financialCalendar'

vi.mock('../dates', async importOriginal => ({ ...await importOriginal<typeof import('../dates')>(), todayInSaoPaulo: () => '2026-10-22' }))
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const item = (id: string, date: string, status = 'Pending', type = 'Expense') => ({ id, source: 'PlannedTransaction', type, date, status, amount: '123.45', description: id, currency: 'BRL', account: null, category: null, plannedTransactionId: id })
const balance: FinancialCalendarBalanceResponse = {
  from: '2026-10-01', through: '2026-12-31', accountId: null, openingBalance: '100.00', unavailableReason: null,
  risk: { minimumProjectedBalance: '-50.00', firstNegativeProjectedDate: '2026-10-23', endingProjectedBalance: '-50.00' },
  days: [
    { date: '2026-10-20', kind: 'Realized', income: '0.00', expense: '0.00', transferIn: '0.00', transferOut: '0.00', closingBalance: '90071992547409.91', hasOverduePending: false, isNegative: false, chartPosition: 0, unavailableReason: null },
    { date: '2026-10-21', kind: 'Realized', income: '0.00', expense: '0.00', transferIn: '0.00', transferOut: '0.00', closingBalance: '100.00', hasOverduePending: false, isNegative: false, chartPosition: 500, unavailableReason: null },
    { date: '2026-10-23', kind: 'Projected', income: '0.00', expense: '0.00', transferIn: '0.00', transferOut: '0.00', closingBalance: '-50.00', hasOverduePending: true, isNegative: true, chartPosition: 1000, unavailableReason: null },
  ],
}

it('orders pending expenses by date, shows overdue/today/future, and omits realized entries and income', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({ items: [item('Futuro item', '2026-10-25'), item('Realizado', '2026-10-01', 'Realized'), item('Hoje item', '2026-10-22'), item('Atrasado item', '2026-10-10', 'Overdue'), item('Receita', '2026-10-02', 'Pending', 'Income')] })))
  const navigate = vi.fn()
  render(<DashboardCommitments workspaceId="a" competence="2026-10" months={3} data={dashboard} onAgenda={navigate} />)
  expect(await screen.findByText('Vence hoje')).toBeTruthy()
  expect(screen.getAllByRole('listitem').map(li => li.querySelector('strong')?.textContent)).toEqual(['Atrasado item', 'Hoje item', 'Futuro item'])
  expect(screen.queryByText('Receita')).toBeNull(); expect(screen.queryByText('Realizado')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Abrir Agenda' })); expect(navigate).toHaveBeenCalledOnce()
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('through=2026-12-31&type=Expense'), expect.anything())
})

it('does not turn a failed agenda into an empty list and supports read-only retry', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response('{}', { status: 503 })).mockResolvedValueOnce(Response.json({ items: [] })))
  render(<DashboardCommitments workspaceId="a" competence="2026-10" months={3} data={dashboard} />)
  await screen.findByRole('alert')
  expect(screen.queryByText(/Nenhuma despesa pendente/)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Recarregar compromissos' }))
  expect(await screen.findByText(/Nenhuma despesa pendente/)).toBeTruthy()
})

it('discards a late agenda response after changing workspace', async () => {
  let old!: (value: Response) => void
  vi.stubGlobal('fetch', vi.fn(async input => String(input).includes('/a/') ? new Promise<Response>(resolve => { old = resolve }) : Response.json({ items: [item('Novo contexto', '2026-10-25')] })))
  const view = render(<DashboardCommitments workspaceId="a" competence="2026-10" months={3} data={dashboard} />)
  await waitFor(() => expect(old).toBeDefined())
  view.rerender(<DashboardCommitments workspaceId="b" competence="2026-11" months={6} data={dashboard} />)
  await screen.findByText('Novo contexto')
  old(Response.json({ items: [item('Antigo contexto', '2026-10-01')] }))
  await Promise.resolve()
  expect(screen.queryByText('Antigo contexto')).toBeNull()
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('from=2026-11-01&through=2027-04-30'), expect.anything())
})

it('reads per-card limits and invoices for the selected competence without adding credit to bank balance', async () => {
  vi.stubGlobal('fetch', vi.fn(async input => {
    const url = String(input)
    if (url.includes('/limit')) return Response.json({ creditLimit: '10000.00', usedLimit: '2000.00', availableLimit: '8000.00', overLimitAmount: '0.00' })
    if (url.includes('/invoices?')) return Response.json([{ id: 'invoice', competence: '2026-10', dueDate: '2026-10-10', totalAmount: '300.00', remainingAmount: '100.00', paymentStatus: 'PartiallyPaid' }])
    return Response.json([{ id: 'card', name: 'Cartão sintético' }])
  }))
  render(<DashboardCardDetails workspaceId="a" competence="2026-10" />)
  expect(await screen.findByText(/Parcialmente paga/)).toBeTruthy()
  expect(screen.getByText(/Limite utilizado/).textContent).toContain('8.000,00')
  expect(screen.getByText(/Crédito não é saldo/)).toBeTruthy()
  expect(fetch).toHaveBeenCalledWith(expect.stringContaining('invoices?from=2026-10&through=2026-10'), expect.anything())
})

it('keeps unavailable invoice/limit distinct from no invoice or zero and can reload the details', async () => {
  vi.stubGlobal('fetch', vi.fn(async input => String(input).includes('?includeArchived=') ? Response.json([{ id: 'card', name: 'Cartão sintético' }]) : new Response('{}', { status: 503 })))
  render(<DashboardCardDetails workspaceId="a" competence="2026-10" />)
  expect(await screen.findByText('Utilização do limite indisponível.')).toBeTruthy()
  expect(screen.getByText(/Faturas indisponíveis/)).toBeTruthy()
  expect(screen.queryByText(/Nenhuma fatura emitida/)).toBeNull()
  expect(screen.queryByText(/R\$/)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Recarregar detalhes dos cartões' }))
  await waitFor(() => expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('?includeArchived='))).toHaveLength(2))
})

it('uses backend chart coordinates and original decimal strings, differentiating realized and projected values', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json(balance)))
  const { container } = render(<DashboardDailyEvolution workspaceId="a" competence="2026-10" months={3} />)
  await screen.findByRole('img', { name: /Evolução diária/ })
  expect(container.querySelectorAll('circle')[1].getAttribute('cy')).toBe('120')
  expect(container.querySelectorAll('circle')[2].getAttribute('class')).toContain('projected')
  expect(container.textContent).toContain('90.071.992.547.409,91')
  expect(container.textContent).toContain('Realizado')
  expect(container.textContent).toContain('Projetado')
})

it('does not bridge missing daily balances and keeps incomplete coverage explicit', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({ ...balance, creditCardProjectionComplete: false, days: [balance.days[0], { ...balance.days[1], chartPosition: null, closingBalance: null }, { ...balance.days[2], kind: 'Realized' }] })))
  const { container } = render(<DashboardDailyEvolution workspaceId="a" competence="2026-10" months={3} />)
  await screen.findByRole('img', { name: /Evolução diária/ })
  expect(container.querySelectorAll('polyline')).toHaveLength(0)
  expect(screen.getByText(/Projeção diária parcial/)).toBeTruthy()
  expect(container.textContent).toContain('Indisponível')
})

it('does not fabricate a daily zero when opening balance is missing', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => Response.json({ ...balance, unavailableReason: 'OpeningBalanceNotConfigured' })))
  render(<DashboardDailyEvolution workspaceId="a" competence="2026-10" months={3} />)
  expect(await screen.findByText(/Evolução indisponível/)).toBeTruthy()
  expect(screen.queryByRole('img')).toBeNull()
})

it('distinguishes a failed daily projection from an empty one and retries only its read', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response('{}', { status: 503 })).mockResolvedValueOnce(Response.json({ ...balance, days: [] })))
  render(<DashboardDailyEvolution workspaceId="a" competence="2026-10" months={3} />)
  await screen.findByRole('alert')
  expect(screen.queryByText(/Nenhum saldo diário/)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Recarregar evolução' }))
  expect(await screen.findByText(/Nenhum saldo diário/)).toBeTruthy()
  expect(fetch).toHaveBeenCalledTimes(2)
})
