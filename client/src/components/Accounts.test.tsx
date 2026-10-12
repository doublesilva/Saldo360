import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AccountBalance, AccountProjection, FinancialAccount } from '../api/accounts'
import type { FamilyMember } from '../api/families'
import { endOfMonth, todayInSaoPaulo } from '../dates'
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

const unavailable = (accountId: string): AccountBalance => ({
  accountId,
  asOf: '2026-10-10',
  openingBalance: null,
  openingBalanceDate: null,
  totalIncome: null,
  totalExpense: null,
  totalTransferIn: null,
  totalTransferOut: null,
  balance: null,
  unavailableReason: 'OpeningBalanceNotConfigured',
})

const unavailableProjection = (accountId: string, through: string): AccountProjection => ({
  accountId,
  baseDate: '2026-10-10',
  through,
  realizedBalance: null,
  overdueIncome: null,
  overdueExpense: null,
  plannedIncome: null,
  plannedExpense: null,
  projectedBalance: null,
  unavailableReason: 'OpeningBalanceNotConfigured',
})

/**
 * API simulada: lista conforme includeInactive, CSRF, gravações, saldo realizado por conta (`balances[accountId]`)
 * e saldo projetado (`projections[accountId]`); sem entrada, ambos indisponíveis por falta de saldo inicial.
 */
function mockApi(
  active: FinancialAccount[] = [itauPf, dinheiro],
  balances: Record<string, AccountBalance> = {},
  projections: Record<string, AccountProjection> = {},
) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      const balance = /\/accounts\/([^/]+)\/balance$/.exec(url)
      if (balance) return Response.json(balances[balance[1]] ?? unavailable(balance[1]))
      const projection = /\/accounts\/([^/]+)\/projection\?through=([\d-]+)$/.exec(url)
      if (projection) {
        const known = projections[projection[1]]
        return Response.json(known ? { ...known, through: projection[2] } : unavailableProjection(projection[1], projection[2]))
      }
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
    expect(calls.filter((c) => !c.url.includes('/balance') && !c.url.includes('/projection')).at(-1)!.url).toBe(
      '/api/families/f1/accounts?includeInactive=true',
    )
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
  const comSaldo: FinancialAccount = { ...itauPf, openingBalance: '1234.56', openingBalanceDate: '2026-10-01' }
  const negativo: FinancialAccount = {
    ...itauPf,
    id: 'a4',
    name: 'Itaú Empresas',
    openingBalance: '-1500.75',
    openingBalanceDate: '2026-09-30',
  }
  const zerado: FinancialAccount = { ...dinheiro, openingBalance: '0.00', openingBalanceDate: '2026-10-01' }

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
    expect(body.openingBalance).toBe('-1500.75')
    expect(body.openingBalanceDate).toBe('2026-10-01')
    expect(String(saveCall(calls)!.init!.body)).toContain('"openingBalance":"-1500.75"')
  })

  it('aceita saldo zero (diferente de não informado)', async () => {
    const calls = await openNewForm()

    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '0,00' } })
    fireEvent.change(screen.getByLabelText('Data de referência'), { target: { value: '2026-10-01' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(saveCall(calls)).toBeTruthy())
    const body = JSON.parse(String(saveCall(calls)!.init!.body))
    expect([body.openingBalance, body.openingBalanceDate]).toEqual(['0.00', '2026-10-01'])
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
    expect([body.openingBalance, body.openingBalanceDate, body.isActive]).toEqual(['1234.56', '2026-10-01', false])
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

describe('Accounts — saldo realizado', () => {
  const comSaldo: FinancialAccount = { ...itauPf, openingBalance: '1000.00', openingBalanceDate: '2026-10-01' }
  const realizado: AccountBalance = {
    accountId: comSaldo.id,
    asOf: '2026-10-10',
    openingBalance: '1000.00',
    openingBalanceDate: '2026-10-01',
    totalIncome: '500.00',
    totalExpense: '200.00',
    totalTransferIn: '300.00',
    totalTransferOut: '300.00',
    balance: '1300.00',
    unavailableReason: null,
  }

  it('mostra "Saldo realizado até" separado do saldo inicial, com entradas e saídas', async () => {
    const calls = mockApi([comSaldo], { [comSaldo.id]: realizado })
    render(<Accounts familyId="f1" members={members} />)

    const item = (await screen.findByText(/Saldo realizado até 10\/10\/2026/)).closest('li')!
    expect(item.textContent).toMatch(/Saldo realizado até 10\/10\/2026:\s*R\$\s1\.300,00/)
    expect(item.textContent).toMatch(/Saldo inicial em 01\/10\/2026:\s*R\$\s1\.000,00/)
    expect(item.textContent).toMatch(/Desde 01\/10\/2026: receitas R\$\s500,00 · despesas R\$\s200,00/)
    expect(item.textContent).toMatch(/Transferências: recebidas R\$\s300,00 · enviadas R\$\s300,00/)
    expect(calls.some((c) => c.url === `/api/families/f1/accounts/${comSaldo.id}/balance`)).toBe(true)
  })

  it('exibe o valor exato vindo da API, mesmo além da precisão de um number', async () => {
    mockApi([comSaldo], { [comSaldo.id]: { ...realizado, totalIncome: '29999999999999.97', balance: '29999999999999.99' } })
    render(<Accounts familyId="f1" members={members} />)

    expect(await screen.findByText(/^R\$\s29\.999\.999\.999\.999,99$/)).toBeTruthy()
  })

  it('saldo negativo aparece destacado e não como crédito disponível', async () => {
    mockApi([comSaldo], { [comSaldo.id]: { ...realizado, totalExpense: '1500.75', balance: '-0.75' } })
    render(<Accounts familyId="f1" members={members} />)

    const amount = await screen.findByText(/^-R\$\s0,75$/)
    expect(amount.className).toContain('amount--negative')
    expect(screen.getByText(/saldo negativo: valor devido, não é crédito disponível/)).toBeTruthy()
  })

  it('sem saldo inicial orienta a configurar e não mostra zero', async () => {
    mockApi([itauPf])
    render(<Accounts familyId="f1" members={members} />)

    const message = await screen.findByText('Saldo realizado indisponível. Configure o saldo inicial para calcular o saldo.')
    expect(message.closest('li')!.textContent).not.toMatch(/R\$/)
  })

  it('consulta anterior à referência explica a indisponibilidade', async () => {
    mockApi([comSaldo], {
      [comSaldo.id]: { ...realizado, asOf: '2026-10-10', openingBalanceDate: '2026-11-01', totalIncome: null, totalExpense: null, totalTransferIn: null, totalTransferOut: null, balance: null, unavailableReason: 'BeforeOpeningBalanceDate' },
    })
    render(<Accounts familyId="f1" members={members} />)

    expect(await screen.findByText(/indisponível: a data de referência do saldo inicial \(01\/11\/2026\) é posterior a 10\/10\/2026/)).toBeTruthy()
  })

  it('consulta de novo quando os lançamentos mudam (balanceVersion) e após salvar o saldo inicial', async () => {
    const calls = mockApi([comSaldo], { [comSaldo.id]: realizado })
    const balanceCalls = () => calls.filter((c) => c.url.endsWith('/balance')).length
    const { rerender } = render(<Accounts familyId="f1" members={members} balanceVersion={0} />)
    await screen.findByText(/Saldo realizado até/)
    const initial = balanceCalls()

    rerender(<Accounts familyId="f1" members={members} balanceVersion={1} />)
    await waitFor(() => expect(balanceCalls()).toBe(initial + 1))

    fireEvent.click(screen.getByRole('button', { name: 'Editar' }))
    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '2.000,00' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(balanceCalls()).toBe(initial + 2))
  })
})

describe('Accounts — saldo inicial como texto decimal exato', () => {
  it.each([
    ['1234.56', /Saldo inicial em 01\/10\/2026:\s*R\$\s1\.234,56/, '1234,56', false],
    ['-1234.56', /Saldo inicial em 01\/10\/2026:\s*-R\$\s1\.234,56/, '-1234,56', true],
    ['9999999999999.99', /Saldo inicial em 01\/10\/2026:\s*R\$\s9\.999\.999\.999\.999,99/, '9999999999999,99', false],
    ['-9999999999999.99', /Saldo inicial em 01\/10\/2026:\s*-R\$\s9\.999\.999\.999\.999,99/, '-9999999999999,99', true],
    ['0.10', /Saldo inicial em 01\/10\/2026:\s*R\$\s0,10/, '0,10', false],
  ])('"%s": exibe, preenche a edição e reenvia o mesmo texto ao mudar só o nome', async (opening, shown, input, negative) => {
    const account: FinancialAccount = { ...itauPf, openingBalance: opening, openingBalanceDate: '2026-10-01' }
    const calls = mockApi([account])
    render(<Accounts familyId="f1" members={members} />)

    const item = (await screen.findByText('Itaú PF')).closest('li')!
    expect(item.textContent).toMatch(shown)
    expect(item.querySelector('.account__opening strong')!.className.includes('amount--negative')).toBe(negative)

    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    expect((screen.getByLabelText('Valor (R$)') as HTMLInputElement).value).toBe(input)
    expect((screen.getByLabelText('Data de referência') as HTMLInputElement).value).toBe('2026-10-01')
    fireEvent.change(screen.getByLabelText('Nome'), { target: { value: 'Itaú PF renomeada' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'PUT')).toBe(true))
    const body = JSON.parse(String(calls.find((c) => c.init?.method === 'PUT')!.init!.body))
    expect([body.name, body.openingBalance, body.openingBalanceDate]).toEqual(['Itaú PF renomeada', opening, '2026-10-01'])
  })

  it('remover valor e data envia null nos dois (não zero)', async () => {
    const account: FinancialAccount = { ...itauPf, openingBalance: '-1234.56', openingBalanceDate: '2026-10-01' }
    const calls = mockApi([account])
    render(<Accounts familyId="f1" members={members} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Editar' }))

    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Data de referência'), { target: { value: '' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(calls.some((c) => c.init?.method === 'PUT')).toBe(true))
    const raw = String(calls.find((c) => c.init?.method === 'PUT')!.init!.body)
    expect(raw).toContain('"openingBalance":null')
    expect(raw).toContain('"openingBalanceDate":null')
  })
})

describe('Accounts — saldo projetado', () => {
  const conta: FinancialAccount = { ...itauPf, openingBalance: '1000.00', openingBalanceDate: '2026-10-01' }
  const projetado: AccountProjection = {
    accountId: conta.id,
    baseDate: '2026-10-12',
    through: '2026-10-31',
    realizedBalance: '1000.00',
    overdueIncome: '0.00',
    overdueExpense: '100.00',
    plannedIncome: '500.00',
    plannedExpense: '200.00',
    projectedBalance: '1200.00',
    unavailableReason: null,
  }
  const projectionCalls = (calls: Call[]) => calls.filter((c) => c.url.includes('/projection'))

  it('começa no último dia do mês atual e mostra projetado, previstas e vencidas em linhas próprias', async () => {
    const calls = mockApi([conta], {}, { [conta.id]: projetado })
    render(<Accounts familyId="f1" members={members} />)

    const item = (await screen.findByText(/Saldo projetado até/)).closest('li')!
    const expected = endOfMonth(todayInSaoPaulo())
    expect((screen.getByLabelText('Projetar até') as HTMLInputElement).value).toBe(expected)
    expect(projectionCalls(calls)[0].url).toBe(`/api/families/f1/accounts/${conta.id}/projection?through=${expected}`)
    expect(item.textContent).toMatch(/Saldo projetado até \d{2}\/\d{2}\/\d{4}:\s*R\$\s1\.200,00/)
    expect(item.textContent).toMatch(/Previstas de 12\/10\/2026 a \d{2}\/\d{2}\/\d{4}: receitas R\$\s500,00 · despesas R\$\s200,00/)
    expect(item.textContent).toMatch(/Vencidas pendentes: receitas R\$\s0,00 · despesas R\$\s100,00/)
    expect(screen.getByText('Inclui previsões pendentes vencidas e até a data escolhida.')).toBeTruthy()
  })

  it('trocar a data-alvo consulta de novo com a data escolhida', async () => {
    const calls = mockApi([conta], {}, { [conta.id]: projetado })
    render(<Accounts familyId="f1" members={members} />)
    await screen.findByText(/Saldo projetado até/)

    fireEvent.change(screen.getByLabelText('Projetar até'), { target: { value: '2027-01-15' } })

    await waitFor(() => expect(projectionCalls(calls).at(-1)!.url).toContain('through=2027-01-15'))
    expect(await screen.findByText(/Saldo projetado até 15\/01\/2027/)).toBeTruthy()
  })

  it('exibe valores exatos e destaca projeção negativa sem fazer contas', async () => {
    mockApi([conta], {}, {
      [conta.id]: { ...projetado, projectedBalance: '-29999999999999.97', plannedExpense: '30000000000999.97' },
    })
    render(<Accounts familyId="f1" members={members} />)

    const amount = await screen.findByText(/^-R\$\s29\.999\.999\.999\.999,97$/)
    expect(amount.className).toContain('amount--negative')
    expect(screen.getByText(/projeção negativa/)).toBeTruthy()
    expect(screen.getByText(/despesas R\$\s30\.000\.000\.000\.999,97/)).toBeTruthy()
  })

  it('indisponível não aparece como zero', async () => {
    mockApi([itauPf])
    render(<Accounts familyId="f1" members={members} />)

    const message = await screen.findByText('Saldo projetado indisponível: depende do saldo realizado de hoje.')
    expect(message.closest('li')!.textContent).not.toMatch(/R\$/)
  })

  it('consulta de novo quando as finanças mudam (balanceVersion)', async () => {
    const calls = mockApi([conta], {}, { [conta.id]: projetado })
    const { rerender } = render(<Accounts familyId="f1" members={members} balanceVersion={0} />)
    await screen.findByText(/Saldo projetado até/)
    const before = projectionCalls(calls).length

    rerender(<Accounts familyId="f1" members={members} balanceVersion={1} />)

    await waitFor(() => expect(projectionCalls(calls).length).toBe(before + 1))
  })
})
