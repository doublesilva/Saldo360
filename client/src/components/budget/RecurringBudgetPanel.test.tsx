import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { EffectiveBudget, EffectiveTarget } from '../../api/effectiveBudget'
import type { FinancialCategory } from '../../api/categories'
import { RecurringBudgetPanel } from './RecurringBudgetPanel'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' } })

const target = (over: Partial<EffectiveTarget> & Pick<EffectiveTarget, 'target'>): EffectiveTarget => ({
  id: over.target, categoryId: null, name: over.target, amount: null, origin: 'Undefined', removed: false, standardFrom: null, ...over,
})
const budget = (over: Partial<EffectiveBudget> = {}): EffectiveBudget => ({
  competence: '2026-12', budgetId: null, standardConfigured: true, outsideCardsTarget: '7500.00', warnings: [],
  targets: [
    target({ target: 'TotalExpenses', amount: '12500.00', origin: 'Inherited', standardFrom: '2026-11' }),
    target({ target: 'AllCards', amount: '5400.00', origin: 'Customized' }),
    target({ target: 'FixedExpenses', amount: '3000.00', origin: 'Historical' }),
    target({ target: 'Category', id: 'cat-m', categoryId: 'cat-m', name: 'Mercado', amount: '800.00', origin: 'Inherited', standardFrom: '2026-11' }),
  ], ...over,
})
const categories = [
  { id: 'cat-m', name: 'Mercado', type: 'Expense', isActive: true }, { id: 'cat-l', name: 'Lazer', type: 'Expense', isActive: true }, { id: 'cat-r', name: 'Salário', type: 'Income', isActive: true },
] as unknown as FinancialCategory[]

type Call = { method: string; url: URL; body: unknown }
function stub(state: { budget?: EffectiveBudget; post?: (call: Call) => Response; preview?: unknown } = {}) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn((input: string, init?: RequestInit) => {
    const url = new URL(String(input), 'http://x'); const method = init?.method ?? 'GET'
    const call = { method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined }
    calls.push(call)
    if (url.pathname === '/auth/csrf') return Promise.resolve(json({ token: 'csrf' }))
    if (method === 'POST') return Promise.resolve(state.post?.(call) ?? json(state.budget ?? budget()))
    if (url.pathname.endsWith('/effective/execution')) return Promise.resolve(json({ targets: [{ targetId: 'AllCards', groupingType: 'AllCards', planned: '5400.00', consumed: '1000.00', available: '4400.00', exceededAmount: '0.00' }] }))
    if (url.pathname.endsWith('/effective/history')) return Promise.resolve(json([{ scope: 'FromCompetence', target: 'AllCards', categoryId: null, categoryName: null, competence: '2026-11', revision: 2, action: 'Set', amount: '5000.00', note: null, at: '2026-11-05T10:00:00Z' }]))
    if (url.pathname.endsWith('/effective/reference')) return Promise.resolve(json(state.preview ?? { source: '2026-11', from: '2026-12', items: [], warnings: [] }))
    return Promise.resolve(json(state.budget ?? budget()))
  }))
  return calls
}
const draw = (onChanged = vi.fn()) => { render(<RecurringBudgetPanel workspaceId="w" competence="2026-12" categories={categories} onChanged={onChanged} />); return onChanged }
const row = async (label: string) => (await screen.findByText(label)).closest('li') as HTMLElement

describe('Orçamento padrão (T12.1)', () => {
  it('mostra o valor efetivo de cada meta e a origem, sem abrir formulário: herdado, personalizado, registro anterior e sem meta', async () => {
    stub({ budget: budget({ targets: [...budget().targets, target({ target: 'Category', id: 'cat-l', categoryId: 'cat-l', name: 'Lazer' })] }) })
    draw()
    expect(text(await row('Total de despesas PF'))).toMatch(/R\$ 12\.500,00\s*Herdado do padrão desde 11\/2026/)
    expect(text(await row('Todos os cartões'))).toMatch(/R\$ 5\.400,00\s*Personalizado neste mês/)
    expect(text(await row('Despesas fixas'))).toMatch(/R\$ 3\.000,00\s*Registro mensal anterior/)
    expect(text(await row('Mercado'))).toMatch(/R\$ 800,00\s*Herdado do padrão desde 11\/2026/)
    expect(text(await row('Lazer'))).toMatch(/Sem meta configurada/)
    expect(text(await row('Fora dos cartões'))).toMatch(/R\$ 7\.500,00\s*Calculado/)
    expect(text(await row('Despesas fixas'))).toContain('não é o mesmo que "fora dos cartões"')
  })

  it('mostra o consumido e o disponível da meta, e só lê dados ao carregar', async () => {
    const calls = stub()
    draw()
    expect(text(await row('Todos os cartões'))).toContain('Consumido R$ 1.000,00 · Disponível R$ 4.400,00')
    expect(calls.every(call => call.method === 'GET')).toBe(true)
  })

  it('sem nenhuma configuração, diz que não há orçamento padrão e que o total é necessário para os Tetos', async () => {
    stub({ budget: budget({ standardConfigured: false, outsideCardsTarget: null, warnings: ['BudgetNotConfigured', 'TotalExpensesRequiredForCeilings'],
      targets: [target({ target: 'TotalExpenses' }), target({ target: 'AllCards' }), target({ target: 'FixedExpenses' })] }) })
    draw()
    expect(await screen.findByText(/Nenhum orçamento padrão configurado/)).not.toBeNull()
    expect(screen.getByText(/Defina o total de despesas para o cenário Tetos operar plenamente/)).not.toBeNull()
    expect(text(await row('Todos os cartões'))).toContain('Sem meta configurada')
  })

  it('editar oferece "somente este mês" e "este mês e os próximos", e envia a escolha com o valor exato', async () => {
    const calls = stub(); const onChanged = draw()
    fireEvent.click(await screen.findByRole('button', { name: 'Editar Total de despesas PF' }))
    const editor = screen.getByRole('group', { name: 'Editar Total de despesas PF' })
    expect((within(editor).getByLabelText(/Somente este mês/) as HTMLInputElement).checked).toBe(true)
    fireEvent.change(within(editor).getByLabelText('Valor da meta'), { target: { value: '13.000,50' } })
    fireEvent.click(within(editor).getByLabelText(/Este mês e os próximos/))
    fireEvent.click(within(editor).getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(onChanged).toHaveBeenCalled())
    const post = calls.find(call => call.method === 'POST')!
    expect(post.url.pathname).toBe('/api/financial-workspaces/w/budgets/effective/changes')
    expect(post.body).toEqual({ competence: '2026-12', target: 'TotalExpenses', categoryId: null, mode: 'ThisAndNext', action: 'Set', amount: '13000.50' })
  })

  it('"somente este mês" cria uma exceção e a mensagem diz que o padrão não mudou', async () => {
    const calls = stub()
    draw()
    fireEvent.click(await screen.findByRole('button', { name: 'Editar Despesas fixas' }))
    fireEvent.change(screen.getByLabelText('Valor da meta'), { target: { value: '3300' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await screen.findByText('Meta personalizada somente neste mês.')
    expect(calls.find(call => call.method === 'POST')!.body).toMatchObject({ mode: 'ThisMonth', action: 'Set', amount: '3300' })
  })

  it('suspender, retirar daqui em diante e restaurar o padrão enviam a ação certa; restaurar só aparece quando há exceção', async () => {
    const calls = stub()
    draw()
    expect(screen.queryByRole('button', { name: 'Restaurar o padrão de Total de despesas PF' })).toBeNull()
    fireEvent.click(await screen.findByRole('button', { name: 'Suspender Mercado neste mês' }))
    await screen.findByText('Mercado suspensa neste mês.')
    fireEvent.click(await screen.findByRole('button', { name: 'Retirar Mercado a partir deste mês' }))
    await screen.findByText('Mercado retirada a partir de 12/2026.')
    fireEvent.click(await screen.findByRole('button', { name: 'Restaurar o padrão de Todos os cartões' }))
    await screen.findByText('Todos os cartões voltou ao padrão.')
    expect(calls.filter(call => call.method === 'POST').map(call => (call.body as { mode: string; action: string }))).toMatchObject([
      { mode: 'ThisMonth', action: 'Remove' }, { mode: 'ThisAndNext', action: 'Remove' }, { mode: 'ThisMonth', action: 'Restore' },
    ])
    expect(screen.queryByRole('button', { name: 'Retirar Todos os cartões a partir deste mês' })).toBeNull() // personalizada: sem retirada do padrão
  })

  it('mostra a recusa do servidor (total menor que os cartões) sem esconder o que foi digitado', async () => {
    stub({ post: () => json({ title: 'Um ou mais erros de validação ocorreram.', status: 400, errors: { budget: ['O orçamento total não pode ser menor que a meta de todos os cartões.'] } }, 400) })
    draw()
    fireEvent.click(await screen.findByRole('button', { name: 'Editar Total de despesas PF' }))
    fireEvent.change(screen.getByLabelText('Valor da meta'), { target: { value: '4000' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await screen.findByText(/não pode ser menor que a meta de todos os cartões/)
    expect((screen.getByLabelText('Valor da meta') as HTMLInputElement).value).toBe('4000')
  })

  it('valor inválido é recusado no navegador, sem chamar a API', async () => {
    const calls = stub()
    draw()
    fireEvent.click(await screen.findByRole('button', { name: 'Editar Despesas fixas' }))
    fireEvent.change(screen.getByLabelText('Valor da meta'), { target: { value: '10,123' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await screen.findByText(/no máximo duas casas decimais/)
    expect(calls.some(call => call.method === 'POST')).toBe(false)
  })

  it('adiciona meta de categoria só de despesas ainda sem meta', async () => {
    const calls = stub()
    draw()
    const select = await screen.findByLabelText('Meta de categoria') as HTMLSelectElement
    expect([...select.options].map(option => option.textContent)).toEqual(['Adicionar meta de categoria…', 'Lazer']) // Mercado já tem meta; Salário é receita
    fireEvent.change(select, { target: { value: 'cat-l' } })
    fireEvent.change(screen.getByLabelText('Valor da meta'), { target: { value: '400' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(calls.some(call => call.method === 'POST')).toBe(true))
    expect(calls.find(call => call.method === 'POST')!.body).toMatchObject({ target: 'Category', categoryId: 'cat-l', mode: 'ThisMonth', amount: '400' })
  })

  it('referência: mostra a prévia com os avisos, aplica só as metas escolhidas a partir da competência e não altera o mês de origem', async () => {
    const calls = stub({ preview: {
      source: '2026-11', from: '2026-12', warnings: ['FixedExpensesNotOutsideCards', 'CategoriesMayBeInsideCards', 'TotalExpensesRequiredForCeilings'],
      items: [{ target: 'AllCards', categoryId: null, name: 'Todos os cartões', amount: '4200.00' }, { target: 'FixedExpenses', categoryId: null, name: 'Despesas fixas', amount: '3000.00' }],
    } })
    draw()
    fireEvent.click(await screen.findByRole('button', { name: 'Usar orçamento existente como referência' }))
    const group = await screen.findByRole('group', { name: 'Usar orçamento existente como referência' })
    await within(group).findByText(/Todos os cartões · R\$ 4\.200,00/)
    expect(text(group)).toContain('A meta de despesas fixas não equivale à meta fora dos cartões.')
    expect(text(group)).toContain('Metas de categorias podem estar contidas na meta dos cartões')
    expect(text(group)).toContain('O total de despesas precisa estar definido para o cenário Tetos operar plenamente.')
    expect(calls.find(call => call.url.pathname.endsWith('/effective/reference'))!.url.searchParams.get('source')).toBe('2026-11')
    fireEvent.click(within(group).getByLabelText(/Despesas fixas · R\$ 3\.000,00/)) // desmarca
    fireEvent.click(within(group).getByRole('button', { name: /Aplicar como padrão a partir de 12\/2026/ }))
    await screen.findByText('Padrão criado a partir de 11/2026, com início em 12/2026.')
    expect(calls.find(call => call.method === 'POST' && call.url.pathname.endsWith('/effective/reference'))!.body).toEqual({ source: '2026-11', from: '2026-12', targets: [{ target: 'AllCards', categoryId: null }] })
  })

  it('o histórico lista as alterações com vigência, ação, valor e revisão', async () => {
    stub()
    draw()
    fireEvent.click(await screen.findByRole('button', { name: 'Histórico de alterações' }))
    expect(text(await screen.findByText(/revisão 2/))).toContain('11/2026 · Todos os cartões · Este mês e os próximos · Definida R$ 5.000,00 · revisão 2')
  })
})
