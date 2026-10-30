import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { getBudgetExecution, listBudgets } from '../api/budgets'
import type { FinancialBudget, FinancialBudgetExecution } from '../api/budgets'
import { FinancialCalendarBudgetSummary } from './FinancialCalendarBudgetSummary'

vi.mock('../api/budgets', () => ({ listBudgets: vi.fn(), getBudgetExecution: vi.fn() }))

const budget = (targets: FinancialBudget['targets']): FinancialBudget => ({
  id: 'budget-1', competence: '2026-10', description: null, isArchived: false, totalPlannedAmount: '300.00',
  targets, createdAt: '', updatedAt: '', archivedAt: null,
})

const execution = (targets: FinancialBudgetExecution['targets']): FinancialBudgetExecution => ({
  budgetId: 'budget-1', competence: '2026-10',
  categories: {
    planned: '300.00', consumed: '280.00', realized: '280.00', committed: '40.00', projectedConsumption: '320.00', available: '20.00', availableProjected: '-20.00', exceededAmount: '0.00', exceededProjected: '20.00',
    consumptionPercentage: '93.33', projectedPercentage: '106.67', status: 'ApproachingLimit', projectedStatus: 'Exceeded', alertMessage: 'O consumo ultrapassou o limite.', approachingCount: 1, reachedCount: 1, exceededCount: 1, targetCount: 3,
  },
  targets,
})

const target = (id: string, name: string): FinancialBudget['targets'][number] => ({
  id, name, plannedAmount: '100.00', groupingType: 'Category', category: { id: `cat-${id}`, name, isActive: true }, isArchived: false, createdAt: '', updatedAt: '', archivedAt: null,
})

const targetExecution = (targetId: string, categoryId: string, status: FinancialBudgetExecution['targets'][number]['status'], projectedConsumption: string): FinancialBudgetExecution['targets'][number] => ({
  targetId, categoryId, groupingType: 'Category', planned: '100.00', consumed: projectedConsumption, realized: projectedConsumption, creditCardCommitted: '0.00', committed: '0.00', projectedConsumption,
  available: status === 'Exceeded' ? '0.00' : '20.00', availableProjected: status === 'Exceeded' ? '0.00' : '20.00', exceededAmount: status === 'Exceeded' ? '10.00' : '0.00', exceededProjected: status === 'Exceeded' ? '10.00' : '0.00',
  consumptionPercentage: '80.00', projectedPercentage: '80.00', status, projectedStatus: status, coverage: { estimatedCompetenceCount: 0, undefinedNatureCount: 0, undefinedNatureAmount: '0.00', noCards: false },
  alertMessage: status === 'Exceeded' ? 'O consumo ultrapassou o limite.' : 'O consumo está próximo do limite.',
})

afterEach(() => { cleanup(); vi.clearAllMocks() })

describe('FinancialCalendarBudgetSummary', () => {
  it('apresenta totais da API e ordena categorias críticas por severidade', async () => {
    const targets = [target('approaching', 'Academia'), target('exceeded', 'Aluguel'), target('reached', 'Mercado')]
    vi.mocked(listBudgets).mockResolvedValue([budget(targets)])
    vi.mocked(getBudgetExecution).mockResolvedValue(execution([
      targetExecution('approaching', 'cat-approaching', 'ApproachingLimit', '80.00'),
      targetExecution('reached', 'cat-reached', 'LimitReached', '100.00'),
      targetExecution('exceeded', 'cat-exceeded', 'Exceeded', '110.00'),
    ]))
    render(<FinancialCalendarBudgetSummary workspaceId="family-1" competence="2026-10-01" onNavigateToBudgets={vi.fn()} />)
    expect((await screen.findAllByText('Orçamento excedido')).length).toBeGreaterThanOrEqual(2)
    expect(screen.getByText(/300,00/)).toBeTruthy()
    const names = screen.getAllByRole('listitem').map((item) => item.textContent)
    expect(names[0]).toContain('Aluguel')
    expect(names[1]).toContain('Mercado')
    expect(names[2]).toContain('Academia')
    expect(screen.getByText(/independente dos filtros/)).toBeTruthy()
  })

  it('informa ausência de orçamento e permite navegar preservando competência', async () => {
    vi.mocked(listBudgets).mockResolvedValue([])
    const navigate = vi.fn()
    render(<FinancialCalendarBudgetSummary workspaceId="family-1" competence="2026-11-01" onNavigateToBudgets={navigate} />)
    expect(await screen.findByText('Nenhum orçamento definido para esta competência.')).toBeTruthy()
    screen.getByRole('button', { name: 'Ir para Orçamentos' }).click()
    expect(navigate).toHaveBeenCalledWith('2026-11')
  })
})
