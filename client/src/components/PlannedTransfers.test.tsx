import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { PlannedTransfer } from '../api/plannedTransfers'
import { PlannedTransfers } from './PlannedTransfers'

const account = (id: string, name: string, scope: 'PF' | 'PJ'): FinancialAccount => ({
  id, name, institution: null, type: 'Checking', financialScope: scope, responsible: null, openingBalance: null, openingBalanceDate: null, isActive: true, currency: 'BRL', createdAt: '2026-10-01T00:00:00Z',
})
const accounts = [account('pf', 'Itaú PF', 'PF'), account('pj', 'Itaú PJ', 'PJ')]
const planned = (over: Partial<PlannedTransfer> = {}): PlannedTransfer => ({
  id: 'p1', sourceAccount: { id: 'pj', name: 'Itaú PJ', financialScope: 'PJ', isActive: true }, destinationAccount: { id: 'pf', name: 'Itaú PF', financialScope: 'PF', isActive: true },
  amount: '500.00', dueDate: '2026-11-05', description: 'Aporte', status: 'Planned', realizedTransferId: null, linkOrigin: null, createdAt: '2026-10-31T00:00:00Z', cancelledAt: null, candidates: [], ...over,
})

function mockApi(items: PlannedTransfer[]) {
  const calls: { url: string; method: string; body?: string }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, method: init?.method ?? 'GET', body: init?.body as string | undefined })
    if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
    if ((init?.method ?? 'GET') === 'GET') return Response.json(items)
    return Response.json(items[0])
  }))
  return calls
}

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('PlannedTransfers', () => {
  it('mostra prevista, vencida, realizada e cancelada com rótulos distintos e permite cancelar a pendente', async () => {
    const calls = mockApi([
      planned(), planned({ id: 'p2', status: 'Overdue' }), planned({ id: 'p3', status: 'Realized', linkOrigin: 'Inferred', realizedTransferId: 't1' }), planned({ id: 'p4', status: 'Cancelled' }),
    ])
    render(<PlannedTransfers workspaceId="w" accounts={accounts} />)

    expect(await screen.findByText('Prevista')).toBeTruthy()
    expect(screen.getByText('Vencida (não realizada)')).toBeTruthy()
    expect(screen.getByText('Realizada')).toBeTruthy()
    expect(screen.getByText('Cancelada')).toBeTruthy()
    expect(screen.getByText(/Associada automaticamente/)).toBeTruthy()
    fireEvent.click(screen.getAllByRole('button', { name: 'Cancelar previsão' })[0])
    await waitFor(() => expect(calls.some((call) => call.method === 'POST' && call.url.endsWith('/planned-transfers/p1/cancel'))).toBe(true))
  })

  it('cria uma previsão e oferece associar a transferência realizada quando há candidata', async () => {
    const calls = mockApi([planned({ candidates: [{ transferId: 't9', transferDate: '2026-11-04', amount: '500.00', description: 'Aporte' }] })])
    render(<PlannedTransfers workspaceId="w" accounts={accounts} />)
    const form = await screen.findByRole('form', { name: 'Nova transferência prevista' })
    fireEvent.change(within(form).getByLabelText('Origem'), { target: { value: 'pj' } })
    fireEvent.change(within(form).getByLabelText('Destino'), { target: { value: 'pf' } })
    fireEvent.change(within(form).getByLabelText('Valor'), { target: { value: '500,00' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Prever transferência' }))
    await waitFor(() => expect(calls.some((call) => call.method === 'POST' && call.url.endsWith('/planned-transfers') && call.body?.includes('"amount":500'))).toBe(true))

    fireEvent.click(await screen.findByRole('button', { name: 'Associar a esta previsão' }))
    await waitFor(() => expect(calls.some((call) => call.url.endsWith('/planned-transfers/p1/link') && call.body === JSON.stringify({ transferId: 't9' }))).toBe(true))
  })
})
