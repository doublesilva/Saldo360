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
  openingBalance: null,
  openingBalanceDate: null,
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
function mockApi(active: FinancialAccount[] = [itauPf, dinheiro]) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (init?.method === 'PUT') return Response.json({ ...itauPf, ...JSON.parse(String(init.body)) })
      if (init?.method === 'POST') return Response.json({ ...itauPf, id: 'novo' }, { status: 201 })
      return Response.json(url.includes('includeInactive=true') ? [...active, antiga] : active)
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
      openingBalance: null,
      openingBalanceDate: null,
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
      openingBalance: null,
      openingBalanceDate: null,
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

describe('Accounts — saldo inicial', () => {
  const comSaldo: FinancialAccount = { ...itauPf, openingBalance: 1234.56, openingBalanceDate: '2026-10-01' }
  const negativo: FinancialAccount = {
    ...itauPf,
    id: 'a4',
    name: 'Itaú Empresas',
    openingBalance: -1500.75,
    openingBalanceDate: '2026-09-30',
  }
  const zerado: FinancialAccount = { ...dinheiro, openingBalance: 0, openingBalanceDate: '2026-10-01' }

  const saveCall = (calls: Call[]) => calls.find((c) => c.init?.method === 'POST' || c.init?.method === 'PUT')

  async function openNewForm() {
    const calls = mockApi()
    render(<Accounts familyId="f1" members={members} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Nova conta' }))
    fireEvent.change(screen.getByLabelText('Nome'), { target: { value: 'Itaú PF' } })
    return calls
  }

  it('mostra "Saldo inicial não informado" para conta sem configuração, sem assumir zero', async () => {
    mockApi([itauPf])
    render(<Accounts familyId="f1" members={members} />)

    const item = (await screen.findByText('Itaú PF')).closest('li')!
    expect(within(item).getByText('Saldo inicial não informado')).toBeTruthy()
    expect(item.textContent).not.toMatch(/R\$/)
  })

  it('mostra valor e data de referência, inclusive negativo e zero, sem chamar de saldo atual', async () => {
    mockApi([comSaldo, negativo, zerado])
    render(<Accounts familyId="f1" members={members} />)

    const item = (await screen.findByText('Itaú PF')).closest('li')!
    expect(item.textContent).toMatch(/Saldo inicial em 01\/10\/2026:\s*R\$\s1\.234,56/)
    expect(item.textContent).toContain('no início do dia, antes das movimentações dessa data')
    expect(item.textContent).toContain('Não é o saldo atual')

    const empresas = screen.getByText('Itaú Empresas').closest('li')!
    expect(empresas.textContent).toMatch(/Saldo inicial em 30\/09\/2026:\s*-R\$\s1\.500,75/)
    const cash = screen.getByText('Dinheiro', { selector: '.account__name' }).closest('li')!
    expect(cash.textContent).toMatch(/R\$\s0,00/)
    expect(document.body.textContent).not.toMatch(/Saldo atual:/i)
  })

  it('cria conta com saldo negativo em formato brasileiro e data sem horário', async () => {
    const calls = await openNewForm()

    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '-1.500,75' } })
    fireEvent.change(screen.getByLabelText('Data de referência'), { target: { value: '2026-10-01' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(saveCall(calls)).toBeTruthy())
    const body = JSON.parse(String(saveCall(calls)!.init!.body))
    expect(body.openingBalance).toBe(-1500.75)
    expect(body.openingBalanceDate).toBe('2026-10-01')
    expect(String(saveCall(calls)!.init!.body)).toContain('"openingBalance":-1500.75')
  })

  it('aceita saldo zero (diferente de não informado)', async () => {
    const calls = await openNewForm()

    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '0,00' } })
    fireEvent.change(screen.getByLabelText('Data de referência'), { target: { value: '2026-10-01' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(saveCall(calls)).toBeTruthy())
    const body = JSON.parse(String(saveCall(calls)!.init!.body))
    expect([body.openingBalance, body.openingBalanceDate]).toEqual([0, '2026-10-01'])
  })

  it.each([
    ['100,00', '', 'Informe a data de referência do saldo inicial.'],
    ['', '2026-10-01', 'Informe o valor do saldo inicial ou remova a data de referência.'],
    ['10,005', '2026-10-01', 'O saldo inicial deve ter no máximo duas casas decimais.'],
    ['abc', '2026-10-01', 'Valor inválido. Use o formato 1.234,56 (negativo: -1.234,56).'],
  ])('recusa valor "%s" com data "%s" sem enviar nem arredondar', async (amount, date, message) => {
    const calls = await openNewForm()

    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: amount } })
    fireEvent.change(screen.getByLabelText('Data de referência'), { target: { value: date } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText(message)).toBeTruthy()
    expect(saveCall(calls)).toBeUndefined()
  })

  it('edição preenche valor e data, e apagar ambos remove a configuração', async () => {
    const calls = mockApi([comSaldo])
    render(<Accounts familyId="f1" members={members} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Editar' }))

    expect((screen.getByLabelText('Valor (R$)') as HTMLInputElement).value).toBe('1234,56')
    expect((screen.getByLabelText('Data de referência') as HTMLInputElement).value).toBe('2026-10-01')

    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Data de referência'), { target: { value: '' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(saveCall(calls)).toBeTruthy())
    const body = JSON.parse(String(saveCall(calls)!.init!.body))
    expect([body.openingBalance, body.openingBalanceDate, body.isActive]).toEqual([null, null, true])
  })

  it('desativar preserva o saldo inicial configurado', async () => {
    const calls = mockApi([comSaldo])
    vi.spyOn(window, 'confirm').mockReturnValueOnce(true)
    render(<Accounts familyId="f1" members={members} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Desativar' }))

    await waitFor(() => expect(saveCall(calls)).toBeTruthy())
    const body = JSON.parse(String(saveCall(calls)!.init!.body))
    expect([body.openingBalance, body.openingBalanceDate, body.isActive]).toEqual([1234.56, '2026-10-01', false])
  })

  it('exibe o erro devolvido pela API no campo do saldo', async () => {
    const calls: Call[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        calls.push({ url: String(input), init })
        if (String(input) === '/auth/csrf') return Response.json({ token: 'csrf' })
        if (init?.method === 'POST')
          return Response.json({ errors: { openingBalance: ['Erro do servidor no saldo.'] } }, { status: 400 })
        return Response.json([])
      }),
    )
    render(<Accounts familyId="f1" members={members} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Nova conta' }))
    fireEvent.change(screen.getByLabelText('Nome'), { target: { value: 'Itaú PF' } })
    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '10' } })
    fireEvent.change(screen.getByLabelText('Data de referência'), { target: { value: '2026-10-01' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('Erro do servidor no saldo.')).toBeTruthy()
    expect(screen.getByLabelText('Valor (R$)').getAttribute('aria-invalid')).toBe('true')
  })
})
