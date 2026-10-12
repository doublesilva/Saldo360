import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { FinancialCategory } from '../api/categories'
import { isOverdue, type PlannedPage, type PlannedTransaction } from '../api/planned'
import { todayInSaoPaulo } from '../dates'
import { Planned } from './Planned'

const account = (id: string, name: string, isActive = true): FinancialAccount => ({
  id,
  name,
  institution: null,
  type: 'Checking',
  financialScope: 'PF',
  responsible: null,
  openingBalance: null,
  openingBalanceDate: null,
  isActive,
  currency: 'BRL',
  createdAt: '2026-10-01T00:00:00Z',
})

const category = (id: string, name: string, type: 'Income' | 'Expense', isActive = true): FinancialCategory => ({
  id,
  name,
  type,
  isActive,
  createdAt: '2026-10-01T00:00:00Z',
})

const itau = account('acc-itau', 'Itaú PF')
const antiga = account('acc-antiga', 'Conta antiga', false)
const salario = category('cat-salario', 'Salário', 'Income')
const aluguel = category('cat-aluguel', 'Aluguel', 'Expense')
const lazer = category('cat-lazer', 'Lazer', 'Expense', false)

/** Data relativa a hoje em America/Sao_Paulo ("yyyy-MM-dd"). */
function shift(days: number): string {
  const [year, month, day] = todayInSaoPaulo().split('-').map(Number)
  return new Date(Date.UTC(year, month - 1, day + days)).toISOString().slice(0, 10)
}

const vencida: PlannedTransaction = {
  id: 'p1',
  type: 'Expense',
  amount: '1800.00',
  dueDate: shift(-3),
  description: 'Aluguel de outubro',
  account: { id: antiga.id, name: antiga.name, isActive: false },
  category: { id: lazer.id, name: lazer.name, isActive: false },
  status: 'Pending',
  realization: null,
  isCancelled: false,
  currency: 'BRL',
  createdAt: '2026-10-01T00:00:00Z',
  updatedAt: null,
  cancelledAt: null,
}

const futura: PlannedTransaction = {
  ...vencida,
  id: 'p2',
  type: 'Income',
  amount: '9999999999999.99',
  dueDate: shift(20),
  description: 'Salário de novembro',
  account: { id: itau.id, name: itau.name, isActive: true },
  category: { id: salario.id, name: salario.name, isActive: true },
}

const hoje: PlannedTransaction = { ...futura, id: 'p3', description: 'Vence hoje', amount: '0.10', dueDate: shift(0) }
const cancelada: PlannedTransaction = {
  ...vencida,
  id: 'p4',
  description: 'Cancelada antiga',
  status: 'Cancelled',
  isCancelled: true,
  cancelledAt: '2026-10-02T00:00:00Z',
}

type Call = { url: string; init?: RequestInit }

function mockApi(
  options: { accounts?: FinancialAccount[]; categories?: FinancialCategory[]; page?: Partial<PlannedPage>; saveStatus?: number; saveBody?: unknown } = {},
) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url.includes('/accounts')) return Response.json(options.accounts ?? [itau, antiga])
      if (url.includes('/categories')) return Response.json(options.categories ?? [salario, aluguel, lazer])
      if (init?.method === 'POST' || init?.method === 'PUT') {
        if (options.saveStatus) return Response.json(options.saveBody ?? {}, { status: options.saveStatus })
        if (url.endsWith('/cancel')) return Response.json({ ...vencida, isCancelled: true })
        return Response.json(vencida, { status: init.method === 'POST' ? 201 : 200 })
      }
      const items = url.includes('includeCancelled=true') ? [vencida, hoje, futura, cancelada] : [vencida, hoje, futura]
      return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length, ...options.page })
    }),
  )
  return calls
}

const writes = (calls: Call[]) => calls.filter((c) => c.init?.method === 'POST' || c.init?.method === 'PUT')
const listCalls = (calls: Call[]) => calls.filter((c) => c.url.includes('/planned-transactions?'))

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

function fill(fields: Partial<Record<'Conta' | 'Categoria' | 'Descrição' | 'Valor (R$)' | 'Data prevista', string>>) {
  for (const [label, value] of Object.entries(fields)) {
    fireEvent.change(screen.getByLabelText(label), { target: { value } })
  }
}

async function openNewForm(options?: Parameters<typeof mockApi>[0]) {
  const calls = mockApi(options)
  render(<Planned familyId="f1" />)
  await screen.findByText('Aluguel de outubro')
  fireEvent.click(screen.getByRole('button', { name: 'Nova previsão' }))
  await screen.findByLabelText('Conta')
  return calls
}

describe('isOverdue', () => {
  it('só previsões pendentes com data anterior a hoje', () => {
    expect(isOverdue({ ...vencida, dueDate: '2026-10-11' }, '2026-10-12')).toBe(true)
    expect(isOverdue({ ...vencida, dueDate: '2026-10-12' }, '2026-10-12')).toBe(false) // hoje não é vencida
    expect(isOverdue({ ...vencida, dueDate: '2026-10-11', status: 'Cancelled', isCancelled: true }, '2026-10-12')).toBe(false)
    expect(isOverdue({ ...vencida, dueDate: '2026-10-11', status: 'Realized' }, '2026-10-12')).toBe(false) // só pendente vence
  })
})

describe('Planned — lista', () => {
  it('mostra data prevista, valor exato, explica que não afeta o saldo e destaca só a vencida', async () => {
    const calls = mockApi()
    render(<Planned familyId="f1" />)

    const overdue = (await screen.findByText('Aluguel de outubro')).closest('li')!
    expect(within(overdue).getByText('Vencida')).toBeTruthy()
    expect(within(overdue).getByText(`Data prevista: ${shift(-3).split('-').reverse().join('/')}`)).toBeTruthy()
    expect(overdue.textContent).toMatch(/−R\$\s1\.800,00/)
    expect(within(overdue).getByText('Lazer (inativa)')).toBeTruthy()
    expect(within(overdue).getByText('Conta antiga (inativa)')).toBeTruthy()

    const future = screen.getByText('Salário de novembro').closest('li')!
    expect(future.textContent).toMatch(/\+R\$\s9\.999\.999\.999\.999,99/)
    expect(within(future).queryByText('Vencida')).toBeNull()
    expect(within(screen.getByText('Vence hoje').closest('li')!).queryByText('Vencida')).toBeNull()
    expect(screen.getByText('Vence hoje').closest('li')!.textContent).toMatch(/R\$\s0,10/)

    expect(screen.getByText(/não afetam o saldo realizado/)).toBeTruthy()
    expect(listCalls(calls)[0].url).toBe('/api/families/f1/planned-transactions?page=1&pageSize=20')
    expect(calls.some((c) => c.url.includes('/balance') || c.url.includes('/transactions'))).toBe(false)
  })

  it('mostra canceladas só sob pedido, sem "Vencida" e sem ações', async () => {
    const calls = mockApi()
    render(<Planned familyId="f1" />)
    await screen.findByText('Aluguel de outubro')
    expect(screen.queryByText('Cancelada antiga')).toBeNull()

    fireEvent.click(screen.getByLabelText('Mostrar canceladas'))

    const item = (await screen.findByText('Cancelada antiga')).closest('li')!
    expect(within(item).getByText('Cancelada')).toBeTruthy()
    expect(within(item).queryByText('Vencida')).toBeNull()
    expect(within(item).queryByRole('button', { name: 'Editar' })).toBeNull()
    expect(listCalls(calls).at(-1)!.url).toContain('includeCancelled=true')
  })

  it('filtra por período da data prevista, tipo, conta e categoria, e pagina', async () => {
    const calls = mockApi({ page: { totalCount: 45 } })
    render(<Planned familyId="f1" />)
    expect(await screen.findByText(/Página 1 de 3 · 45 previsões/)).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Próxima' }))
    await waitFor(() => expect(listCalls(calls).at(-1)!.url).toContain('page=2'))

    fireEvent.change(screen.getByLabelText('Prevista de'), { target: { value: '2026-11-01' } })
    fireEvent.change(screen.getByLabelText('Prevista até'), { target: { value: '2026-11-30' } })
    fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Expense' } })
    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: itau.id } })
    fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: aluguel.id } })

    await waitFor(() => expect(listCalls(calls).at(-1)!.url).toContain(`categoryId=${aluguel.id}`))
    expect(Object.fromEntries(new URL(listCalls(calls).at(-1)!.url, 'http://x').searchParams)).toEqual({
      page: '1',
      pageSize: '20',
      from: '2026-11-01',
      to: '2026-11-30',
      accountId: itau.id,
      categoryId: aluguel.id,
      type: 'Expense',
    })
  })

  it('cancelar pede confirmação e usa o endpoint de cancelamento com CSRF', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm')
    render(<Planned familyId="f1" />)
    const item = (await screen.findByText('Aluguel de outubro')).closest('li')!

    confirm.mockReturnValueOnce(false)
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar previsão' }))
    expect(writes(calls)).toHaveLength(0)

    confirm.mockReturnValueOnce(true)
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar previsão' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(confirm).toHaveBeenLastCalledWith(expect.stringContaining('não poderá mais ser editado'))
    expect(writes(calls)[0].url).toBe('/api/families/f1/planned-transactions/p1/cancel')
    expect((writes(calls)[0].init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
  })
})

describe('Planned — formulário', () => {
  it('cria receita futura com valor em texto exato e data prevista', async () => {
    const calls = await openNewForm()
    fireEvent.click(screen.getByLabelText('Receita prevista'))

    fill({ Conta: itau.id, Categoria: salario.id, Descrição: ' Salário de novembro ', 'Valor (R$)': '5.432,10', 'Data prevista': shift(30) })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    const post = writes(calls)[0]
    expect(post.url).toBe('/api/families/f1/planned-transactions')
    expect(JSON.parse(String(post.init!.body))).toEqual({
      accountId: itau.id,
      categoryId: salario.id,
      type: 'Income',
      description: 'Salário de novembro',
      amount: '5432.10',
      dueDate: shift(30),
    })
  })

  it('aceita data passada (previsão vencida)', async () => {
    const calls = await openNewForm()

    fill({ Conta: itau.id, Categoria: aluguel.id, Descrição: 'Aluguel atrasado', 'Valor (R$)': '1.800,00', 'Data prevista': shift(-10) })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(JSON.parse(String(writes(calls)[0].init!.body))).toMatchObject({ type: 'Expense', dueDate: shift(-10), amount: '1800.00' })
  })

  it('mostra só categorias ativas do tipo e limpa a categoria ao trocar o tipo', async () => {
    await openNewForm()
    const options = () => Array.from((screen.getByLabelText('Categoria') as HTMLSelectElement).options).map((o) => o.textContent)

    expect(options()).toEqual(['Escolha a categoria de despesa', 'Aluguel'])
    fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: aluguel.id } })
    fireEvent.click(screen.getByLabelText('Receita prevista'))

    expect(options()).toEqual(['Escolha a categoria de receita', 'Salário'])
    expect((screen.getByLabelText('Categoria') as HTMLSelectElement).value).toBe('')
  })

  it.each([
    ['0,00', 'O valor deve ser maior que zero. Para saídas, escolha o tipo despesa prevista.'],
    ['-50,00', 'O valor deve ser maior que zero. Para saídas, escolha o tipo despesa prevista.'],
    ['10,005', 'O valor deve ter no máximo duas casas decimais.'],
  ])('recusa valor "%s" sem enviar', async (amount, message) => {
    const calls = await openNewForm()

    fill({ Conta: itau.id, Categoria: aluguel.id, Descrição: 'X', 'Valor (R$)': amount, 'Data prevista': shift(1) })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText(message)).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)
  })

  it('exige data prevista', async () => {
    const calls = await openNewForm()

    fill({ Conta: itau.id, Categoria: aluguel.id, Descrição: 'X', 'Valor (R$)': '10' })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('Informe a data prevista.')).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)
  })

  it('orienta a cadastrar conta e categoria quando não há ativas', async () => {
    await openNewForm({ accounts: [antiga], categories: [lazer, salario] })

    expect(screen.getByText(/Nenhuma conta ativa/)).toBeTruthy()
    expect(screen.getByText(/Nenhuma categoria de despesa ativa/)).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Salvar' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('edição mantém conta e categoria inativas e reenvia o valor exato', async () => {
    const calls = mockApi()
    render(<Planned familyId="f1" />)
    const item = (await screen.findByText('Aluguel de outubro')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    await screen.findByLabelText('Conta')

    expect((screen.getByLabelText('Conta') as HTMLSelectElement).value).toBe(antiga.id)
    expect((screen.getByLabelText('Categoria') as HTMLSelectElement).value).toBe(lazer.id)
    expect((screen.getByLabelText('Valor (R$)') as HTMLInputElement).value).toBe('1800,00')
    fill({ Descrição: 'Aluguel de outubro (reajustado)', 'Data prevista': shift(5) })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    const put = writes(calls)[0]
    expect(put.url).toBe('/api/families/f1/planned-transactions/p1')
    expect(JSON.parse(String(put.init!.body))).toEqual({
      accountId: antiga.id,
      categoryId: lazer.id,
      type: 'Expense',
      description: 'Aluguel de outubro (reajustado)',
      amount: '1800.00',
      dueDate: shift(5),
    })
  })

  it('edição do valor máximo preserva todos os centavos', async () => {
    const calls = mockApi()
    render(<Planned familyId="f1" />)
    const item = (await screen.findByText('Salário de novembro')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    await screen.findByLabelText('Conta')

    expect((screen.getByLabelText('Valor (R$)') as HTMLInputElement).value).toBe('9999999999999,99')
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(String(writes(calls)[0].init!.body)).toContain('"amount":"9999999999999.99"')
  })

  it('exibe o 409 de previsão cancelada', async () => {
    mockApi({ saveStatus: 409, saveBody: { title: 'Previsão cancelada não pode ser editada.' } })
    render(<Planned familyId="f1" />)
    const item = (await screen.findByText('Aluguel de outubro')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    await screen.findByLabelText('Conta')

    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('Previsão cancelada não pode ser editada.')).toBeTruthy()
  })
})

describe('Planned — realização', () => {
  const realizada: PlannedTransaction = {
    ...vencida,
    id: 'p5',
    description: 'Aluguel de setembro',
    amount: '200.00',
    status: 'Realized',
    realization: { transactionId: 't-efetivo', amount: '210.00', transactionDate: '2026-09-10' },
  }

  const efetivo = {
    id: 't-efetivo',
    type: 'Expense',
    amount: '210.00',
    transactionDate: '2026-09-10',
    description: 'Aluguel de setembro',
    account: { id: itau.id, name: itau.name, isActive: true },
    category: { id: aluguel.id, name: aluguel.name, isActive: true },
    isCancelled: false,
    currency: 'BRL',
    createdAt: '2026-09-10T00:00:00Z',
    updatedAt: null,
    cancelledAt: null,
    plannedTransactionId: 'p5',
  }

  function mockRealization(options: { realizeStatus?: number; realizeBody?: unknown } = {}) {
    const calls: Call[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        calls.push({ url, init })
        if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
        if (url.includes('/accounts')) return Response.json([itau])
        if (url.includes('/categories')) return Response.json([salario, aluguel])
        if (url.endsWith('/realize')) return Response.json(options.realizeBody ?? efetivo, { status: options.realizeStatus ?? 201 })
        if (url.includes('/transactions/t-efetivo')) return Response.json(efetivo)
        const items = url.includes('includeRealized=true') ? [vencida, realizada] : [vencida]
        return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length })
      }),
    )
    return calls
  }

  const realizeCalls = (calls: Call[]) => calls.filter((c) => c.url.endsWith('/realize'))

  it('pendente tem "Realizar"; o formulário começa com o valor previsto e hoje, confirma e avisa saldos e lançamentos', async () => {
    const calls = mockRealization()
    const onChanged = vi.fn()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Planned familyId="f1" onChanged={onChanged} />)
    const item = (await screen.findByText('Aluguel de outubro')).closest('li')!

    fireEvent.click(within(item).getByRole('button', { name: 'Realizar' }))

    expect((screen.getByLabelText('Valor efetivo (R$)') as HTMLInputElement).value).toBe('1800,00')
    expect((screen.getByLabelText('Data efetiva') as HTMLInputElement).value).toBe(todayInSaoPaulo())
    fireEvent.change(screen.getByLabelText('Valor efetivo (R$)'), { target: { value: '1.850,5' } })
    fireEvent.change(screen.getByLabelText('Data efetiva'), { target: { value: shift(-1) } })
    fireEvent.click(screen.getByRole('button', { name: 'Realizar' }))

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1))
    expect(confirm).toHaveBeenCalledWith(expect.stringMatching(/por R\$\s1\.850,50 em .*valor previsto \(R\$\s1\.800,00\) é mantido/))
    const post = realizeCalls(calls)[0]
    expect(post.url).toBe('/api/families/f1/planned-transactions/p1/realize')
    expect((post.init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    expect(JSON.parse(String(post.init!.body))).toEqual({ amount: '1850.5', transactionDate: shift(-1) })
    expect(await screen.findByText('Aluguel de outubro')).toBeTruthy() // volta à lista
  })

  it('não envia sem confirmação nem com valor/data inválidos', async () => {
    const calls = mockRealization()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<Planned familyId="f1" />)
    fireEvent.click(within((await screen.findByText('Aluguel de outubro')).closest('li')!).getByRole('button', { name: 'Realizar' }))

    fireEvent.change(screen.getByLabelText('Data efetiva'), { target: { value: shift(1) } })
    fireEvent.click(screen.getByRole('button', { name: 'Realizar' }))
    expect(await screen.findByText('A data efetiva não pode ser futura.')).toBeTruthy()

    fireEvent.change(screen.getByLabelText('Data efetiva'), { target: { value: shift(0) } })
    fireEvent.change(screen.getByLabelText('Valor efetivo (R$)'), { target: { value: '0' } })
    fireEvent.click(screen.getByRole('button', { name: 'Realizar' }))
    expect(await screen.findByText('O valor deve ser maior que zero.')).toBeTruthy()

    fireEvent.change(screen.getByLabelText('Valor efetivo (R$)'), { target: { value: '1.800,00' } })
    fireEvent.click(screen.getByRole('button', { name: 'Realizar' }))
    expect(confirm).toHaveBeenCalledTimes(1)
    expect(realizeCalls(calls)).toHaveLength(0)
  })

  it('exibe a orientação da API (conta inativa) sem avisar os saldos', async () => {
    mockRealization({
      realizeStatus: 400,
      realizeBody: { errors: { accountId: ['A conta da previsão está inativa. Edite a previsão e escolha uma conta ativa antes de realizar.'] } },
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const onChanged = vi.fn()
    render(<Planned familyId="f1" onChanged={onChanged} />)
    fireEvent.click(within((await screen.findByText('Aluguel de outubro')).closest('li')!).getByRole('button', { name: 'Realizar' }))
    fireEvent.click(screen.getByRole('button', { name: 'Realizar' }))

    expect(await screen.findByText(/Edite a previsão e escolha uma conta ativa/)).toBeTruthy()
    expect(onChanged).not.toHaveBeenCalled()
  })

  it('realizadas aparecem só sob pedido, com previsto e efetivo separados, sem "Vencida" nem ações, e acesso ao lançamento', async () => {
    const calls = mockRealization()
    render(<Planned familyId="f1" />)
    await screen.findByText('Aluguel de outubro')
    expect(screen.queryByText('Aluguel de setembro')).toBeNull()

    fireEvent.click(screen.getByLabelText('Mostrar realizadas'))

    const item = (await screen.findByText('Aluguel de setembro')).closest('li')!
    expect(item.textContent).toMatch(/Previsto R\$\s200,00 · Efetivo R\$\s210,00 em 10\/09\/2026/)
    expect(within(item).getByText('Realizada')).toBeTruthy()
    expect(within(item).queryByText('Vencida')).toBeNull()
    expect(within(item).queryByRole('button', { name: 'Realizar' })).toBeNull()
    expect(within(item).queryByRole('button', { name: 'Editar' })).toBeNull()
    expect(within(item).queryByRole('button', { name: 'Cancelar previsão' })).toBeNull()
    expect(calls.filter((c) => c.url.includes('/planned-transactions?')).at(-1)!.url).toContain('includeRealized=true')

    fireEvent.click(within(item).getByRole('button', { name: 'Ver lançamento' }))
    expect(await within(item).findByText(/Lançamento efetivo: Aluguel de setembro · R\$\s210,00 · 10\/09\/2026 · Itaú PF · Aluguel/)).toBeTruthy()
    expect(calls.some((c) => c.url === '/api/families/f1/transactions/t-efetivo')).toBe(true)
  })

  it('recarrega quando outra seção muda as finanças (refreshKey)', async () => {
    const calls = mockRealization()
    const { rerender } = render(<Planned familyId="f1" refreshKey={0} />)
    await screen.findByText('Aluguel de outubro')
    const before = calls.filter((c) => c.url.includes('/planned-transactions?')).length

    rerender(<Planned familyId="f1" refreshKey={1} />)

    await waitFor(() => expect(calls.filter((c) => c.url.includes('/planned-transactions?')).length).toBe(before + 1))
  })
})
