import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { FinancialCategory } from '../api/categories'
import type { FinancialCalendarBalanceResponse, FinancialCalendarItem, FinancialCalendarResponse } from '../api/financialCalendar'
import type { PlannedTransaction } from '../api/planned'
import type { FinancialTransaction } from '../api/transactions'
import { FinancialCalendarPage } from './FinancialCalendar'

type Call = { url: string; init?: RequestInit }

const account = (id: string, name: string, isActive = true): FinancialAccount => ({
  id,
  name,
  institution: null,
  type: 'Checking',
  financialScope: 'PF',
  responsible: null,
  openingBalance: '1000.00',
  openingBalanceDate: '2026-01-01',
  isActive,
  currency: 'BRL',
  createdAt: '2026-01-01T00:00:00Z',
})

const itau = account('acc-itau', 'Itaú PF')
const inactiveAccount = account('acc-old', 'Conta histórica', false)
const incomeCategory: FinancialCategory = { id: 'cat-income', name: 'Salário', type: 'Income', isActive: true, createdAt: '2026-01-01T00:00:00Z' }
const expenseCategory: FinancialCategory = { id: 'cat-expense', name: 'Moradia', type: 'Expense', isActive: true, createdAt: '2026-01-01T00:00:00Z' }
const inactiveCategory: FinancialCategory = { id: 'cat-old', name: 'Categoria antiga', type: 'Expense', isActive: false, createdAt: '2026-01-01T00:00:00Z' }

const standalone: FinancialTransaction = {
  id: 'tx-standalone',
  type: 'Income',
  amount: '5000.00',
  transactionDate: '2026-12-15',
  description: 'Salário recebido',
  account: itau,
  category: incomeCategory,
  isCancelled: false,
  currency: 'BRL',
  createdAt: '2026-12-15T12:00:00Z',
  updatedAt: null,
  cancelledAt: null,
  plannedTransactionId: null,
}

const linked: FinancialTransaction = {
  ...standalone,
  id: 'tx-linked',
  type: 'Expense',
  amount: '800.00',
  description: 'Aluguel realizado',
  category: expenseCategory,
  plannedTransactionId: 'planned-linked',
}

const overdue: PlannedTransaction = {
  id: 'planned-overdue',
  type: 'Expense',
  amount: '10.05',
  dueDate: '2026-12-10',
  description: 'Conta vencida',
  account: itau,
  category: expenseCategory,
  status: 'Pending',
  realization: null,
  isCancelled: false,
  currency: 'BRL',
  createdAt: '2026-11-01T00:00:00Z',
  updatedAt: null,
  cancelledAt: null,
}

const pending: PlannedTransaction = {
  ...overdue,
  id: 'planned-pending',
  amount: '1234.56',
  dueDate: '2026-12-20',
  description: 'Aluguel previsto',
  account: inactiveAccount,
  category: inactiveCategory,
}

function calendarItem(value: FinancialTransaction | PlannedTransaction, status?: FinancialCalendarItem['status']): FinancialCalendarItem {
  const planned = 'dueDate' in value
  return {
    id: value.id,
    source: planned ? 'PlannedTransaction' : 'Transaction',
    status: status ?? (planned ? 'Pending' : 'Realized'),
    type: value.type,
    amount: value.amount,
    date: planned ? value.dueDate : value.transactionDate,
    description: value.description,
    account: value.account,
    category: value.category,
    currency: value.currency,
    plannedTransactionId: planned ? null : value.plannedTransactionId,
  }
}

function balance(openingBalance = '1000.00'): FinancialCalendarBalanceResponse {
  return {
    from: '2026-12-01',
    through: '2026-12-31',
    accountId: null,
    openingBalance,
    unavailableReason: null,
    days: [{
      date: '2026-12-15',
      kind: 'Projected',
      income: '5000.00',
      expense: '800.00',
      transferIn: '0.00',
      transferOut: '0.00',
      closingBalance: '5200.00',
      hasOverduePending: true,
      isNegative: false,
      chartPosition: 500,
      unavailableReason: null,
    }],
    risk: { minimumProjectedBalance: '5200.00', firstNegativeProjectedDate: null, endingProjectedBalance: '5200.00' },
  }
}

function calendar(items: FinancialCalendarItem[], changed: boolean): FinancialCalendarResponse {
  return {
    from: '2026-12-01',
    through: '2026-12-31',
    items,
    summary: {
      realizedIncome: '5000.00',
      realizedExpense: changed ? '999.99' : '800.00',
      pendingIncome: '0.00',
      pendingExpense: '1244.61',
      overdueIncome: '0.00',
      overdueExpense: '10.05',
    },
  }
}

function mockApi() {
  const calls: Call[] = []
  const state = { changed: false, linkedCancelled: false, failNextAction: false }
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const method = init?.method ?? 'GET'
    calls.push({ url, init })

    if (url === '/auth/csrf') return Response.json({ token: 'csrf-token' })
    if (/\/accounts(?:\?|$)/.test(url)) return Response.json([itau, inactiveAccount])
    if (/\/categories(?:\?|$)/.test(url)) return Response.json([incomeCategory, expenseCategory, inactiveCategory])

    if (url.includes('/financial-calendar/balances?')) return Response.json(balance(state.changed ? '777.77' : '1000.00'))
    if (url.includes('/financial-calendar?')) {
      const currentItems = state.linkedCancelled
        ? [calendarItem(standalone), calendarItem({ ...overdue, id: 'planned-linked', dueDate: '2026-12-15', description: 'Aluguel reaberto', amount: '800.00' }), calendarItem(overdue, 'Overdue'), calendarItem(pending)]
        : [calendarItem(standalone), calendarItem(linked), calendarItem(overdue, 'Overdue'), calendarItem(pending)]
      return Response.json(calendar(currentItems, state.changed))
    }

    if (method === 'GET' && url.endsWith('/transactions/tx-standalone')) return Response.json(standalone)
    if (method === 'GET' && url.endsWith('/transactions/tx-linked')) return Response.json(linked)
    if (method === 'GET' && url.endsWith('/planned-transactions/planned-overdue')) return Response.json(overdue)
    if (method === 'GET' && url.endsWith('/planned-transactions/planned-pending')) return Response.json(pending)

    if (method !== 'GET' && state.failNextAction) {
      state.failNextAction = false
      return new Response(null, { status: 503 })
    }
    if (method === 'POST' && url.endsWith('/planned-transactions/planned-pending/realize')) {
      const body = JSON.parse(String(init?.body)) as { amount: string; transactionDate: string }
      state.changed = true
      return Response.json({ ...linked, id: 'tx-realized', amount: body.amount, transactionDate: body.transactionDate, plannedTransactionId: pending.id }, { status: 201 })
    }
    if (method === 'PUT' && url.endsWith('/planned-transactions/planned-pending')) {
      const body = JSON.parse(String(init?.body)) as { description: string }
      state.changed = true
      return Response.json({ ...pending, description: body.description })
    }
    if (method === 'POST' && url.endsWith('/planned-transactions/planned-overdue/cancel')) {
      state.changed = true
      return Response.json({ ...overdue, status: 'Cancelled', isCancelled: true })
    }
    if (method === 'POST' && url.endsWith('/transactions/tx-linked/cancel')) {
      state.changed = true
      state.linkedCancelled = true
      return Response.json({ ...linked, isCancelled: true })
    }
    if (method === 'POST' && url.endsWith('/transactions/tx-standalone/cancel')) {
      state.changed = true
      return Response.json({ ...standalone, isCancelled: true })
    }
    throw new Error(`URL inesperada: ${method} ${url}`)
  }))
  return { calls, state }
}

function callsFor(calls: Call[], fragment: string): Call[] {
  return calls.filter((call) => call.url.includes(fragment))
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  vi.setSystemTime(new Date('2026-12-15T15:00:00Z'))
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('FinancialCalendarDetails', () => {
  it('abre, exibe as propriedades disponíveis, gerencia foco e fecha por teclado', async () => {
    mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')
    fireEvent.click(screen.getByRole('gridcell', { name: /20\/12\/2026/ }))
    const trigger = screen.getByRole('button', { name: 'Ver detalhes de Aluguel previsto' })

    fireEvent.click(trigger)

    const dialog = await screen.findByRole('dialog', { name: 'Detalhes do compromisso' })
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Fechar detalhes' }))
    expect(within(dialog).getByText(/1\.234,56/)).toBeTruthy()
    expect(within(dialog).getByText('Despesa')).toBeTruthy()
    expect(within(dialog).getByText('20/12/2026')).toBeTruthy()
    expect(within(dialog).getByText('Conta histórica (inativa)')).toBeTruthy()
    expect(within(dialog).getByText('Categoria antiga (inativa)')).toBeTruthy()
    expect(within(dialog).getAllByText('Previsão')).toHaveLength(2)
    expect(within(dialog).getByText('Pendente')).toBeTruthy()
    await within(dialog).findByRole('button', { name: 'Cancelar previsão' })

    fireEvent.keyDown(dialog, { key: 'Tab', shiftKey: true })
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Cancelar previsão' }))
    fireEvent.keyDown(dialog, { key: 'Escape' })

    expect(screen.queryByRole('dialog')).toBeNull()
    expect(document.activeElement).toBe(trigger)
  })

  it('oferece ações conforme origem e impede editar uma realização vinculada', async () => {
    mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')

    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Salário recebido' }))
    let dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByRole('button', { name: 'Editar' })).toBeTruthy()
    expect(within(dialog).getByRole('button', { name: 'Cancelar lançamento' })).toBeTruthy()
    expect(within(dialog).queryByRole('button', { name: 'Realizar' })).toBeNull()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Fechar detalhes' }))

    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Aluguel realizado' }))
    dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText(/Cancelá-lo fará a previsão voltar a ficar pendente/)).toBeTruthy()
    expect(within(dialog).queryByRole('button', { name: 'Editar' })).toBeNull()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Fechar detalhes' }))

    fireEvent.click(screen.getByRole('gridcell', { name: /10\/12\/2026/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Conta vencida' }))
    dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByRole('button', { name: 'Realizar' })).toBeTruthy()
    expect(within(dialog).getByRole('button', { name: 'Editar' })).toBeTruthy()
    expect(within(dialog).getByRole('button', { name: 'Cancelar previsão' })).toBeTruthy()
  })

  it('realiza com valor efetivo diferente, recarrega agenda/resumo/evolução e preserva o contexto', async () => {
    const { calls } = mockApi()
    const changed = vi.fn()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<FinancialCalendarPage familyId="family-1" onChanged={changed} />)
    await screen.findByText('Salário recebido')
    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: inactiveAccount.id } })
    fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Expense' } })
    await waitFor(() => expect(callsFor(calls, '/financial-calendar?').length).toBeGreaterThanOrEqual(3))
    fireEvent.click(screen.getByRole('gridcell', { name: /20\/12\/2026/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Aluguel previsto' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Realizar' }))
    fireEvent.change(within(dialog).getByLabelText('Valor efetivo (R$)'), { target: { value: '999,99' } })
    const calendarBefore = callsFor(calls, '/financial-calendar?').length
    const balancesBefore = callsFor(calls, '/financial-calendar/balances?').length

    fireEvent.click(within(dialog).getByRole('button', { name: 'Realizar' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    const realization = calls.find((call) => call.url.endsWith('/planned-transactions/planned-pending/realize'))!
    expect(realization.init?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'csrf-token' })
    expect(JSON.parse(String(realization.init?.body))).toEqual({ amount: '999.99', transactionDate: '2026-12-15' })
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('R$ 999,99'))
    await waitFor(() => expect(callsFor(calls, '/financial-calendar?').length).toBeGreaterThan(calendarBefore))
    await waitFor(() => expect(callsFor(calls, '/financial-calendar/balances?').length).toBeGreaterThan(balancesBefore))
    expect(changed).toHaveBeenCalledTimes(1)
    expect((screen.getByLabelText('Conta') as HTMLSelectElement).value).toBe(inactiveAccount.id)
    expect((screen.getByLabelText('Tipo') as HTMLSelectElement).value).toBe('Expense')
    expect(screen.getByRole('gridcell', { name: /20\/12\/2026/ }).getAttribute('aria-pressed')).toBe('true')
    expect(await within(screen.getByText('Despesas realizadas').parentElement!).findByText(/R\$\s999,99/)).toBeTruthy()
    expect(await within(screen.getByText('Saldo no início do período').parentElement!).findByText(/R\$\s777,77/)).toBeTruthy()
  })

  it('reutiliza a edição de previsão e mantém conta e categoria históricas selecionáveis', async () => {
    const { calls } = mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')
    fireEvent.click(screen.getByRole('gridcell', { name: /20\/12\/2026/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Aluguel previsto' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Editar' }))

    expect(await within(dialog).findByRole('heading', { name: 'Editar previsão' })).toBeTruthy()
    expect((within(dialog).getByLabelText('Conta') as HTMLSelectElement).value).toBe(inactiveAccount.id)
    expect((within(dialog).getByLabelText('Categoria') as HTMLSelectElement).value).toBe(inactiveCategory.id)
    fireEvent.change(within(dialog).getByLabelText('Descrição'), { target: { value: 'Aluguel corrigido' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    const update = calls.find((call) => call.url.endsWith('/planned-transactions/planned-pending') && call.init?.method === 'PUT')!
    expect(JSON.parse(String(update.init?.body))).toMatchObject({
      description: 'Aluguel corrigido',
      amount: '1234.56',
      accountId: inactiveAccount.id,
      categoryId: inactiveCategory.id,
    })
  })

  it('confirma cancelamentos com valor e consequência, inclusive a reabertura da previsão', async () => {
    const { calls } = mockApi()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValue(true)
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')
    fireEvent.click(screen.getByRole('gridcell', { name: /10\/12\/2026/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Conta vencida' }))
    let dialog = await screen.findByRole('dialog')
    const cancelPlannedButton = await within(dialog).findByRole('button', { name: 'Cancelar previsão' })

    fireEvent.click(cancelPlannedButton)
    expect(calls.some((call) => call.url.endsWith('/planned-overdue/cancel'))).toBe(false)
    expect(confirm.mock.calls[0][0]).toMatch(/Conta vencida.*R\$ 10,05.*deixará de participar da projeção/)
    fireEvent.click(cancelPlannedButton)
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    fireEvent.click(await screen.findByRole('gridcell', { name: /15\/12\/2026/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Aluguel realizado' }))
    dialog = await screen.findByRole('dialog')
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Cancelar lançamento' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(confirm.mock.calls.at(-1)?.[0]).toMatch(/Aluguel realizado.*R\$ 800,00.*voltará a ficar pendente/)
    const reopened = (await screen.findByText('Aluguel reaberto')).closest('li')!
    expect(within(reopened).getByText('Pendente')).toBeTruthy()
  })

  it('preserva o painel e os dados anteriores quando uma ação falha', async () => {
    const { state } = mockApi()
    state.failNextAction = true
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')
    fireEvent.click(screen.getByRole('gridcell', { name: /10\/12\/2026/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Ver detalhes de Conta vencida' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Cancelar previsão' }))

    expect(await within(dialog).findByRole('alert')).toHaveProperty('textContent', 'Falha ao salvar a previsão (HTTP 503).')
    expect(within(dialog).getByText('Conta vencida')).toBeTruthy()
    expect(screen.getByText('Receitas realizadas')).toBeTruthy()
    expect(screen.getByRole('grid')).toBeTruthy()
  })
})
