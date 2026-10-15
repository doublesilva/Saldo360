import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialBudget, FinancialBudgetExecution } from '../api/budgets'
import type { FinancialCategory } from '../api/categories'
import { FinancialBudgetsPage } from './FinancialBudgets'

const expense: FinancialCategory = { id: 'cat-expense', name: 'Moradia', type: 'Expense', isActive: true, createdAt: '' }
const inactive: FinancialCategory = { id: 'cat-inactive', name: 'Academia', type: 'Expense', isActive: false, createdAt: '' }
const income: FinancialCategory = { id: 'cat-income', name: 'Salário', type: 'Income', isActive: true, createdAt: '' }
const target = {
  id: 'target-1', name: 'Moradia', plannedAmount: '1000.00', groupingType: 'Category' as const,
  category: { id: expense.id, name: expense.name, isActive: true }, isArchived: false,
  createdAt: '', updatedAt: '', archivedAt: null,
}
const budget: FinancialBudget = {
  id: 'budget-1', competence: '2026-10', description: 'Outubro', isArchived: false,
  totalPlannedAmount: '1000.00', targets: [target], createdAt: '', updatedAt: '', archivedAt: null,
}
const execution: FinancialBudgetExecution = {
  budgetId: budget.id, competence: budget.competence,
  total: { planned: '1000.00', realized: '200.00', committed: '300.00', projectedConsumption: '500.00', available: '500.00', exceededAmount: '0.00', consumptionPercentage: '50.00' },
  targets: [{ targetId: target.id, categoryId: expense.id, planned: '1000.00', realized: '200.00', committed: '300.00', projectedConsumption: '500.00', available: '500.00', exceededAmount: '0.00', consumptionPercentage: '50.00' }],
}

type Call = { url: string; init?: RequestInit }

function mockApi(options: { budgets?: FinancialBudget[]; execution?: FinancialBudgetExecution | null; saveStatus?: number } = {}) {
  const calls: Call[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input); calls.push({ url, init })
    if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
    if (url.includes('/categories')) return Response.json([expense, inactive, income])
    if (init?.method === 'POST' || init?.method === 'PUT') {
      if (options.saveStatus) return Response.json({ title: 'Já existe um orçamento para esta competência.' }, { status: options.saveStatus })
      return Response.json(budget, { status: init.method === 'POST' ? 201 : 200 })
    }
    if (init?.method === 'DELETE') return new Response(null, { status: 204 })
    if (url.includes('/execution')) return Response.json(options.execution ?? execution)
    return Response.json(options.budgets ?? [budget])
  }))
  return calls
}

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers() })

describe('FinancialBudgetsPage', () => {
  it('seleciona o mês de São Paulo, mostra resumo e navega entre competências', async () => {
    const calls = mockApi()
    render(<FinancialBudgetsPage familyId="family-1" />)
    expect(await screen.findByText('Outubro')).toBeTruthy()
    expect(screen.getAllByText(/500,00/).length).toBeGreaterThan(0)
    expect(screen.getByText('50.00%')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Próxima competência' }))
    await waitFor(() => expect(calls.some((call) => call.url.includes('competence=2026-11'))).toBe(true))
  })

  it('mostra estado vazio e cria orçamento da competência sem criar metas', async () => {
    const calls = mockApi({ budgets: [] })
    render(<FinancialBudgetsPage familyId="family-1" />)
    expect(await screen.findByText('Nenhum orçamento definido para esta competência.')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Criar orçamento' }))
    fireEvent.change(screen.getByLabelText('Descrição (opcional)'), { target: { value: 'Novo mês' } })
    fireEvent.click(screen.getByRole('button', { name: 'Criar orçamento' }))
    await waitFor(() => expect(calls.some((call) => call.init?.method === 'POST')).toBe(true))
    const request = calls.find((call) => call.init?.method === 'POST' && call.url.endsWith('/budgets'))!
    expect(JSON.parse(String(request.init!.body))).toEqual({ competence: '2026-10', description: 'Novo mês' })
  })

  it('exibe inativa, oferece somente despesas elegíveis e envia centavos exatos ao adicionar meta', async () => {
    const calls = mockApi()
    render(<FinancialBudgetsPage familyId="family-1" />)
    await screen.findByText('Moradia')
    expect(await screen.findByRole('option', { name: /Academia.*inativa/ })).toBeTruthy()
    fireEvent.change(screen.getByLabelText('Categoria de despesa'), { target: { value: inactive.id } })
    fireEvent.change(screen.getByLabelText('Valor planejado'), { target: { value: '1234,56' } })
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar meta' }))
    await waitFor(() => expect(calls.some((call) => call.url.endsWith('/targets') && call.init?.method === 'POST')).toBe(true))
    const request = calls.find((call) => call.url.endsWith('/targets') && call.init?.method === 'POST')!
    expect(JSON.parse(String(request.init!.body))).toMatchObject({ categoryId: inactive.id, plannedAmount: '1234.56', groupingType: 'Category' })
    expect(screen.queryByRole('option', { name: 'Salário' })).toBeNull()
  })

  it('edita a meta e arquiva com confirmação explicando o impacto', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<FinancialBudgetsPage familyId="family-1" />)
    const card = (await screen.findByText('Moradia')).closest('article')!
    fireEvent.click(within(card).getByRole('button', { name: 'Editar' }))
    fireEvent.change(within(card).getByLabelText('Valor planejado'), { target: { value: '900,00' } })
    fireEvent.click(within(card).getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(calls.some((call) => call.init?.method === 'PUT')).toBe(true))
    const update = calls.find((call) => call.init?.method === 'PUT')!
    expect(JSON.parse(String(update.init!.body))).toEqual({ name: 'Moradia', plannedAmount: '900.00' })
    fireEvent.click(await screen.findByRole('button', { name: 'Arquivar meta' }))
    await waitFor(() => expect(calls.some((call) => call.init?.method === 'DELETE')).toBe(true))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('sem cancelar despesas'))
  })

  it('trata percentual nulo e excedente sem recalcular valores', async () => {
    const over = { ...execution, total: { ...execution.total, planned: '0.00', realized: '10.00', committed: '5.00', projectedConsumption: '15.00', available: '-15.00', exceededAmount: '15.00', consumptionPercentage: null }, targets: [{ ...execution.targets[0], planned: '0.00', realized: '10.00', committed: '5.00', projectedConsumption: '15.00', available: '-15.00', exceededAmount: '15.00', consumptionPercentage: null }] }
    mockApi({ execution: over })
    render(<FinancialBudgetsPage familyId="family-1" />)
    expect((await screen.findAllByText('Sem referência')).length).toBeGreaterThan(0)
    expect(screen.getByText('Excedida')).toBeTruthy()
    expect(screen.getAllByText(/15,00/).length).toBeGreaterThan(0)
  })

  it('mostra conflito de criação devolvido pela API', async () => {
    mockApi({ budgets: [], saveStatus: 409 })
    render(<FinancialBudgetsPage familyId="family-1" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Criar orçamento' }))
    fireEvent.click(screen.getByRole('button', { name: 'Criar orçamento' }))
    expect(await screen.findByText(/Já existe um orçamento/)).toBeTruthy()
  })
})
