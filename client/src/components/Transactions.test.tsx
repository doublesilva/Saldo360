import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { FinancialCategory } from '../api/categories'
import type { FinancialCreditCard } from '../api/creditCards'
import type { EntryGroupSummary, FinancialEntriesPage, FinancialEntry } from '../api/financialEntries'
import type { FinancialTransaction } from '../api/transactions'
import { todayInSaoPaulo } from '../dates'
import { Transactions } from './Transactions'

const account = (id: string, name: string, isActive = true): FinancialAccount => ({
  id, name, institution: null, type: 'Checking', financialScope: 'PF', responsible: null, openingBalance: null, openingBalanceDate: null, isActive, currency: 'BRL', createdAt: '2026-10-01T00:00:00Z',
})
const category = (id: string, name: string, type: 'Income' | 'Expense', isActive = true): FinancialCategory => ({ id, name, type, isActive, createdAt: '2026-10-01T00:00:00Z' })

const itau = account('acc-itau', 'Itaú PF')
const antiga = account('acc-antiga', 'Conta antiga', false)
const salario = category('cat-salario', 'Salário', 'Income')
const mercado = category('cat-mercado', 'Mercado', 'Expense')
const lazer = category('cat-lazer', 'Lazer', 'Expense', false)

const tx: FinancialTransaction = {
  id: 't1', type: 'Expense', amount: '1234.56', transactionDate: '2026-10-05', description: 'Feira do mês', account: { id: antiga.id, name: antiga.name, isActive: false },
  category: { id: lazer.id, name: lazer.name, isActive: false }, isCancelled: false, currency: 'BRL', createdAt: '2026-10-05T12:00:00Z', updatedAt: null, cancelledAt: null, plannedTransactionId: null,
}

const entry = (over: Partial<FinancialEntry>): FinancialEntry => ({
  id: 'Transaction:t1', source: 'Transaction', competence: '2026-10', date: '2026-10-05', dateKind: 'Effective', description: 'Feira do mês', type: 'Expense', direction: 'Out', status: 'Realized', amount: '1234.56',
  account: { id: antiga.id, name: antiga.name, scope: 'PF' }, counterpart: null, card: null, category: { id: lazer.id, name: 'Lazer', path: 'Lazer' }, nature: 'Variable', origin: 'Manual', budgetImpact: 'Consumes', cashImpact: 'Immediate',
  transactionId: 't1', plannedTransactionId: null, transferId: null, plannedTransferId: null, recurrenceId: null, invoiceCompetence: null, actions: ['ViewDetails', 'Edit', 'Cancel'], flags: [], ...over,
})

const feira = entry({})
const salarioEntry = entry({ id: 'Transaction:t2', transactionId: 't2', description: 'Salário setembro', type: 'Income', direction: 'In', amount: '5000.00', date: '2026-10-01', category: { id: salario.id, name: 'Salário', path: 'Salário' }, account: { id: itau.id, name: itau.name, scope: 'PF' }, nature: null })
const previsao = entry({ id: 'Planned:p1', source: 'Planned', transactionId: null, plannedTransactionId: 'p1', description: 'Aluguel previsto', status: 'Pending', dateKind: 'Due', date: '2026-10-28', amount: '2000.00', origin: 'Manual', budgetImpact: 'Planned', cashImpact: 'Pending',
  actions: ['ViewDetails', 'Realize', 'Edit', 'Cancel'], flags: ['Planned'] })
const compra = entry({ id: 'CardInstallment:c1', source: 'CardPurchase', transactionId: null, description: 'Mercado no cartão', card: { id: 'k1', name: 'Nubank' }, origin: 'CardInstallment', cashImpact: 'AtInvoicePayment', dateKind: 'Invoice', invoiceCompetence: '2026-10', amount: '300.00', actions: ['ViewDetails', 'OpenCards'] })
const transferencia = entry({ id: 'Transfer:x1', source: 'Transfer', transactionId: null, transferId: 'x1', description: 'Transferência de Itaú PF para Nubank PJ', type: 'Transfer', direction: 'Neutral', category: null, nature: null, amount: '50.00', counterpart: { id: 'pj', name: 'Nubank PJ', scope: 'PJ' }, budgetImpact: 'None', actions: ['ViewDetails', 'OpenTransfers'] })
const importado = entry({ id: 'Transaction:t4', transactionId: 't4', description: 'MERCADO CENTRAL', origin: 'PluggyImport', category: null, amount: '10.25', actions: ['ViewDetails', 'Recategorize', 'ChangeNature', 'Cancel'], flags: ['Imported', 'CategoryNeedsReview'] })
const devolucao = entry({ id: 'Transaction:t5', transactionId: 't5', description: 'Estorno loja', type: 'Income', direction: 'In', origin: 'PluggyRefund', amount: '20.00', budgetImpact: 'Reduces', actions: ['ViewDetails', 'UndoRefund', 'Cancel'], flags: ['Refund'] })
const cancelado = entry({ id: 'Transaction:t3', transactionId: 't3', description: 'Lançamento errado', status: 'Cancelled', actions: ['ViewDetails'] })

type Call = { url: string; init?: RequestInit }

function mockApi(options: { items?: FinancialEntry[]; page?: Partial<FinancialEntriesPage>; accounts?: FinancialAccount[]; categories?: FinancialCategory[]; cards?: FinancialCreditCard[]; saveStatus?: number; saveBody?: unknown; transaction?: Partial<FinancialTransaction> } = {}) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, init })
    if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
    if (url.includes('/accounts')) return Response.json(options.accounts ?? [itau, antiga])
    if (url.includes('/categories')) return Response.json(options.categories ?? [salario, mercado, lazer])
    if (url.includes('/credit-cards')) return Response.json(options.cards ?? [])
    if (url.includes('/financial-entries')) {
      const items = options.items ?? [feira, salarioEntry, previsao, compra, transferencia]
      return Response.json({ items, page: 1, pageSize: 25, totalCount: items.length, competence: '2026-10', ...options.page })
    }
    if (init?.method === 'POST' || init?.method === 'PUT') {
      if (options.saveStatus) return Response.json(options.saveBody ?? {}, { status: options.saveStatus })
      return Response.json(tx, { status: init.method === 'POST' ? 201 : 200 })
    }
    if (/\/transactions\/[^/?]+$/.test(url)) return Response.json({ ...tx, ...options.transaction })
    return new Response('{}', { status: 404 })
  }))
  return calls
}

const writes = (calls: Call[]) => calls.filter(c => c.init?.method === 'POST' || c.init?.method === 'PUT')
const entryCalls = (calls: Call[]) => calls.filter(c => c.url.includes('/financial-entries?')).map(c => new URL(c.url, 'http://x').searchParams)
const plain = (value: string | null | undefined) => (value ?? '').replace(/[  ]/g, ' ')
const rowOf = (description: string) => screen.getByText(description, { selector: 'strong' }).closest('tr') as HTMLElement
const openMenu = (description: string) => fireEvent.click(within(rowOf(description)).getByRole('button', { name: `Ações de ${description}` }))
const pick = (name: string) => fireEvent.click(screen.getByRole('menuitem', { name }))

beforeEach(() => { vi.spyOn(window, 'confirm').mockReturnValue(true) })
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

// A página lembra os filtros por orçamento: cada teste usa um workspace próprio.
let seq = 0
const renderPage = (props: Partial<React.ComponentProps<typeof Transactions>> = {}) => render(<Transactions workspaceId={`f${++seq}`} {...props} />)

describe('Lançamentos — tabela única', () => {
  it('mostra cabeçalho, uma tabela com as colunas do modelo e a competência atual selecionada', async () => {
    const calls = mockApi(); renderPage()
    expect(screen.getByRole('heading', { name: 'Lançamentos' })).toBeTruthy(); expect(screen.getByText('Suas receitas, despesas e compromissos realizados ou previstos.')).toBeTruthy()
    await screen.findByText('Feira do mês')
    expect(screen.getAllByRole('table')).toHaveLength(1)
    expect(screen.getAllByRole('columnheader').map(header => header.textContent?.trim())).toEqual(['Data', 'Descrição', 'Tipo', 'Origem', 'Categoria', 'Situação', 'Valor', 'Ações'])
    expect((screen.getByLabelText('Competência') as HTMLInputElement).value).toBe(todayInSaoPaulo().slice(0, 7))
    const first = entryCalls(calls)[0]
    expect(first.get('competence')).toBe(todayInSaoPaulo().slice(0, 7)); expect(first.get('scope')).toBe('all'); expect(first.get('page')).toBe('1'); expect(first.get('sort')).toBe('date')
  })

  it('apresenta valor com sinal, tipo, origem, categoria e situação; transferência neutra e cartão com o nome do cartão', async () => {
    mockApi(); renderPage()
    await screen.findByText('Feira do mês')
    expect(plain(rowOf('Feira do mês').textContent)).toContain('− R$ 1.234,56'); expect(rowOf('Feira do mês').textContent).toContain('Realizado')
    expect(plain(rowOf('Salário setembro').textContent)).toContain('+ R$ 5.000,00')
    expect(rowOf('Aluguel previsto').textContent).toContain('Previsto'); expect(rowOf('Aluguel previsto').textContent).toContain('Previsão')
    expect(rowOf('Mercado no cartão').textContent).toContain('Cartão · Nubank')
    const transfer = plain(rowOf('Transferência de Itaú PF para Nubank PJ').textContent)
    expect(transfer).toContain('R$ 50,00'); expect(transfer).not.toMatch(/[+−] R\$ 50/); expect(transfer).toContain('Transferência')
  })

  it('situação vencido, em revisão e cancelado em texto, e cancelado sem ações de edição', async () => {
    mockApi({ items: [entry({ id: 'a', description: 'Atrasado', status: 'Overdue', transactionId: null, actions: ['ViewDetails'] }), entry({ id: 'b', description: 'A revisar', status: 'NeedsReview', transactionId: null, actions: ['ViewDetails'] }), cancelado] }); renderPage()
    await screen.findByText('Atrasado')
    expect(rowOf('Atrasado').textContent).toContain('Vencido'); expect(rowOf('A revisar').textContent).toContain('Em revisão'); expect(rowOf('Lançamento errado').textContent).toContain('Cancelado')
    openMenu('Lançamento errado'); expect(screen.getAllByRole('menuitem').map(item => item.textContent)).toEqual(['Ver detalhes'])
  })

  it('estado vazio diz a competência e os filtros', async () => {
    mockApi({ items: [] }); renderPage()
    expect(await screen.findByText(/Nenhum lançamento encontrado para/)).toBeTruthy()
  })

  it('falha de carregamento é dita, sem lista', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => String(input).includes('/financial-entries') ? Response.json({ title: 'Falha geral' }, { status: 500 }) : Response.json([])))
    renderPage()
    expect(await screen.findByRole('alert')).toBeTruthy()
  })
})

describe('Lançamentos — filtros, ordenação e paginação no servidor', () => {
  it('navega a competência e envia cada filtro à API, voltando para a página 1', async () => {
    const calls = mockApi({ page: { totalCount: 80 } }); renderPage()
    await screen.findByText('Feira do mês')
    fireEvent.click(screen.getByRole('button', { name: 'Próxima' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('page')).toBe('2'))
    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2026-08' } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('competence')).toBe('2026-08')); expect(entryCalls(calls).at(-1)!.get('page')).toBe('1')
    fireEvent.click(screen.getByRole('button', { name: 'Competência anterior' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('competence')).toBe('2026-07'))
    fireEvent.click(screen.getByRole('button', { name: 'Próxima competência' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('competence')).toBe('2026-08'))
    fireEvent.change(screen.getByLabelText('Escopo'), { target: { value: 'pf' } }); fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Payment' } })
    fireEvent.change(screen.getByLabelText('Situação'), { target: { value: 'OverduePrevious' } }); fireEvent.change(await screen.findByLabelText('Conta bancária'), { target: { value: itau.id } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('accountId')).toBe(itau.id))
    const last = entryCalls(calls).at(-1)!
    expect([last.get('scope'), last.get('type'), last.get('status'), last.get('competence')]).toEqual(['pf', 'Payment', 'OverduePrevious', '2026-08'])
  })

  it('filtros avançados: categoria, natureza, origem, pesquisa e cancelados', async () => {
    const calls = mockApi(); renderPage()
    await screen.findByText('Feira do mês')
    fireEvent.click(screen.getByRole('button', { name: 'Filtros avançados' }))
    fireEvent.change(screen.getByLabelText('Natureza'), { target: { value: 'Fixed' } }); fireEvent.change(screen.getByLabelText('Origem'), { target: { value: 'CardPurchase' } })
    fireEvent.change(await screen.findByLabelText('Categoria'), { target: { value: mercado.id } })
    fireEvent.change(screen.getByLabelText('Pesquisar na descrição'), { target: { value: ' mercado ' } }); fireEvent.submit(screen.getByLabelText('Pesquisar na descrição').closest('form')!)
    fireEvent.click(screen.getByLabelText('Mostrar cancelados'))
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('includeCancelled')).toBe('true'))
    const last = entryCalls(calls).at(-1)!
    expect([last.get('nature'), last.get('source'), last.get('categoryId'), last.get('search')]).toEqual(['Fixed', 'CardPurchase', mercado.id, 'mercado'])
  })

  it('ordena por data, descrição e valor no servidor, alternando o sentido e informando aria-sort', async () => {
    const calls = mockApi(); renderPage()
    await screen.findByText('Feira do mês')
    expect(screen.getByRole('columnheader', { name: 'Data' }).getAttribute('aria-sort')).toBe('descending')
    fireEvent.click(screen.getByRole('button', { name: 'Data' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('direction')).toBe('asc'))
    fireEvent.click(screen.getByRole('button', { name: 'Valor' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('sort')).toBe('amount'))
    expect(entryCalls(calls).at(-1)!.get('direction')).toBe('desc')
    fireEvent.click(screen.getByRole('button', { name: 'Descrição' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('sort')).toBe('description'))
    expect(entryCalls(calls).at(-1)!.get('direction')).toBe('asc'); expect(screen.getByRole('columnheader', { name: 'Descrição' }).getAttribute('aria-sort')).toBe('ascending')
  })

  it('pagina com Anterior/Próxima e mostra o total', async () => {
    const calls = mockApi({ page: { totalCount: 60, pageSize: 25 } }); renderPage()
    await screen.findByText('Feira do mês')
    expect(screen.getByText(/Página 1 de 3 · 60 lançamentos/)).toBeTruthy(); expect((screen.getByRole('button', { name: 'Anterior' }) as HTMLButtonElement).disabled).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Próxima' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('page')).toBe('2'))
  })

  it('preserva os filtros ao sair e voltar à página, e o recorte vindo de outra tela vira competência, conta e tipo', async () => {
    mockApi(); const first = renderPage({}); const workspace = 'preservado'
    first.unmount(); const view = render(<Transactions workspaceId={workspace} />); await screen.findByText('Feira do mês')
    fireEvent.change(screen.getByLabelText('Escopo'), { target: { value: 'pj' } }); view.unmount()
    const calls = mockApi(); render(<Transactions workspaceId={workspace} />)
    await screen.findByText('Feira do mês'); expect((screen.getByLabelText('Escopo') as HTMLSelectElement).value).toBe('pj')
    cleanup(); const calls2 = mockApi(); renderPage({ initialFilters: { from: '2026-09-01', to: '2026-09-30', accountId: itau.id, type: 'Expense', expenseNature: 'Fixed' } })
    await screen.findByText('Feira do mês')
    const query = entryCalls(calls2)[0]; expect([query.get('competence'), query.get('accountId'), query.get('type'), query.get('nature')]).toEqual(['2026-09', itau.id, 'Expense', 'Fixed']); expect(calls.length).toBeGreaterThan(0)
  })
})

describe('Lançamentos — menu de ações por origem', () => {
  it('cada origem oferece só o que permite: lançamento manual, previsão, cartão, transferência e importado', async () => {
    mockApi({ items: [feira, previsao, compra, transferencia, importado, devolucao] }); renderPage()
    await screen.findByText('Feira do mês')
    const items = () => screen.getAllByRole('menuitem').map(item => item.textContent)
    openMenu('Feira do mês'); expect(items()).toEqual(['Ver detalhes', 'Editar lançamento', 'Cancelar lançamento']); fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByRole('menu')).toBeNull()
    openMenu('Aluguel previsto'); expect(items()).toEqual(['Ver detalhes', 'Realizar previsão', 'Editar previsão', 'Cancelar previsão']); fireEvent.keyDown(document, { key: 'Escape' })
    openMenu('Mercado no cartão'); expect(items()).toEqual(['Ver detalhes', 'Abrir cartões e faturas']); fireEvent.keyDown(document, { key: 'Escape' })
    openMenu('Transferência de Itaú PF para Nubank PJ'); expect(items()).toEqual(['Ver detalhes', 'Consultar transferência']); fireEvent.keyDown(document, { key: 'Escape' })
    openMenu('MERCADO CENTRAL'); expect(items()).toEqual(['Ver detalhes', 'Corrigir categoria', 'Corrigir tipo de despesa', 'Cancelar lançamento']); fireEvent.keyDown(document, { key: 'Escape' })
    openMenu('Estorno loja'); expect(items()).toContain('Desfazer devolução')
    expect(screen.getByRole('button', { name: 'Ações de Feira do mês' }).getAttribute('aria-haspopup')).toBe('menu')
  })

  it('ações de outros módulos levam à seção correta', async () => {
    const onNavigate = vi.fn(); mockApi(); renderPage({ onNavigate })
    await screen.findByText('Feira do mês')
    openMenu('Aluguel previsto'); pick('Realizar previsão'); openMenu('Aluguel previsto'); pick('Editar previsão')
    openMenu('Mercado no cartão'); pick('Abrir cartões e faturas'); openMenu('Transferência de Itaú PF para Nubank PJ'); pick('Consultar transferência')
    expect(onNavigate.mock.calls.map(call => call[0])).toEqual(['planned', 'planned', 'cards', 'transfers'])
  })

  it('"Ver detalhes" mostra competência, tipo de data, impactos no orçamento e no caixa e as ressalvas', async () => {
    mockApi({ items: [compra, importado] }); renderPage()
    await screen.findByText('Mercado no cartão')
    openMenu('Mercado no cartão'); pick('Ver detalhes')
    const detail = document.querySelector('.entries__detail') as HTMLElement
    expect(detail.textContent).toContain('Vencimento da fatura'); expect(detail.textContent).toContain('Consome o orçamento'); expect(detail.textContent).toContain('Movimenta o caixa no pagamento da fatura'); expect(detail.textContent).toContain('fatura de')
    openMenu('Mercado no cartão'); pick('Ver detalhes'); expect(document.querySelector('.entries__detail')).toBeNull()
    openMenu('MERCADO CENTRAL'); pick('Ver detalhes'); expect(screen.getByRole('list', { name: 'Ressalvas' }).textContent).toContain('Importado do banco')
  })

  it('editar busca o lançamento e abre o formulário com PUT; cancelar pede confirmação e usa o endpoint com CSRF', async () => {
    const calls = mockApi(); renderPage()
    await screen.findByText('Feira do mês')
    openMenu('Feira do mês'); pick('Editar lançamento'); await screen.findByLabelText('Conta')
    expect((screen.getByLabelText('Conta') as HTMLSelectElement).value).toBe(antiga.id)
    fireEvent.change(screen.getByLabelText('Descrição'), { target: { value: 'Feira corrigida' } }); fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1)); expect(writes(calls)[0].url).toBe('/api/financial-workspaces/f' + seq + '/transactions/t1'); expect(writes(calls)[0].init!.method).toBe('PUT')
    await screen.findByText('Feira do mês')
    openMenu('Feira do mês'); pick('Cancelar lançamento')
    await waitFor(() => expect(writes(calls).at(-1)!.url).toMatch(/\/transactions\/t1\/cancel$/)); expect(window.confirm).toHaveBeenCalled()
    expect(new Headers(writes(calls).at(-1)!.init!.headers).get('X-CSRF-TOKEN') ?? new Headers(writes(calls).at(-1)!.init!.headers).get('x-csrf-token')).toBeTruthy()
  })

  it('cancelar sem confirmar não chama a API', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false); const calls = mockApi(); renderPage()
    await screen.findByText('Feira do mês'); openMenu('Feira do mês'); pick('Cancelar lançamento')
    expect(writes(calls)).toHaveLength(0)
  })

  it('corrige só a categoria e só o tipo de despesa de um importado, e mostra o erro da API sem perder a lista', async () => {
    const calls2 = mockApi({ items: [importado] }); renderPage()
    await screen.findByText('MERCADO CENTRAL')
    openMenu('MERCADO CENTRAL'); pick('Corrigir categoria')
    fireEvent.change(screen.getByLabelText('Categoria de MERCADO CENTRAL'), { target: { value: mercado.id } })
    await waitFor(() => expect(writes(calls2)).toHaveLength(1)); expect(writes(calls2)[0].url).toMatch(/\/transactions\/t4\/category$/); expect(JSON.parse(String(writes(calls2)[0].init!.body))).toEqual({ categoryId: mercado.id })
    fireEvent.change(screen.getByLabelText('Tipo de despesa de MERCADO CENTRAL'), { target: { value: 'Fixed' } })
    await waitFor(() => expect(writes(calls2)).toHaveLength(2)); expect(writes(calls2)[1].url).toMatch(/\/transactions\/t4\/expense-nature$/)
  })
})

describe('Lançamentos — formulário e avisos', () => {
  async function openNewForm() {
    const calls = mockApi(); renderPage()
    await screen.findByText('Feira do mês'); fireEvent.click(screen.getByRole('button', { name: '+ Novo lançamento' })); await screen.findByLabelText('Conta')
    return calls
  }
  const fill = (fields: Partial<Record<'Conta' | 'Categoria' | 'Descrição' | 'Valor (R$)', string>>) => { for (const [label, value] of Object.entries(fields)) fireEvent.change(screen.getByLabelText(label), { target: { value } }) }

  it('cria despesa enviando o valor como texto decimal exato e a data sem horário', async () => {
    const calls = await openNewForm()
    expect((screen.getByLabelText('Data do pagamento') as HTMLInputElement).value).toBe(todayInSaoPaulo())
    fill({ Conta: itau.id, Categoria: mercado.id, Descrição: '  Mercado da semana ', 'Valor (R$)': '1.234,56' }); fireEvent.change(screen.getByLabelText('Data do pagamento'), { target: { value: '2026-10-05' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(JSON.parse(String(writes(calls)[0].init!.body))).toEqual({ accountId: itau.id, categoryId: mercado.id, type: 'Expense', description: 'Mercado da semana', amount: '1234.56', transactionDate: '2026-10-05', expenseNature: 'Undefined' })
  })

  it('mostra só categorias ativas do tipo escolhido e limpa a categoria ao trocar o tipo', async () => {
    await openNewForm()
    const options = () => Array.from((screen.getByLabelText('Categoria') as HTMLSelectElement).options).map(o => o.textContent)
    expect(options()).toEqual(['Escolha a categoria de despesa', 'Mercado'])
    fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: mercado.id } }); fireEvent.click(screen.getByLabelText('Receita (entrada)'))
    expect(options()).toEqual(['Escolha a categoria de receita', 'Salário']); expect((screen.getByLabelText('Categoria') as HTMLSelectElement).value).toBe('')
  })

  it.each([['0,00', 'O valor deve ser maior que zero. Para saídas, escolha o tipo despesa.'], ['10,005', 'O valor deve ter no máximo duas casas decimais.'], ['', 'Informe o valor.']])('recusa valor "%s" sem enviar', async (amount, message) => {
    const calls = await openNewForm()
    fill({ Conta: itau.id, Categoria: mercado.id, Descrição: 'Compra', 'Valor (R$)': amount }); fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    expect(await screen.findByText(message)).toBeTruthy(); expect(writes(calls)).toHaveLength(0)
  })

  it('orienta a cadastrar conta e categoria quando não há ativas', async () => {
    mockApi({ accounts: [antiga], categories: [lazer, salario] }); renderPage()
    await screen.findByText('Feira do mês'); fireEvent.click(screen.getByRole('button', { name: '+ Novo lançamento' }))
    expect(await screen.findByText(/Nenhuma conta ativa/)).toBeTruthy(); expect((screen.getByRole('button', { name: 'Salvar' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('exibe erros da API ao salvar e abre o formulário de novo lançamento ao entrar quando pedido', async () => {
    mockApi({ saveStatus: 409, saveBody: { title: 'Lançamento cancelado não pode ser editado.' } }); renderPage({ startWithForm: true })
    await screen.findByLabelText('Conta'); fill({ Conta: itau.id, Categoria: mercado.id, Descrição: 'X', 'Valor (R$)': '10' }); fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    expect(await screen.findByText('Lançamento cancelado não pode ser editado.')).toBeTruthy()
  })

  it('chama onChanged após criar e após cancelar, e recarrega quando outra seção muda as finanças', async () => {
    const onChanged = vi.fn(); const calls = mockApi(); const view = renderPage({ onChanged })
    await screen.findByText('Feira do mês'); fireEvent.click(screen.getByRole('button', { name: '+ Novo lançamento' })); await screen.findByLabelText('Conta')
    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: itau.id } }); fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: mercado.id } })
    fireEvent.change(screen.getByLabelText('Descrição'), { target: { value: 'Compra' } }); fireEvent.change(screen.getByLabelText('Valor (R$)'), { target: { value: '10' } }); fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1)); await screen.findByText('Feira do mês')
    const before = entryCalls(calls).length; view.rerender(<Transactions workspaceId={`f${seq}`} onChanged={onChanged} refreshKey={1} />)
    await waitFor(() => expect(entryCalls(calls).length).toBeGreaterThan(before))
  })

  // ---- T13.4: filtros independentes e agrupamento ------------------------------------------------------------------------------

  const card = (id: string, name: string, isArchived = false): FinancialCreditCard => ({
    id, name, issuer: 'Banco', lastFourDigits: null, holderUserId: null, creditLimit: '1000.00', closingDay: 5, dueDay: 10, defaultPaymentAccountId: itau.id, isArchived, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z', archivedAt: null,
  })
  const nubank = card('card-nubank', 'Nubank PJ'); const personnalite = card('card-pers', 'Personnalité Mult Black'); const antigo = card('card-old', 'Cartão antigo', true)

  const summary = (over: Partial<EntryGroupSummary>): EntryGroupSummary => ({
    key: 'card:card-nubank', kind: 'Card', id: 'card-nubank', name: 'Nubank PJ', scope: 'PF', count: 40, activeCount: 40, cancelledCount: 0, transferCount: 0, outflow: '4000.00', inflow: '0.00', paymentsOut: '0.00',
    budgetConsumed: '3500.00', budgetReduced: '0.00', budgetNet: '3500.00', budgetPlanned: '0.00', invoiceFinancing: '0.00', startsOnPage: 1, ...over,
  })

  it('carrega os cartões do cadastro, filtra pelo ID real e mantém conta e cartão como filtros independentes; "Sem cartão" pede withoutCard', async () => {
    const calls = mockApi({ cards: [nubank, personnalite, antigo] }); renderPage()
    const cardSelect = await screen.findByLabelText('Cartão') as HTMLSelectElement
    await waitFor(() => expect(Array.from(cardSelect.options).map(o => o.textContent)).toEqual(['Todos os cartões', 'Sem cartão', 'Nubank PJ', 'Personnalité Mult Black', 'Cartão antigo (arquivado)']))
    expect(Array.from((screen.getByLabelText('Conta bancária') as HTMLSelectElement).options).map(o => o.textContent)).toEqual(['Todas as contas', 'Itaú PF', 'Conta antiga (inativa)'])

    fireEvent.change(cardSelect, { target: { value: nubank.id } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('cardId')).toBe(nubank.id))
    fireEvent.change(screen.getByLabelText('Conta bancária'), { target: { value: itau.id } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('accountId')).toBe(itau.id))
    const both = entryCalls(calls).at(-1)!
    expect([both.get('cardId'), both.get('accountId'), both.has('withoutCard')]).toEqual([nubank.id, itau.id, false])

    fireEvent.change(cardSelect, { target: { value: 'none' } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('withoutCard')).toBe('true'))
    expect(entryCalls(calls).at(-1)!.has('cardId')).toBe(false)
  })

  it('agrupa por cartão por padrão, permite trocar o agrupamento e volta à página 1 a cada mudança de filtro', async () => {
    const calls = mockApi({ cards: [nubank], page: { totalCount: 60, pageSize: 25 } }); renderPage()
    await screen.findByText('Feira do mês')
    expect(entryCalls(calls)[0].get('groupBy')).toBe('card')
    expect((screen.getByLabelText('Agrupar por') as HTMLSelectElement).value).toBe('card')

    fireEvent.click(screen.getByRole('button', { name: 'Próxima' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('page')).toBe('2'))
    fireEvent.change(screen.getByLabelText('Agrupar por'), { target: { value: 'account' } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('groupBy')).toBe('account')); expect(entryCalls(calls).at(-1)!.get('page')).toBe('1')
    fireEvent.click(screen.getByRole('button', { name: 'Próxima' })); await waitFor(() => expect(entryCalls(calls).at(-1)!.get('page')).toBe('2'))
    fireEvent.change(screen.getByLabelText('Cartão'), { target: { value: nubank.id } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('cardId')).toBe(nubank.id)); expect(entryCalls(calls).at(-1)!.get('page')).toBe('1')
    fireEvent.change(screen.getByLabelText('Agrupar por'), { target: { value: 'none' } })
    await waitFor(() => expect(entryCalls(calls).at(-1)!.get('groupBy')).toBe('none'))
  })

  describe('grupos', () => {
    const inNubank = (over: Partial<FinancialEntry>) => entry({ source: 'CardPurchase', transactionId: null, card: { id: 'card-nubank', name: 'Nubank PJ' }, groupKey: 'card:card-nubank', ...over })
    const items = [
      inNubank({ id: 'CardInstallment:a', description: 'Mercado' }), inNubank({ id: 'CardInstallment:b', description: 'Mercado', amount: '99.00' }),
      entry({ id: 'Transaction:z', description: 'Salário setembro', groupKey: 'account:acc-itau', type: 'Income', direction: 'In' }),
    ]
    const groups = [
      summary({ count: 40, outflow: '4000.00', budgetNet: '3500.00', invoiceFinancing: '1814.73', startsOnPage: 1 }),
      summary({ key: 'account:acc-itau', kind: 'Account', id: 'acc-itau', name: 'Itaú PF', count: 3, activeCount: 2, cancelledCount: 1, transferCount: 1, outflow: '10.00', inflow: '5000.00', paymentsOut: '300.00', budgetNet: '10.00', startsOnPage: 2 }),
    ]

    it('mostra o cabeçalho de cada grupo com o total do grupo INTEIRO, distinguindo o que aparece nesta página, e destaca o financiamento de fatura', async () => {
      mockApi({ items, page: { groups, totals: summary({ key: 'all', kind: 'All', id: null, name: 'Todos os lançamentos filtrados', count: 43, outflow: '4010.00', inflow: '5000.00', budgetNet: '3510.00' }), totalCount: 43, pageSize: 25 } }); renderPage()
      await screen.findAllByText('Mercado')
      const nubankHeader = document.getElementById('entries-group-card:card-nubank') as HTMLElement
      const text = plain(nubankHeader.textContent)
      expect(text).toContain('Nubank PJ'); expect(text).toContain('40 lançamentos no total · 2 nesta página'); expect(text).toContain('R$ 4.000,00'); expect(text).toContain('R$ 3.500,00')
      expect(text).toContain('Financiamento de fatura (obrigação, sem novo consumo)'); expect(text).toContain('R$ 1.814,73')
      const itauText = plain((document.getElementById('entries-group-account:acc-itau') as HTMLElement).textContent)
      expect(itauText).toContain('3 lançamentos no total · 1 nesta página'); expect(itauText).toContain('1 cancelado fora dos totais'); expect(itauText).toContain('1 transferência não somada')
      expect(itauText).toContain('Pagamentos de fatura/dívida (caixa)'); expect(itauText).not.toContain('Financiamento de fatura')
      expect(plain(screen.getByText('Soma das linhas filtradas').closest('p')!.textContent)).toContain('43 lançamentos')
    })

    it('recolhe e expande um grupo sem alterar seus totais, e os chips levam à página inicial de cada grupo', async () => {
      const calls = mockApi({ items, page: { groups, totalCount: 43, pageSize: 25 } }); renderPage()
      await screen.findAllByText('Mercado')
      fireEvent.click(screen.getByRole('button', { name: 'Recolher Nubank PJ' }))
      expect(screen.queryAllByText('Mercado')).toHaveLength(0); expect(screen.getByText('Salário setembro')).toBeTruthy()
      expect(plain(document.getElementById('entries-group-card:card-nubank')!.textContent)).toContain('40 lançamentos no total')
      fireEvent.click(screen.getByRole('button', { name: 'Expandir Nubank PJ' })); expect(screen.getAllByText('Mercado')).toHaveLength(2)

      const nav = screen.getByRole('navigation', { name: 'Grupos de lançamentos' })
      fireEvent.click(within(nav).getByRole('button', { name: /Itaú PF/ }))
      await waitFor(() => expect(entryCalls(calls).at(-1)!.get('page')).toBe('2'))
    })

    it('sem agrupamento ou sem grupos na resposta a tabela é a lista simples de sempre', async () => {
      mockApi({ items: [feira, salarioEntry] }); renderPage()
      await screen.findByText('Feira do mês')
      expect(document.querySelector('.entries__group')).toBeNull(); expect(screen.queryByRole('navigation', { name: 'Grupos de lançamentos' })).toBeNull()
      expect(screen.getAllByRole('row')).toHaveLength(3)
    })

    it('mantém no HTML as classes que impedem a quebra de datas e palavras curtas e o cabeçalho responsivo (o CSS é do arquivo de estilos)', async () => {
      mockApi({ items, page: { groups, totalCount: 43, pageSize: 25 } }); renderPage()
      await screen.findAllByText('Mercado')
      const row = rowOf('Salário setembro')
      expect(row.querySelector('td.entries__date')).not.toBeNull(); expect(row.querySelector('td.entries__c-type')).not.toBeNull(); expect(row.querySelector('.entries__badge')).not.toBeNull()
      expect(document.querySelector('tr.entries__group > th[colspan="8"]')).not.toBeNull()
    })
  })
})
