import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialDebts } from './FinancialDebts'
import type { DebtInstallment, FinancialDebt } from '../api/debts'
import { ApiRequestError } from '../api/apiErrors'

const api = vi.hoisted(() => ({
  listDebts: vi.fn(),
  listInstallments: vi.fn(),
  listPayments: vi.fn(),
  createDebt: vi.fn(),
  updateDebt: vi.fn(),
  createPayment: vi.fn(),
  cancelPayment: vi.fn(),
  createInstallment: vi.fn(),
  changeDebt: vi.fn(),
  archiveDebt: vi.fn(),
}))
vi.mock('../api/debts', () => api)
vi.mock('./FinancialDebtReconciliation', () => ({ FinancialDebtReconciliation: () => null }))

const debt: FinancialDebt = {
  id: 'debt-1', workspaceId: 'family-1', name: 'Financiamento', description: null, debtType: 'Loan', lenderName: 'Banco',
  responsibleAccountId: 'account-1', originalPrincipal: '1200.00', outstandingPrincipal: '800.00', outstandingBalance: null,
  balanceReferenceDate: null, contractDate: '2026-01-01', firstDueDate: '2026-02-10', installmentCount: 3,
  paidInstallmentCount: 1, installmentAmount: '400.00', interestRate: null, interestRatePeriod: null, amortizationMethod: null,
  status: 'Active', currency: 'BRL', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', archivedAt: null,
}
const installment: DebtInstallment = {
  id: 'installment-1', workspaceId: 'family-1', debtId: debt.id, installmentNumber: 2, dueDate: '2026-03-10', scheduledAmount: '400.00',
  categoryId: null, principalComponent: '390.00', interestComponent: '10.00', insuranceComponent: null, otherChargesComponent: null,
  paidAmount: '0.00', remainingAmount: '400.00', status: 'Pending', createdAt: '2026-01-01T00:00:00Z',
}

describe('FinancialDebts', () => {
  afterEach(() => cleanup())
  beforeEach(() => {
    vi.clearAllMocks()
    api.listDebts.mockResolvedValue([debt])
    api.listInstallments.mockResolvedValue([installment])
    api.listPayments.mockResolvedValue([])
    api.createDebt.mockResolvedValue(debt)
    api.updateDebt.mockResolvedValue(debt)
    api.createPayment.mockResolvedValue({ id: 'payment-1' })
    api.cancelPayment.mockResolvedValue(undefined)
    api.changeDebt.mockResolvedValue(debt)
    api.archiveDebt.mockResolvedValue(undefined)
  })

  it('cadastra uma dívida preservando campos opcionais nulos', async () => {
    api.listDebts.mockResolvedValueOnce([])
    render(<FinancialDebts workspaceId="family-1" />)
    fireEvent.click(screen.getByRole('button', { name: 'Nova dívida' }))
    fireEvent.change(screen.getByLabelText('Nome'), { target: { value: 'Curso' } })
    fireEvent.change(screen.getByLabelText('Principal original'), { target: { value: '1234.56' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(api.createDebt).toHaveBeenCalledTimes(1))
    expect(api.createDebt.mock.calls[0][1]).toMatchObject({ name: 'Curso', originalPrincipal: '1234.56', responsibleAccountId: null, firstDueDate: null })
  })

  it('permite editar a dívida sem converter valores monetários para number', async () => {
    render(<FinancialDebts workspaceId="family-1" />)
    fireEvent.click(await screen.findByText('Financiamento'))
    fireEvent.click(await screen.findByRole('button', { name: 'Editar' }))
    const amount = screen.getByLabelText('Principal original')
    fireEvent.change(amount, { target: { value: '999.90' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(api.updateDebt).toHaveBeenCalledTimes(1))
    expect(api.updateDebt.mock.calls[0][2].originalPrincipal).toBe('999.90')
  })

  it('preserva a operação após resultado incerto e impede envios simultâneos', async () => {
    let rejectFirst!: (error: unknown) => void
    api.createPayment.mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectFirst = reject })).mockResolvedValueOnce({ id: 'payment-1' })
    render(<FinancialDebts workspaceId="family-1" />)
    fireEvent.click(await screen.findByText('Financiamento'))
    const row = await screen.findByText(/nominal 400\.00/)
    const container = row.closest('li') as HTMLElement
    const pay = within(container).getByRole('button', { name: 'Registrar pagamento' })
    fireEvent.click(pay)
    await waitFor(() => expect(api.createPayment).toHaveBeenCalledTimes(1))
    fireEvent.click(pay)
    expect(api.createPayment).toHaveBeenCalledTimes(1)
    await act(async () => rejectFirst(new ApiRequestError('Resultado incerto.', 'timeout', undefined, true)))
    expect((await screen.findByRole('alert')).textContent).toContain('Consulte os dados atualizados antes de tentar novamente')
    fireEvent.click(pay)
    await waitFor(() => expect(api.createPayment).toHaveBeenCalledTimes(2))
    expect(api.createPayment.mock.calls[0][3].operationId).toBe(api.createPayment.mock.calls[1][3].operationId)
  })

  it('consulta o histórico após resposta perdida e não reenvia o pagamento confirmado', async () => {
    let operationId = ''
    api.createPayment.mockImplementation((_f: string, _d: string, _i: string, input: { operationId: string }) => {
      operationId = input.operationId
      return Promise.reject(new ApiRequestError('Resultado incerto.', 'timeout', undefined, true))
    })
    api.listPayments.mockImplementation(() => Promise.resolve(operationId ? [{ id: 'payment-1', operationId }] : []))
    render(<FinancialDebts workspaceId="family-1" />)
    fireEvent.click(await screen.findByText('Financiamento'))
    const row = await screen.findByText(/nominal 400\.00/)
    fireEvent.click(within(row.closest('li') as HTMLElement).getByRole('button', { name: 'Registrar pagamento' }))
    await waitFor(() => expect(api.listPayments).toHaveBeenCalled())
    expect(api.createPayment).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole('alert')).toBeNull()
  })
})
