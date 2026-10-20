import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialDebtReconciliation } from './FinancialDebtReconciliation'
import type { DebtInstallment, FinancialDebt } from '../api/debts'

const api = vi.hoisted(() => ({ listReconciliations: vi.fn(), createReconciliation: vi.fn(), removeReconciliation: vi.fn() }))
const plannedApi = vi.hoisted(() => ({ listPlanned: vi.fn() }))
vi.mock('../api/debts', () => api)
vi.mock('../api/planned', () => plannedApi)

const debt: FinancialDebt = { id: 'debt-1', workspaceId: 'family-1', name: 'Financiamento', description: null, debtType: 'Loan', lenderName: null, responsibleAccountId: 'account-1', originalPrincipal: '1200.00', outstandingPrincipal: '800.00', outstandingBalance: null, balanceReferenceDate: null, contractDate: '2026-01-01', firstDueDate: '2026-03-10', installmentCount: 3, paidInstallmentCount: 0, installmentAmount: '400.00', interestRate: null, interestRatePeriod: null, amortizationMethod: null, status: 'Active', currency: 'BRL', createdAt: '', updatedAt: '', archivedAt: null }
const installment: DebtInstallment = { id: 'installment-1', workspaceId: 'family-1', debtId: debt.id, installmentNumber: 1, dueDate: '2026-03-10', scheduledAmount: '400.00', categoryId: 'category-1', principalComponent: null, interestComponent: null, insuranceComponent: null, otherChargesComponent: null, paidAmount: '0.00', remainingAmount: '400.00', status: 'Pending', createdAt: '' }
const planned = { id: 'planned-1', type: 'Expense', amount: '399.90', dueDate: '2026-03-12', description: 'Parcela prevista', account: { id: 'account-1', name: 'Conta', isActive: true }, category: { id: 'category-1', name: 'Moradia', isActive: true }, status: 'Pending', realization: null, isCancelled: false, currency: 'BRL', createdAt: '', updatedAt: null, cancelledAt: null }

describe('FinancialDebtReconciliation', () => {
  afterEach(() => cleanup())
  beforeEach(() => { vi.clearAllMocks(); plannedApi.listPlanned.mockResolvedValue({ items: [planned], totalCount: 1 }); api.listReconciliations.mockResolvedValue([]); api.createReconciliation.mockResolvedValue({ id: 'link-1', workspaceId: 'family-1', debtInstallmentId: installment.id, plannedTransactionId: planned.id, createdAt: '', removedAt: null }); api.removeReconciliation.mockResolvedValue(undefined) })
  it('seleciona previsão, exibe divergência e confirma explicitamente', async () => {
    const changed = vi.fn(); render(<FinancialDebtReconciliation workspaceId="family-1" debt={debt} installment={installment} onChanged={changed} />)
    fireEvent.change(await screen.findByLabelText('Previsão de despesa'), { target: { value: planned.id } })
    expect(screen.getByRole('alert').textContent).toContain('Valor divergente')
    fireEvent.click(screen.getByRole('button', { name: 'Revisar conciliação' }))
    expect(screen.getByText(/Nenhum pagamento ou lançamento bancário/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar conciliação' }))
    await waitFor(() => expect(api.createReconciliation).toHaveBeenCalledWith('family-1', { debtInstallmentId: installment.id, plannedTransactionId: planned.id }))
    expect(changed).toHaveBeenCalled()
  })
  it('mostra vínculo ativo e permite desfazer sem alterar a previsão', async () => {
    api.listReconciliations.mockResolvedValue([{ id: 'link-1', workspaceId: 'family-1', debtInstallmentId: installment.id, plannedTransactionId: planned.id, createdAt: '2026-03-01T00:00:00Z', removedAt: null }])
    vi.stubGlobal('confirm', vi.fn(() => true)); render(<FinancialDebtReconciliation workspaceId="family-1" debt={debt} installment={installment} />)
    expect(await screen.findByText(/Previsão vinculada/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Desfazer conciliação' }))
    await waitFor(() => expect(api.removeReconciliation).toHaveBeenCalledWith('family-1', 'link-1'))
  })
  it('permite carregar uma previsão além da primeira página', async () => {
    const distant = { ...planned, id: 'planned-101', description: 'Previsão distante' }
    plannedApi.listPlanned.mockImplementation((_family: string, _filters: unknown, page: number) => Promise.resolve({ items: page === 1 ? [planned] : [distant], page, pageSize: 100, totalCount: 101 }))
    render(<FinancialDebtReconciliation workspaceId="family-1" debt={debt} installment={installment} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Carregar mais previsões' }))
    await waitFor(() => expect(plannedApi.listPlanned).toHaveBeenCalledWith('family-1', expect.anything(), 2, expect.anything(), 100))
    expect(screen.getByRole('option', { name: /Previsão distante/ })).toBeTruthy()
  })
})
