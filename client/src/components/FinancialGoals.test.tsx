import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError } from '../api/apiErrors'
import { FinancialGoals } from './FinancialGoals'

const api = vi.hoisted(() => ({
  listGoals: vi.fn(), getGoalSummary: vi.fn(), getGoalAvailability: vi.fn(), getProgress: vi.fn(), listAllocations: vi.fn(),
  createContribution: vi.fn(), createWithdrawal: vi.fn(), createGoal: vi.fn(), archiveGoal: vi.fn(), cancelAllocation: vi.fn(), changeGoal: vi.fn(),
}))
const accountApi = vi.hoisted(() => ({ listAccounts: vi.fn() }))
vi.mock('../api/goals', () => api)
vi.mock('../api/accounts', () => accountApi)

const goal = { id:'goal-1', workspaceId:'family-1', name:'Reserva', description:null, goalType:'EmergencyFund', targetAmount:'1000.00', targetDate:null, priority:'High', status:'Active', currency:'BRL', createdAt:'', updatedAt:'', completedAt:null, archivedAt:null }

describe('FinancialGoals', () => {
  afterEach(cleanup)
  beforeEach(() => {
    vi.clearAllMocks()
    api.listGoals.mockResolvedValue([goal])
    api.getGoalSummary.mockResolvedValue({ allocatedToGoals:'0.00', realizedBalance:'5000.00', unallocatedRealizedBalance:'5000.00', activeGoals:1, completedGoals:0, pausedGoals:0, coverageComplete:true, accounts:[] })
    api.getGoalAvailability.mockResolvedValue([])
    accountApi.listAccounts.mockResolvedValue([{ id:'account-1', name:'Conta', isActive:true }])
    api.getProgress.mockResolvedValue({ goalId:'goal-1', targetAmount:'1000.00', allocatedAmount:'0.00', remainingToTarget:'1000.00', progressPercentage:'0.00', isFunded:false })
    api.listAllocations.mockResolvedValue([])
    api.createContribution.mockResolvedValue({ id:'allocation-1' })
  })

  it('exibe resumo, objetivo sem aporte e disponibilidade retornados pela API', async () => {
    render(<FinancialGoals workspaceId="family-1" />)
    expect(await screen.findByText('Reserva')).toBeTruthy()
    expect(screen.getByText(/Saldo realizado: R\$/)).toBeTruthy()
  })

  it('exibe conflito sanitizado com traceId', async () => {
    api.createContribution.mockRejectedValue(new ApiRequestError('Saldo não alocado insuficiente.', 'conflict', { status:409, code:'INSUFFICIENT_FUNDS', traceId:'trace-goal' }))
    render(<FinancialGoals workspaceId="family-1" />)
    fireEvent.click(await screen.findByText('Reserva'))
    fireEvent.change(await screen.findByLabelText('Valor'), { target: { value:'6000.00' } })
    fireEvent.click(screen.getByRole('button', { name:'Registrar' }))
    expect((await screen.findByRole('alert')).textContent).toContain('Saldo não alocado insuficiente. Referência: trace-goal.')
  })

  it('preserva operationId após timeout e bloqueia reenvio simultâneo', async () => {
    let rejectFirst!: (error: unknown) => void
    api.createContribution.mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectFirst = reject })).mockResolvedValueOnce({ id:'allocation-1' })
    render(<FinancialGoals workspaceId="family-1" />)
    fireEvent.click(await screen.findByText('Reserva'))
    fireEvent.change(await screen.findByLabelText('Valor'), { target: { value:'100.00' } })
    const submit = screen.getByRole('button', { name:'Registrar' })
    fireEvent.click(submit)
    await waitFor(() => expect(api.createContribution).toHaveBeenCalledTimes(1))
    fireEvent.click(submit)
    expect(api.createContribution).toHaveBeenCalledTimes(1)
    await act(async () => rejectFirst(new ApiRequestError('Resultado incerto.', 'timeout', undefined, true)))
    await screen.findByRole('alert')
    fireEvent.click(submit)
    await waitFor(() => expect(api.createContribution).toHaveBeenCalledTimes(2))
    expect(api.createContribution.mock.calls[0][2].operationId).toBe(api.createContribution.mock.calls[1][2].operationId)
  })

  it('consulta alocações após resposta perdida e confirma o aporte sem novo POST', async () => {
    let operationId = ''
    api.createContribution.mockImplementation((_family: string, _goal: string, input: { operationId: string }) => {
      operationId = input.operationId
      return Promise.reject(new ApiRequestError('Resultado incerto.', 'timeout', undefined, true))
    })
    api.listAllocations.mockResolvedValueOnce([]).mockImplementation(() => Promise.resolve(operationId ? [{ id:'allocation-1', workspaceId:'family-1', goalId:'goal-1', financialAccountId:'account-1', operationType:'Contribution', amount:'100.00', effectiveDate:'2026-10-16', operationId, description:null, createdAt:'', cancelledAt:null }] : []))
    render(<FinancialGoals workspaceId="family-1" />)
    fireEvent.click(await screen.findByText('Reserva'))
    fireEvent.change(await screen.findByLabelText('Valor'), { target: { value:'100.00' } })
    fireEvent.click(screen.getByRole('button', { name:'Registrar' }))
    await waitFor(() => expect(api.listAllocations.mock.calls.length).toBeGreaterThanOrEqual(2))
    expect(api.createContribution).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('alert')).toBeNull()
  })
})
