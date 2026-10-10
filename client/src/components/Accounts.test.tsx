import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { FamilyMember } from '../api/families'
import { Accounts } from './Accounts'

const members: FamilyMember[] = [
  { memberId: 'm-ana', displayName: 'Ana', role: 'Owner', joinedAt: '2026-10-10T00:00:00Z', isCurrentUser: true },
  { memberId: 'm-bruno', displayName: 'Bruno', role: 'Member', joinedAt: '2026-10-10T01:00:00Z', isCurrentUser: false },
]

const itauPf: FinancialAccount = {
  id: 'a1',
  name: 'Itaú PF',
  institution: 'Itaú',
  type: 'Checking',
  financialScope: 'PF',
  responsible: { memberId: 'm-ana', displayName: 'Ana' },
  isActive: true,
  currency: 'BRL',
  createdAt: '2026-10-10T00:00:00Z',
}

const dinheiro: FinancialAccount = {
  ...itauPf,
  id: 'a2',
  name: 'Dinheiro',
  institution: null,
  type: 'Cash',
  responsible: null,
}

const antiga: FinancialAccount = { ...itauPf, id: 'a3', name: 'Conta antiga', isActive: false }

type Call = { url: string; init?: RequestInit }

/** API simulada: lista conforme includeInactive, CSRF e gravações. */
function mockApi() {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (init?.method === 'PUT') return Response.json({ ...itauPf, ...JSON.parse(String(init.body)) })
      if (init?.method === 'POST') return Response.json({ ...itauPf, id: 'novo' }, { status: 201 })
      return Response.json(url.includes('includeInactive=true') ? [itauPf, dinheiro, antiga] : [itauPf, dinheiro])
    }),
  )
  return calls
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

describe('Accounts', () => {
  it('lista nome, instituição, tipo, PF/PJ, responsável (ou Conjunta) e situação, sem saldo', async () => {
    const calls = mockApi()
    render(<Accounts familyId="f1" members={members} />)

    const item = (await screen.findByText('Itaú PF')).closest('li')!
    expect(within(item).getByText('Itaú')).toBeTruthy()
    expect(within(item).getByText('Conta corrente')).toBeTruthy()
    expect(within(item).getByText('PF')).toBeTruthy()
    expect(within(item).getByText('Ana')).toBeTruthy()
    expect(within(item).getByText('Ativa')).toBeTruthy()

    const cash = screen.getByText('Dinheiro', { selector: '.account__name' }).closest('li')!
    expect(within(cash).getByText('Conjunta')).toBeTruthy()
    expect(document.body.textContent).not.toMatch(/R\$/)
    expect(calls[0].url).toBe('/api/families/f1/accounts')
  })

  it('mostra contas inativas somente quando solicitado', async () => {
    const calls = mockApi()
    render(<Accounts familyId="f1" members={members} />)
    await screen.findByText('Itaú PF')
    expect(screen.queryByText('Conta antiga')).toBeNull()

    fireEvent.click(screen.getByLabelText('Mostrar contas inativas'))

    expect(await screen.findByText('Conta antiga')).toBeTruthy()
    expect(calls.at(-1)!.url).toBe('/api/families/f1/accounts?includeInactive=true')
    expect(screen.getByRole('button', { name: 'Reativar' })).toBeTruthy()
  })

  it('desativar pede confirmação e envia PUT com CSRF e isActive=false', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm')
    render(<Accounts familyId="f1" members={members} />)
    const item = (await screen.findByText('Itaú PF')).closest('li')!

    confirm.mockReturnValueOnce(false)
    fireEvent.click(within(item).getByRole('button', { name: 'Desativar' }))
    expect(calls.some((c) => c.init?.method === 'PUT')).toBe(false)

    confirm.mockReturnValueOnce(true)
    fireEvent.click(within(item).getByRole('button', { name: 'Desativar' }))

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'PUT')).toBe(true))
    const put = calls.find((c) => c.init?.method === 'PUT')!
    expect(put.url).toBe('/api/families/f1/accounts/a1')
    expect((put.init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    expect(JSON.parse(String(put.init!.body))).toEqual({
      name: 'Itaú PF',
      institution: 'Itaú',
      type: 'Checking',
      financialScope: 'PF',
      responsibleMemberId: 'm-ana',
      isActive: false,
    })
  })

  it('cria conta com responsável escolhido entre os membros, sem campos do servidor', async () => {
    const calls = mockApi()
    render(<Accounts familyId="f1" members={members} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Nova conta' }))

    fireEvent.change(screen.getByLabelText('Nome'), { target: { value: 'Itaú Empresas' } })
    fireEvent.change(screen.getByLabelText('Instituição (opcional)'), { target: { value: 'Itaú' } })
    fireEvent.change(screen.getByLabelText('Pessoa física ou jurídica'), { target: { value: 'PJ' } })
    fireEvent.change(screen.getByLabelText('Responsável'), { target: { value: 'm-bruno' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'POST')).toBe(true))
    const post = calls.find((c) => c.init?.method === 'POST')!
    expect(post.url).toBe('/api/families/f1/accounts')
    expect(JSON.parse(String(post.init!.body))).toEqual({
      name: 'Itaú Empresas',
      institution: 'Itaú',
      type: 'Checking',
      financialScope: 'PJ',
      responsibleMemberId: 'm-bruno',
    })
  })

  it('opção Conjunta envia responsável nulo', async () => {
    const calls = mockApi()
    render(<Accounts familyId="f1" members={members} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Nova conta' }))

    fireEvent.change(screen.getByLabelText('Nome'), { target: { value: 'Dinheiro' } })
    fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Cash' } })
    expect((screen.getByLabelText('Responsável') as HTMLSelectElement).value).toBe('')
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'POST')).toBe(true))
    const body = JSON.parse(String(calls.find((c) => c.init?.method === 'POST')!.init!.body))
    expect(body.responsibleMemberId).toBeNull()
    expect(body.institution).toBeNull()
  })
})
