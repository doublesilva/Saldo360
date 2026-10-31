import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { FinancialCategory } from '../api/categories'
import type { FinancialTransaction, TransactionPage } from '../api/transactions'
import { todayInSaoPaulo } from '../dates'
import { Transactions } from './Transactions'

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
const mercado = category('cat-mercado', 'Mercado', 'Expense')
const lazer = category('cat-lazer', 'Lazer', 'Expense', false)

const feira: FinancialTransaction = {
  id: 't1',
  type: 'Expense',
  amount: '1234.56',
  transactionDate: '2026-10-05',
  description: 'Feira do mês',
  account: { id: antiga.id, name: antiga.name, isActive: false },
  category: { id: lazer.id, name: lazer.name, isActive: false },
  isCancelled: false,
  currency: 'BRL',
  createdAt: '2026-10-05T12:00:00Z',
  updatedAt: null,
  cancelledAt: null,
  plannedTransactionId: null,
}

const pagamento: FinancialTransaction = {
  ...feira,
  id: 't2',
  type: 'Income',
  amount: '5000.00',
  transactionDate: '2026-10-01',
  description: 'Salário setembro',
  account: { id: itau.id, name: itau.name, isActive: true },
  category: { id: salario.id, name: salario.name, isActive: true },
}

const cancelado: FinancialTransaction = { ...pagamento, id: 't3', description: 'Lançamento errado', isCancelled: true }
const importado: FinancialTransaction = {
  ...feira, id: 't4', description: 'MERCADO CENTRAL', amount: '10.25', transactionDate: '2026-10-09', origin: 'PluggyImport',
  account: { id: itau.id, name: itau.name, isActive: true }, category: null,
}

type Call = { url: string; init?: RequestInit }

function mockApi(
  options: {
    accounts?: FinancialAccount[]
    categories?: FinancialCategory[]
    page?: Partial<TransactionPage>
    saveStatus?: number
    saveBody?: unknown
  } = {},
) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url.includes('/accounts')) return Response.json(options.accounts ?? [itau, antiga])
      if (url.includes('/categories')) return Response.json(options.categories ?? [salario, mercado, lazer])
      if (init?.method === 'POST' || init?.method === 'PUT') {
        if (options.saveStatus) return Response.json(options.saveBody ?? {}, { status: options.saveStatus })
        if (url.endsWith('/cancel')) return Response.json({ ...feira, isCancelled: true })
        return Response.json(feira, { status: init.method === 'POST' ? 201 : 200 })
      }
      const items = url.includes('includeCancelled=true') ? [feira, pagamento, cancelado] : [feira, pagamento]
      return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length, ...options.page })
    }),
  )
  return calls
}

const writes = (calls: Call[]) => calls.filter((c) => c.init?.method === 'POST' || c.init?.method === 'PUT')
const listCalls = (calls: Call[]) => calls.filter((c) => c.url.includes('/transactions?'))

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

async function openNewForm() {
  const calls = mockApi()
  render(<Transactions workspaceId="f1" />)
  await screen.findByText('Feira do mês')
  fireEvent.click(screen.getByRole('button', { name: 'Novo lançamento' }))
  await screen.findByLabelText('Conta')
  return calls
}

function fill(fields: Partial<Record<'Conta' | 'Categoria' | 'Descrição' | 'Valor (R$)', string>>) {
  for (const [label, value] of Object.entries(fields)) {
    fireEvent.change(screen.getByLabelText(label), { target: { value } })
  }
}

describe('Transactions — lista', () => {
  it('explica que são movimentações realizadas e mostra valor com sinal, conta e categoria (mesmo inativas)', async () => {
    const calls = mockApi()
    render(<Transactions workspaceId="f1" />)

    const item = (await screen.findByText('Feira do mês')).closest('li')!
    expect(item.textContent).toMatch(/−R\$\s1\.234,56/)
    expect(within(item).getByText('05/10/2026')).toBeTruthy()
    expect(within(item).getByText('Lazer (inativa)')).toBeTruthy()
    expect(within(item).getByText('Conta antiga (inativa)')).toBeTruthy()
    expect(screen.getByText('Salário setembro').closest('li')!.textContent).toMatch(/\+R\$\s5\.000,00/)
    expect(screen.getByText(/já aconteceram/)).toBeTruthy()
    expect(listCalls(calls)[0].url).toBe('/api/financial-workspaces/f1/transactions?page=1&pageSize=20')
  })

  it('mostra cancelados só sob pedido, sem ações de edição', async () => {
    const calls = mockApi()
    render(<Transactions workspaceId="f1" />)
    await screen.findByText('Feira do mês')
    expect(screen.queryByText('Lançamento errado')).toBeNull()

    fireEvent.click(screen.getByLabelText('Mostrar cancelados'))

    const item = (await screen.findByText('Lançamento errado')).closest('li')!
    expect(within(item).getByText('Cancelado')).toBeTruthy()
    expect(within(item).queryByRole('button', { name: 'Editar' })).toBeNull()
    expect(listCalls(calls).at(-1)!.url).toContain('includeCancelled=true')
  })

  it('filtra por período, tipo, conta e categoria e volta para a página 1', async () => {
    const calls = mockApi({ page: { totalCount: 45 } })
    render(<Transactions workspaceId="f1" />)
    await screen.findByText('Feira do mês')

    fireEvent.click(screen.getByRole('button', { name: 'Próxima' }))
    await waitFor(() => expect(listCalls(calls).at(-1)!.url).toContain('page=2'))

    fireEvent.change(screen.getByLabelText('De'), { target: { value: '2026-09-01' } })
    fireEvent.change(screen.getByLabelText('Até'), { target: { value: '2026-09-30' } })
    fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Expense' } })
    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: itau.id } })
    fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: mercado.id } })

    await waitFor(() => expect(listCalls(calls).at(-1)!.url).toContain(`categoryId=${mercado.id}`))
    const query = new URL(listCalls(calls).at(-1)!.url, 'http://x').searchParams
    expect(Object.fromEntries(query)).toEqual({
      page: '1',
      pageSize: '20',
      from: '2026-09-01',
      to: '2026-09-30',
      accountId: itau.id,
      categoryId: mercado.id,
      type: 'Expense',
    })
  })

  it('pagina com Anterior/Próxima e mostra o total', async () => {
    const calls = mockApi({ page: { totalCount: 45 } })
    render(<Transactions workspaceId="f1" />)
    expect(await screen.findByText(/Página 1 de 3 · 45 lançamentos/)).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Anterior' }) as HTMLButtonElement).disabled).toBe(true)

    fireEvent.click(screen.getByRole('button', { name: 'Próxima' }))

    await waitFor(() => expect(listCalls(calls).at(-1)!.url).toContain('page=2'))
  })

  it('cancelar pede confirmação e usa o endpoint de cancelamento com CSRF', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm')
    render(<Transactions workspaceId="f1" />)
    const item = (await screen.findByText('Feira do mês')).closest('li')!

    confirm.mockReturnValueOnce(false)
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar lançamento' }))
    expect(writes(calls)).toHaveLength(0)

    confirm.mockReturnValueOnce(true)
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar lançamento' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(confirm).toHaveBeenLastCalledWith(expect.stringContaining('não poderá mais ser editado'))
    expect(writes(calls)[0].url).toBe('/api/financial-workspaces/f1/transactions/t1/cancel')
    expect((writes(calls)[0].init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
  })
})

describe('Transactions — transferências', () => {
  it('não diz mais que as transferências "terão fluxo próprio": elas já têm seção, com atalho', async () => {
    mockApi()
    const open = vi.fn()
    render(<Transactions workspaceId="f1" onOpenTransfers={open} />)
    await screen.findByText('Feira do mês')

    expect(screen.queryByText(/terão fluxo próprio/)).toBeNull()
    expect(screen.getByText(/não são receita nem despesa e têm a sua própria seção/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Transferências' }))
    expect(open).toHaveBeenCalledTimes(1)
  })
})

describe('Transactions — devolução bancária', () => {
  const devolucao: FinancialTransaction = {
    ...importado, id: 't9', type: 'Income', description: 'Pix devolvido Pix Marketplace', amount: '55.99', transactionDate: '2026-10-23', origin: 'PluggyRefund', category: null,
    refundOf: { originalTransactionId: 't8', originalDescription: 'Pagamento de Pix QR Code', originalDate: '2026-10-13', postingId: 'post-9' },
  }
  const despesa: FinancialTransaction = { ...feira, id: 't8', description: 'Pagamento de Pix QR Code', amount: '55.99', transactionDate: '2026-10-13', refundedAmount: '55.99' }

  it('mostra a devolução como entrada que não é receita, ligada à despesa devolvida, e a despesa com o total devolvido', async () => {
    mockApi({ page: { items: [devolucao, despesa], totalCount: 2 } })
    render(<Transactions workspaceId="f1" />)

    const credit = (await screen.findByText('Pix devolvido Pix Marketplace')).closest('li')!
    expect(within(credit).getByText('Devolução (não é receita)')).toBeTruthy()
    expect(within(credit).getByText(/Devolve: Pagamento de Pix QR Code \(13\/10\/2026\)/)).toBeTruthy()
    expect(within(credit).queryByRole('button', { name: 'Editar' })).toBeNull()
    const expense = screen.getAllByText('Pagamento de Pix QR Code')[0].closest('li')!
    expect(expense.textContent).toMatch(/Devolvido R\$\s55,99/)
  })

  it('desfaz a devolução pelo rastro do crédito, depois de confirmar, e recarrega a lista', async () => {
    const calls = mockApi({ page: { items: [devolucao, despesa], totalCount: 2 } })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const onChanged = vi.fn()
    render(<Transactions workspaceId="f1" onChanged={onChanged} />)

    fireEvent.click(await screen.findByRole('button', { name: 'Desfazer devolução' }))

    await waitFor(() => expect(writes(calls).some((c) => c.url === '/api/financial-workspaces/f1/pluggy-integration/review/post-9/refund/undo')).toBe(true))
    await waitFor(() => expect(onChanged).toHaveBeenCalled())
  })
})

describe('Transactions — formulário', () => {
  it('cria despesa enviando o valor como texto decimal exato e a data sem horário', async () => {
    const calls = await openNewForm()
    expect((screen.getByLabelText('Data do pagamento') as HTMLInputElement).value).toBe(todayInSaoPaulo())

    fill({ Conta: itau.id, Categoria: mercado.id, Descrição: '  Mercado da semana ', 'Valor (R$)': '1.234,56' })
    fireEvent.change(screen.getByLabelText('Data do pagamento'), { target: { value: '2026-10-05' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    const post = writes(calls)[0]
    expect(post.url).toBe('/api/financial-workspaces/f1/transactions')
    expect(JSON.parse(String(post.init!.body))).toEqual({
      accountId: itau.id,
      categoryId: mercado.id,
      type: 'Expense',
      description: 'Mercado da semana',
      amount: '1234.56',
      transactionDate: '2026-10-05',
      expenseNature: 'Undefined', // despesa sempre envia o tipo, e o padrão é "Não definida"
    })
  })

  it('mostra só categorias ativas do tipo escolhido e limpa a categoria ao trocar o tipo', async () => {
    await openNewForm()
    const options = () =>
      Array.from((screen.getByLabelText('Categoria') as HTMLSelectElement).options).map((o) => o.textContent)
    const accounts = Array.from((screen.getByLabelText('Conta') as HTMLSelectElement).options).map((o) => o.value)

    expect(options()).toEqual(['Escolha a categoria de despesa', 'Mercado'])
    expect(accounts).toEqual(['', itau.id]) // conta inativa não é oferecida para novo lançamento
    fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: mercado.id } })

    fireEvent.click(screen.getByLabelText('Receita (entrada)'))

    expect(options()).toEqual(['Escolha a categoria de receita', 'Salário'])
    expect((screen.getByLabelText('Categoria') as HTMLSelectElement).value).toBe('')
    expect(screen.getByLabelText('Data do recebimento')).toBeTruthy()
  })

  it.each([
    ['0,00', 'O valor deve ser maior que zero. Para saídas, escolha o tipo despesa.'],
    ['-50,00', 'O valor deve ser maior que zero. Para saídas, escolha o tipo despesa.'],
    ['10,005', 'O valor deve ter no máximo duas casas decimais.'],
    ['', 'Informe o valor.'],
  ])('recusa valor "%s" sem enviar nem arredondar', async (amount, message) => {
    const calls = await openNewForm()

    fill({ Conta: itau.id, Categoria: mercado.id, Descrição: 'Compra', 'Valor (R$)': amount })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText(message)).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)
  })

  it('recusa data futura em America/Sao_Paulo', async () => {
    const calls = await openNewForm()
    const [year, month, day] = todayInSaoPaulo().split('-').map(Number)
    const tomorrow = new Date(Date.UTC(year, month - 1, day + 1)).toISOString().slice(0, 10)

    fill({ Conta: itau.id, Categoria: mercado.id, Descrição: 'Compra', 'Valor (R$)': '10' })
    fireEvent.change(screen.getByLabelText('Data do pagamento'), { target: { value: tomorrow } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('A data não pode ser futura: registre só movimentações já realizadas.')).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)
  })

  it('orienta a cadastrar conta e categoria quando não há ativas', async () => {
    mockApi({ accounts: [antiga], categories: [lazer, salario] })
    render(<Transactions workspaceId="f1" />)
    await screen.findByText('Feira do mês')
    fireEvent.click(screen.getByRole('button', { name: 'Novo lançamento' }))

    expect(await screen.findByText(/Nenhuma conta ativa/)).toBeTruthy()
    expect(screen.getByText(/Nenhuma categoria de despesa ativa/)).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Salvar' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('edição mantém conta e categoria inativas já usadas e envia PUT', async () => {
    const calls = mockApi()
    render(<Transactions workspaceId="f1" />)
    const item = (await screen.findByText('Feira do mês')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    await screen.findByLabelText('Conta')

    expect((screen.getByLabelText('Conta') as HTMLSelectElement).value).toBe(antiga.id)
    expect((screen.getByLabelText('Categoria') as HTMLSelectElement).value).toBe(lazer.id)
    expect((screen.getByLabelText('Valor (R$)') as HTMLInputElement).value).toBe('1234,56')
    fill({ Descrição: 'Feira do mês (corrigida)' })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    const put = writes(calls)[0]
    expect(put.url).toBe('/api/financial-workspaces/f1/transactions/t1')
    expect(put.init!.method).toBe('PUT')
    expect(JSON.parse(String(put.init!.body))).toEqual({
      accountId: antiga.id,
      categoryId: lazer.id,
      type: 'Expense',
      description: 'Feira do mês (corrigida)',
      amount: '1234.56',
      transactionDate: '2026-10-05',
      expenseNature: 'Undefined', // despesa sempre envia o tipo, e o padrão é "Não definida"
    })
  })

  it('exibe erros da API (ex.: categoria de outro tipo ou lançamento já cancelado)', async () => {
    mockApi({ saveStatus: 409, saveBody: { title: 'Lançamento cancelado não pode ser editado.' } })
    render(<Transactions workspaceId="f1" />)
    const item = (await screen.findByText('Feira do mês')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    await screen.findByLabelText('Conta')

    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('Lançamento cancelado não pode ser editado.')).toBeTruthy()
  })
})

describe('Transactions — aviso de mudança para os saldos', () => {
  it('chama onChanged após criar e após cancelar', async () => {
    mockApi()
    const onChanged = vi.fn()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Transactions workspaceId="f1" onChanged={onChanged} />)
    await screen.findByText('Feira do mês')

    fireEvent.click(screen.getByRole('button', { name: 'Novo lançamento' }))
    await screen.findByLabelText('Conta')
    fill({ Conta: itau.id, Categoria: mercado.id, Descrição: 'Compra', 'Valor (R$)': '10' })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1))

    const item = (await screen.findByText('Feira do mês')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar lançamento' }))
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(2))
  })

  it('não chama onChanged quando a gravação é recusada', async () => {
    mockApi({ saveStatus: 400, saveBody: { errors: { categoryId: ['Categoria inválida.'] } } })
    const onChanged = vi.fn()
    render(<Transactions workspaceId="f1" onChanged={onChanged} />)
    await screen.findByText('Feira do mês')
    fireEvent.click(screen.getByRole('button', { name: 'Novo lançamento' }))
    await screen.findByLabelText('Conta')

    fill({ Conta: itau.id, Categoria: mercado.id, Descrição: 'Compra', 'Valor (R$)': '10' })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('Categoria inválida.')).toBeTruthy()
    expect(onChanged).not.toHaveBeenCalled()
  })
})

describe('Transactions — valor como texto decimal exato', () => {
  const maximo: FinancialTransaction = { ...pagamento, id: 't9', description: 'Valor máximo', amount: '9999999999999.99' }
  const centavos: FinancialTransaction = { ...feira, id: 't8', description: 'Bala', amount: '0.10' }

  function mockPage(items: FinancialTransaction[]) {
    const calls: Call[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        calls.push({ url, init })
        if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
        if (url.includes('/accounts')) return Response.json([itau, antiga])
        if (url.includes('/categories')) return Response.json([salario, mercado, lazer])
        if (init?.method === 'PUT') return Response.json({ ...maximo, ...JSON.parse(String(init.body)) })
        return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length })
      }),
    )
    return calls
  }

  it('exibe centavos e o valor máximo exatamente, a partir do texto', async () => {
    mockPage([maximo, centavos])
    render(<Transactions workspaceId="f1" />)

    expect((await screen.findByText('Valor máximo')).closest('li')!.textContent).toMatch(/\+R\$\s9\.999\.999\.999\.999,99/)
    expect(screen.getByText('Bala').closest('li')!.textContent).toMatch(/−R\$\s0,10/)
  })

  it('edição do valor máximo preenche o campo e reenvia o mesmo texto exato', async () => {
    const calls = mockPage([maximo])
    render(<Transactions workspaceId="f1" />)
    const item = (await screen.findByText('Valor máximo')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    await screen.findByLabelText('Conta')

    expect((screen.getByLabelText('Valor (R$)') as HTMLInputElement).value).toBe('9999999999999,99')
    fill({ Descrição: 'Valor máximo (corrigido)' })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(JSON.parse(String(writes(calls)[0].init!.body))).toMatchObject({
      amount: '9999999999999.99',
      description: 'Valor máximo (corrigido)',
    })
  })

  it('edição de centavos reenvia "0.10", não 0.1', async () => {
    const calls = mockPage([centavos])
    render(<Transactions workspaceId="f1" />)
    const item = (await screen.findByText('Bala')).closest('li')!
    fireEvent.click(within(item).getByRole('button', { name: 'Editar' }))
    await screen.findByLabelText('Conta')

    expect((screen.getByLabelText('Valor (R$)') as HTMLInputElement).value).toBe('0,10')
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(String(writes(calls)[0].init!.body)).toContain('"amount":"0.10"')
  })
})

describe('Transactions — lançamento que realiza previsão', () => {
  const vinculado: FinancialTransaction = { ...feira, id: 't7', description: 'Aluguel de setembro', plannedTransactionId: 'p5' }

  it('mostra o vínculo, não oferece edição e avisa que a previsão volta a pendente ao cancelar', async () => {
    const calls: Call[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        calls.push({ url, init })
        if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
        if (url.includes('/accounts')) return Response.json([itau])
        if (url.includes('/categories')) return Response.json([mercado])
        if (url.endsWith('/cancel')) return Response.json({ ...vinculado, isCancelled: true })
        return Response.json({ items: [vinculado], page: 1, pageSize: 20, totalCount: 1 })
      }),
    )
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const onChanged = vi.fn()
    render(<Transactions workspaceId="f1" onChanged={onChanged} />)
    const item = (await screen.findByText('Aluguel de setembro')).closest('li')!

    expect(within(item).getByText('Realiza previsão')).toBeTruthy()
    expect(within(item).queryByRole('button', { name: 'Editar' })).toBeNull()
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar lançamento' }))

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('a previsão voltará a ficar pendente'))
    expect(calls.some((c) => c.url === '/api/financial-workspaces/f1/transactions/t7/cancel')).toBe(true)
  })

  it('recarrega quando outra seção muda as finanças (refreshKey)', async () => {
    const calls = mockApi()
    const { rerender } = render(<Transactions workspaceId="f1" refreshKey={0} />)
    await screen.findByText('Feira do mês')
    const before = listCalls(calls).length

    rerender(<Transactions workspaceId="f1" refreshKey={1} />)

    await waitFor(() => expect(listCalls(calls).length).toBe(before + 1))
  })
})

describe('Transactions — importados da Pluggy', () => {
  it('identifica o lançamento importado, mostra "Sem categoria" e não oferece edição completa', async () => {
    mockApi({ page: { items: [importado], totalCount: 1 } })
    render(<Transactions workspaceId="f1" />)

    const item = (await screen.findByText('MERCADO CENTRAL')).closest('li')!
    expect(within(item).getByText('Importado da Pluggy')).toBeTruthy()
    expect(within(item).getAllByText('Sem categoria').some((node) => node.tagName === 'SPAN')).toBe(true)
    expect(within(item).queryByRole('button', { name: 'Editar' })).toBeNull()
    expect(within(item).getByRole('button', { name: 'Cancelar lançamento' })).toBeTruthy()
  })

  it('mostra a origem da categoria (Pluggy, regra, IA ou você) e sinaliza o que precisa de revisão', async () => {
    const withCategory = (id: string, description: string, source: NonNullable<FinancialTransaction['categorySource']>): FinancialTransaction => ({
      ...importado, id, description, category: { id: salario.id, name: salario.name, isActive: true }, categorySource: source,
    })
    mockApi({ page: { items: [
      withCategory('i1', 'LOJA PLUGGY', 'Pluggy'), withCategory('i2', 'LOJA IA', 'AI'), withCategory('i3', 'LOJA MANUAL', 'User'), withCategory('i4', 'LOJA LEGADA', 'Mapped'),
      { ...importado, id: 'i5', description: 'SEM CATEGORIA SEGURA', categorySource: 'None', categoryNeedsReview: true },
    ], totalCount: 5 } })
    render(<Transactions workspaceId="f1" />)

    expect(within((await screen.findByText('LOJA PLUGGY')).closest('li')!).getByText('Categoria: Pluggy')).toBeTruthy()
    expect(within(screen.getByText('LOJA IA').closest('li')!).getByText('Categoria: IA')).toBeTruthy()
    expect(within(screen.getByText('LOJA MANUAL').closest('li')!).getByText('Categoria: Você')).toBeTruthy()
    expect(within(screen.getByText('LOJA LEGADA').closest('li')!).getByText('Categoria: Pluggy')).toBeTruthy()
    expect(within(screen.getByText('SEM CATEGORIA SEGURA').closest('li')!).getByText('Sem categoria segura: revisar')).toBeTruthy()
  })

  it('corrige só a categoria (PUT /category com CSRF), oferecendo apenas categorias ativas do mesmo tipo', async () => {
    const calls = mockApi({ page: { items: [importado], totalCount: 1 } })
    render(<Transactions workspaceId="f1" />)
    const select = (await screen.findByLabelText('Categoria de MERCADO CENTRAL')) as HTMLSelectElement

    expect(Array.from(select.options).map((option) => option.text)).toEqual(['Sem categoria', 'Mercado']) // Lazer inativa, Salário é receita
    fireEvent.change(select, { target: { value: mercado.id } })

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    const [write] = writes(calls)
    expect(write.url).toBe('/api/financial-workspaces/f1/transactions/t4/category')
    expect(write.init?.method).toBe('PUT')
    expect(JSON.parse(String(write.init?.body))).toEqual({ categoryId: mercado.id })
    expect((write.init?.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
  })

  it('mostra o erro da API sem perder a lista', async () => {
    mockApi({ page: { items: [importado], totalCount: 1 }, saveStatus: 400, saveBody: { errors: { categoryId: ['A categoria deve ser do mesmo tipo do lançamento.'] } } })
    render(<Transactions workspaceId="f1" />)

    fireEvent.change(await screen.findByLabelText('Categoria de MERCADO CENTRAL'), { target: { value: mercado.id } })

    expect((await screen.findByRole('alert')).textContent).toContain('mesmo tipo')
    expect(screen.getByText('MERCADO CENTRAL')).toBeTruthy()
  })
})
