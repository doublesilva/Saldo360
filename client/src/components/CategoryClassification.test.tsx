import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { FinancialCategory } from '../api/categories'
import type { FinancialTransaction } from '../api/transactions'
import type { FinancialEntry } from '../api/financialEntries'
import { Categories } from './Categories'
import { Transactions } from './Transactions'

/** R02: hierarquia de categorias e tipo de despesa nas telas. API simulada; nada de rede. */

const category = (id: string, name: string, extra: Partial<FinancialCategory> = {}): FinancialCategory => ({
  id,
  name,
  type: 'Expense',
  isActive: true,
  createdAt: '2026-10-01T00:00:00Z',
  ...extra,
})
const sub = (id: string, name: string, parent: FinancialCategory, extra: Partial<FinancialCategory> = {}) =>
  category(id, name, { parentCategoryId: parent.id, parentName: parent.name, isAvailable: parent.isActive && (extra.isActive ?? true), ...extra })

const moradia = category('moradia', 'Moradia')
const energia = sub('energia', 'Energia', moradia)
const agua = sub('agua', 'Água', moradia)
const lazer = category('lazer', 'Lazer')
const salario = category('salario', 'Salário', { type: 'Income' })

const account: FinancialAccount = {
  id: 'acc', name: 'Itaú PF', institution: null, type: 'Checking', financialScope: 'PF', responsible: null,
  openingBalance: null, openingBalanceDate: null, isActive: true, currency: 'BRL', createdAt: '2026-10-01T00:00:00Z',
}

const conta: FinancialTransaction = {
  id: 't1', type: 'Expense', amount: '120.00', transactionDate: '2026-10-05', description: 'Conta de luz',
  account: { id: account.id, name: account.name, isActive: true },
  category: { id: energia.id, name: energia.name, isActive: true, parentName: 'Moradia', path: 'Moradia / Energia' },
  isCancelled: false, currency: 'BRL', createdAt: '2026-10-05T12:00:00Z', updatedAt: null, cancelledAt: null,
  plannedTransactionId: null, expenseNature: 'Fixed',
}
const mercado: FinancialTransaction = {
  ...conta, id: 't2', description: 'Mercado', category: { id: lazer.id, name: lazer.name, isActive: true }, expenseNature: 'Undefined',
}
const importado: FinancialTransaction = { ...mercado, id: 't3', description: 'LOJA IMPORTADA', origin: 'PluggyImport', expenseNature: 'Variable' }
const renda: FinancialTransaction = {
  ...conta, id: 't4', type: 'Income', description: 'Salário setembro', category: { id: salario.id, name: salario.name, isActive: true }, expenseNature: 'Undefined',
}

type Call = { url: string; init?: RequestInit }

/** Linha da tabela unificada (financial-entries) equivalente a uma transação efetiva. */
const entryOf = (t: FinancialTransaction): FinancialEntry => ({
  id: `Transaction:${t.id}`, source: 'Transaction', competence: t.transactionDate.slice(0, 7), date: t.transactionDate, dateKind: 'Effective', description: t.description, type: t.type, direction: t.type === 'Income' ? 'In' : 'Out',
  status: 'Realized', amount: t.amount, account: { id: t.account.id, name: t.account.name, scope: 'PF' }, counterpart: null, card: null,
  category: t.category ? { id: t.category.id, name: t.category.name, path: t.category.path ?? t.category.name } : null, nature: t.type === 'Expense' ? t.expenseNature ?? 'Undefined' : null,
  origin: t.origin ?? 'Manual', budgetImpact: 'Consumes', cashImpact: 'Immediate', transactionId: t.id, plannedTransactionId: null, transferId: null, plannedTransferId: null, recurrenceId: null, invoiceCompetence: null,
  actions: t.origin === 'PluggyImport' ? ['ViewDetails', 'Recategorize', 'ChangeNature', 'Cancel'] : ['ViewDetails', 'Edit', 'Cancel'], flags: [],
})

function mockApi(options: { categories?: FinancialCategory[]; items?: FinancialTransaction[]; saveStatus?: number; saveBody?: unknown } = {}) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url.includes('/accounts')) return Response.json([account])
      if (url.includes('/categories') && init?.method !== 'POST' && init?.method !== 'PUT') return Response.json(options.categories ?? [moradia, energia, agua, lazer, salario])
      if (init?.method === 'POST' || init?.method === 'PUT') {
        if (options.saveStatus) return Response.json(options.saveBody ?? {}, { status: options.saveStatus })
        if (url.includes('/categories')) return Response.json({ ...energia, ...JSON.parse(String(init.body)) }, { status: init.method === 'POST' ? 201 : 200 })
        return Response.json(conta, { status: init.method === 'POST' ? 201 : 200 })
      }
      const items = (options.items ?? [conta, mercado, importado, renda]).map(entryOf)
      return Response.json({ items, page: 1, pageSize: 25, totalCount: items.length, competence: '2026-10' })
    }),
  )
  return calls
}

const writes = (calls: Call[]) => calls.filter((c) => c.init?.method === 'POST' || c.init?.method === 'PUT')
const bodyOf = (call: Call) => JSON.parse(String(call.init!.body)) as Record<string, unknown>

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('Categorias — hierarquia', () => {
  it('mostra a principal com as subcategorias agrupadas e identificadas', async () => {
    mockApi()
    render(<Categories workspaceId="f1" />)

    const subs = await screen.findByRole('list', { name: 'Subcategorias de Moradia' })
    expect(within(subs).getByText('Energia')).toBeTruthy()
    expect(within(subs).getByText('Água')).toBeTruthy()
    expect(within(screen.getByRole('heading', { name: 'Despesas' }).closest('section')!).getByText('Moradia')).toBeTruthy()
    expect(within(screen.getByRole('heading', { name: 'Receitas' }).closest('section')!).queryByText('Moradia')).toBeNull()
  })

  it('cria subcategoria sob a principal identificada, sem enviar o tipo', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)
    const row = (await screen.findByText('Moradia')).closest('.category') as HTMLElement

    fireEvent.click(within(row).getByRole('button', { name: 'Nova subcategoria' }))
    expect(screen.getByText('Moradia', { selector: 'strong' })).toBeTruthy() // principal identificada no formulário
    fireEvent.change(screen.getByLabelText('Nova subcategoria de Moradia'), { target: { value: '  Internet ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar subcategoria' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(writes(calls)[0].url).toBe('/api/financial-workspaces/f1/categories')
    expect(bodyOf(writes(calls)[0])).toEqual({ name: 'Internet', parentCategoryId: 'moradia' })
  })

  it('mostra o erro de duplicidade dentro da principal e não cria em lote por vírgula', async () => {
    const calls = mockApi({ saveStatus: 409, saveBody: { title: 'Já existe uma categoria com esse nome neste nível.' } })
    render(<Categories workspaceId="f1" />)
    const row = (await screen.findByText('Moradia')).closest('.category') as HTMLElement
    fireEvent.click(within(row).getByRole('button', { name: 'Nova subcategoria' }))

    fireEvent.change(screen.getByLabelText('Nova subcategoria de Moradia'), { target: { value: 'Luz, Gás, Internet' } })
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar subcategoria' }))
    expect(await screen.findByText(/uma categoria por vez/)).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)

    fireEvent.change(screen.getByLabelText('Nova subcategoria de Moradia'), { target: { value: 'energia' } })
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar subcategoria' }))
    expect(await screen.findByText('Já existe uma categoria com esse nome neste nível.')).toBeTruthy()
  })

  it('subcategoria não oferece criar um terceiro nível', async () => {
    mockApi()
    render(<Categories workspaceId="f1" />)
    const subRow = (await screen.findByText('Energia')).closest('.category') as HTMLElement

    expect(within(subRow).queryByRole('button', { name: 'Nova subcategoria' })).toBeNull()
  })

  it('principal inativa deixa as subcategorias indisponíveis e avisa na desativação', async () => {
    const inativa = category('velha', 'Velha', { isActive: false })
    const filha = sub('filha', 'Filha', inativa)
    mockApi({ categories: [inativa, filha, moradia, energia] })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<Categories workspaceId="f1" />)

    const filhaRow = (await screen.findByText('Filha')).closest('.category') as HTMLElement
    expect(within(filhaRow).getByText(/indisponível: categoria principal inativa/)).toBeTruthy()

    const moradiaRow = screen.getByText('Moradia').closest('.category') as HTMLElement
    fireEvent.click(within(moradiaRow).getByRole('button', { name: 'Desativar' }))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('As subcategorias ficam indisponíveis para novos registros'))
  })
})

let seq = 0
const page = () => render(<Transactions workspaceId={`c${++seq}`} />)
const lastQuery = (calls: Call[]) => new URL(calls.filter((c) => c.url.includes('/financial-entries?')).at(-1)!.url, 'http://x').searchParams
const rowOf = (description: string) => screen.getByText(description, { selector: 'strong' }).closest('tr') as HTMLElement
const openMenu = (description: string) => fireEvent.click(within(rowOf(description)).getByRole('button', { name: `Ações de ${description}` }))

describe('Lançamentos — categoria/subcategoria e tipo de despesa', () => {
  it('lista "Categoria / Subcategoria" na tabela e o tipo da despesa em texto nos detalhes, e nada para receita', async () => {
    mockApi()
    page()

    await screen.findByText('Conta de luz')
    expect(within(rowOf('Conta de luz')).getAllByText('Moradia / Energia').length).toBeGreaterThan(0)
    openMenu('Conta de luz'); fireEvent.click(screen.getByRole('menuitem', { name: 'Ver detalhes' }))
    expect(document.querySelector('.entries__detail')!.textContent).toContain('Tipo de despesaFixa')
    openMenu('Salário setembro'); fireEvent.click(screen.getByRole('menuitem', { name: 'Ver detalhes' }))
    expect(document.querySelector('.entries__detail')!.textContent).not.toContain('Tipo de despesa')
  })

  it('filtra por tipo de despesa e preserva os demais filtros', async () => {
    const calls = mockApi()
    page()
    await screen.findByText('Conta de luz')

    fireEvent.change(await screen.findByLabelText('Conta bancária'), { target: { value: 'acc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Filtros avançados' }))
    fireEvent.change(screen.getByLabelText('Natureza'), { target: { value: 'Fixed' } })

    await waitFor(() => expect(lastQuery(calls).get('nature')).toBe('Fixed'))
    expect(lastQuery(calls).get('accountId')).toBe('acc')
  })

  it('o filtro de categoria diz que a principal inclui as subcategorias e lista cada subcategoria separada', async () => {
    const calls = mockApi()
    page()
    await screen.findByText('Conta de luz')
    fireEvent.click(screen.getByRole('button', { name: 'Filtros avançados' }))

    const filter = (await screen.findByLabelText('Categoria')) as HTMLSelectElement
    const labels = [...filter.options].map((option) => option.textContent)
    expect(labels).toContain('Moradia (com subcategorias)')
    expect(labels).toContain('Moradia / Energia')

    fireEvent.change(filter, { target: { value: 'energia' } })
    await waitFor(() => expect(lastQuery(calls).get('categoryId')).toBe('energia'))
  })

  it('o formulário de despesa traz o campo com a dica e envia o tipo escolhido; receita não tem o campo', async () => {
    const calls = mockApi()
    page()
    await screen.findByText('Conta de luz')
    fireEvent.click(screen.getByRole('button', { name: '+ Novo lançamento' }))
    await screen.findByLabelText('Conta')

    fireEvent.click(screen.getByLabelText('Receita (entrada)'))
    expect(screen.queryByLabelText('Tipo de despesa')).toBeNull() // receita não tem o conceito
    fireEvent.click(screen.getByLabelText('Despesa (saída)'))

    const nature = screen.getByLabelText('Tipo de despesa') as HTMLSelectElement
    expect(nature.value).toBe('Undefined') // explícito, nunca "variável" por omissão
    expect(screen.getByText('Fixa é um compromisso habitual, mesmo que o valor mude.')).toBeTruthy()
    const options = [...(screen.getByLabelText('Categoria') as HTMLSelectElement).options].map((o) => o.textContent)
    expect(options).toContain('Moradia / Energia')
    expect(options).not.toContain('Salário') // categoria de receita fora de uma despesa

    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: 'acc' } })
    fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: 'energia' } })
    fireEvent.change(nature, { target: { value: 'Fixed' } })
    fireEvent.change(screen.getByLabelText('Descrição'), { target: { value: 'Luz de novembro' } })
    fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '150,00' } })
    fireEvent.click(screen.getByRole('button', { name: /Salvar|Adicionar|Registrar/ }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(bodyOf(writes(calls)[0])).toMatchObject({ categoryId: 'energia', type: 'Expense', expenseNature: 'Fixed' })
  })

  it('não oferece subcategoria indisponível numa nova despesa', async () => {
    const indisponivel = sub('velha-filha', 'Velha filha', category('p', 'P', { isActive: false }))
    mockApi({ categories: [moradia, energia, lazer, indisponivel] })
    page()
    await screen.findByText('Conta de luz')
    fireEvent.click(screen.getByRole('button', { name: '+ Novo lançamento' }))
    await screen.findByLabelText('Conta')

    const labels = [...(screen.getByLabelText('Categoria') as HTMLSelectElement).options].map((o) => o.textContent)

    expect(labels.some((label) => label?.includes('Velha filha'))).toBe(false)
  })

  it('corrige o tipo de um lançamento importado sem mexer na categoria', async () => {
    const calls = mockApi()
    page()
    await screen.findByText('LOJA IMPORTADA')

    openMenu('LOJA IMPORTADA'); fireEvent.click(screen.getByRole('menuitem', { name: 'Corrigir tipo de despesa' }))
    fireEvent.change(screen.getByLabelText('Tipo de despesa de LOJA IMPORTADA'), { target: { value: 'Fixed' } })

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(writes(calls)[0].url).toMatch(/\/transactions\/t3\/expense-nature$/)
    expect(writes(calls)[0].init!.method).toBe('PUT')
    expect(bodyOf(writes(calls)[0])).toEqual({ expenseNature: 'Fixed' })
  })
})
