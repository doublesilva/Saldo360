import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import { Accounts } from './Accounts'

type ResponsibleUserOption = { userId: string; displayName: string; isCurrentUser: boolean; role?: string; joinedAt?: string }
const members: ResponsibleUserOption[] = [{ userId: 'm-ana', displayName: 'Ana', role: 'Owner', joinedAt: '2026-10-10T00:00:00Z', isCurrentUser: true }]

const itauPf: FinancialAccount = {
  id: 'a1', name: 'Itaú PF', institution: 'Itaú', type: 'Checking', financialScope: 'PF', responsible: { userId: 'm-ana', displayName: 'Ana' },
  openingBalance: null, openingBalanceDate: null, isActive: true, currency: 'BRL', createdAt: '2026-10-10T00:00:00Z', origin: 'Pluggy', isPrimary: true,
}
const itauPj: FinancialAccount = { ...itauPf, id: 'a2', name: 'Itaú PJ', financialScope: 'PJ', isPrimary: false, origin: 'Manual' }
const dinheiro: FinancialAccount = { ...itauPf, id: 'a3', name: 'Dinheiro', type: 'Cash', isPrimary: false, origin: 'Manual', responsible: null }

type Call = { url: string; init?: RequestInit }

function mockApi(accounts: FinancialAccount[] = [itauPf, itauPj, dinheiro]) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })
    if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
    if (url.endsWith('/pluggy-integration/status')) return new Response(null, { status: 404 })
    if (/\/accounts\/[^/]+\/balance$/.test(url)) return Response.json({ accountId: 'x', asOf: '2026-10-10', openingBalance: null, openingBalanceDate: null, totalIncome: null, totalExpense: null, totalTransferIn: null, totalTransferOut: null, balance: null, unavailableReason: 'OpeningBalanceNotConfigured' })
    if (/\/accounts\/[^/]+\/projection\?/.test(url)) return Response.json({ accountId: 'x', baseDate: '2026-10-10', through: '2026-10-31', realizedBalance: null, overdueIncome: null, overdueExpense: null, plannedIncome: null, plannedExpense: null, projectedBalance: null, unavailableReason: 'OpeningBalanceNotConfigured' })
    const primary = /\/accounts\/([^/]+)\/primary$/.exec(url)
    if (primary) return Response.json({ ...accounts.find(a => a.id === primary[1]), isPrimary: init?.method === 'PUT' })
    if (init?.method === 'PUT') return Response.json({ ...itauPf, ...JSON.parse(String(init.body)) })
    if (init?.method === 'POST') return Response.json({ ...itauPj, id: 'novo' }, { status: 201 })
    return Response.json(accounts)
  }))
  return calls
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

const mutations = (calls: Call[]) => calls.filter(call => call.init?.method && call.init.method !== 'GET')

describe('Accounts — conta principal (T09.3.3)', () => {
  it('indica a conta principal e a origem Pluggy ou Manual de cada conta', async () => {
    mockApi()
    render(<Accounts workspaceId="f1" members={members} />)

    const pf = (await screen.findByText('Itaú PF')).closest('li')!
    expect(within(pf).getByText('Conta principal')).toBeTruthy()
    expect(within(pf).getByText('Origem: Pluggy')).toBeTruthy()
    const pj = screen.getByText('Itaú PJ').closest('li')!
    expect(within(pj).queryByText('Conta principal')).toBeNull()
    expect(within(pj).getByText('Origem: Manual')).toBeTruthy()
  })

  it('oferece trocar a principal só em conta bancária ativa e confirma antes de chamar a API', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm')
    render(<Accounts workspaceId="f1" members={members} />)
    const pj = (await screen.findByText('Itaú PJ')).closest('li')!
    const cash = screen.getByText('Dinheiro', { selector: '.account__name' }).closest('li')!
    expect(within(cash).queryByRole('button', { name: 'Definir como principal' })).toBeNull()
    expect(within((await screen.findByText('Itaú PF')).closest('li')!).queryByRole('button', { name: 'Definir como principal' })).toBeNull()

    confirm.mockReturnValueOnce(false)
    fireEvent.click(within(pj).getByRole('button', { name: 'Definir como principal' }))
    expect(mutations(calls)).toHaveLength(0)

    confirm.mockReturnValueOnce(true)
    fireEvent.click(within(pj).getByRole('button', { name: 'Definir como principal' }))
    await waitFor(() => expect(mutations(calls)).toHaveLength(1))
    const put = mutations(calls)[0]
    expect([put.url, put.init!.method]).toEqual(['/api/financial-workspaces/f1/accounts/a2/primary', 'PUT'])
    expect((put.init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    // A principal anterior e as demais contas não são tocadas pelo navegador: a troca atômica é do servidor.
    expect(mutations(calls).every(call => call.url.endsWith('/a2/primary'))).toBe(true)
  })

  it('remove a condição de principal de forma explícita', async () => {
    const calls = mockApi()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Accounts workspaceId="f1" members={members} />)
    const pf = (await screen.findByText('Itaú PF')).closest('li')!

    fireEvent.click(within(pf).getByRole('button', { name: 'Remover condição de principal' }))

    await waitFor(() => expect(mutations(calls)).toHaveLength(1))
    expect([mutations(calls)[0].url, mutations(calls)[0].init!.method]).toEqual(['/api/financial-workspaces/f1/accounts/a1/primary', 'DELETE'])
  })

  it('desativar a conta principal avisa e remove a condição na mesma chamada', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Accounts workspaceId="f1" members={members} />)
    const pf = (await screen.findByText('Itaú PF')).closest('li')!

    fireEvent.click(within(pf).getByRole('button', { name: 'Desativar' }))

    await waitFor(() => expect(mutations(calls)).toHaveLength(1))
    expect(String(confirm.mock.calls[0][0])).toMatch(/conta principal/)
    expect(JSON.parse(String(mutations(calls)[0].init!.body))).toMatchObject({ isActive: false, isPrimary: false })
  })

  it('editar sem mexer na principal não envia isPrimary; marcar a opção envia true', async () => {
    const calls = mockApi()
    render(<Accounts workspaceId="f1" members={members} />)
    const pj = (await screen.findByText('Itaú PJ')).closest('li')!

    fireEvent.click(within(pj).getByRole('button', { name: 'Editar' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(mutations(calls)).toHaveLength(1))
    expect(JSON.parse(String(mutations(calls)[0].init!.body))).not.toHaveProperty('isPrimary')

    cleanup()
    const second = mockApi()
    render(<Accounts workspaceId="f1" members={members} />)
    fireEvent.click(within((await screen.findByText('Itaú PJ')).closest('li')!).getByRole('button', { name: 'Editar' }))
    fireEvent.click(await screen.findByLabelText('Definir como conta principal'))
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(mutations(second)).toHaveLength(1))
    expect(JSON.parse(String(mutations(second)[0].init!.body))).toMatchObject({ isPrimary: true })
  })

  it('não oferece a opção de conta principal para dinheiro em espécie', async () => {
    mockApi()
    render(<Accounts workspaceId="f1" members={members} />)
    fireEvent.click(within((await screen.findByText('Dinheiro', { selector: '.account__name' })).closest('li')!).getByRole('button', { name: 'Editar' }))

    await screen.findByLabelText('Nome')
    expect(screen.queryByLabelText('Definir como conta principal')).toBeNull()
  })
})
