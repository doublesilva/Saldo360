import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FinancialInvoiceReconciliations } from './FinancialInvoiceReconciliations'

afterEach(() => { cleanup(); vi.restoreAllMocks() })

const card = { id: 'card-1', name: 'Platinum', issuer: 'Banco', holderUserId: null, creditLimit: '10000.00', closingDay: 10, dueDay: 20, defaultPaymentAccountId: 'account-1', isArchived: false, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', archivedAt: null }
const account = { id: 'account-1', name: 'Conta principal', institution: 'Banco', type: 'Checking', financialScope: 'PF', responsible: null, openingBalance: '5000.00', openingBalanceDate: '2026-01-01', isActive: true, currency: 'BRL', createdAt: '2026-01-01T00:00:00Z' }
const planned = { id: 'planned-1', type: 'Expense', amount: '1000.00', dueDate: '2026-10-20', description: 'Fatura prevista', account: { id: 'account-1', name: 'Conta principal', isActive: true }, category: { id: 'category-1', name: 'Compras', isActive: true }, status: 'Pending', realization: null, isCancelled: false, currency: 'BRL', createdAt: '2026-01-01T00:00:00Z', updatedAt: null, cancelledAt: null }
const invoice = { id: 'invoice-1', creditCardId: 'card-1', competence: '2026-10', cycleStartDate: '2026-09-11', closingDate: '2026-10-10', dueDate: '2026-10-20', status: 'Closed', totalAmount: '1050.00', paidAmount: '0.00', remainingAmount: '1050.00', paymentStatus: 'Unpaid', installments: [], installmentCount: 0 }
const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))

function mockApi(link: unknown[] = []) {
  vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
    if (init?.method === 'POST') return json({ id: 'link-2', familyId: 'family-1', plannedTransactionId: 'planned-1', creditCardInvoiceId: 'invoice-1', createdAt: '2026-10-01T00:00:00Z', removedAt: null }, 201)
    if (init?.method === 'DELETE') return Promise.resolve(new Response(null, { status: 204 }))
    if (url.includes('/invoice-reconciliations/')) return json(link)
    if (url.includes('/planned-transactions?')) return json({ items: [planned], page: 1, pageSize: 100, totalCount: 1 })
    if (url.includes('/credit-cards?')) return json([card])
    if (url.includes('/accounts?')) return json([account])
    if (url.includes('/invoices?')) return json([invoice])
    return json([])
  }))
}

describe('FinancialInvoiceReconciliations', () => {
  it('apresenta estado vazio e mantém a navegação acessível', async () => {
    mockApi([])
    render(<FinancialInvoiceReconciliations familyId="family-1" />)
    expect(await screen.findByText('Nenhuma conciliação ativa.')).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Nova conciliação' })).toBeTruthy()
  })

  it('revisa valores divergentes e cria o vínculo sem converter valores', async () => {
    const changed = vi.fn()
    mockApi([])
    render(<FinancialInvoiceReconciliations familyId="family-1" onChanged={changed} />)
    await screen.findByText('Nenhuma conciliação ativa.')
    fireEvent.change(screen.getByLabelText('Previsão de despesa'), { target: { value: 'planned-1' } })
    fireEvent.change(screen.getByLabelText('Fatura com saldo devido'), { target: { value: 'invoice-1' } })
    fireEvent.click(screen.getByRole('button', { name: 'Revisar conciliação' }))
    expect(screen.getByText(/Previsão:/)).toBeTruthy()
    expect(screen.getByText(/Fatura:/)).toBeTruthy()
    expect(screen.getByText(/nenhum valor é ajustado automaticamente/i)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar conciliação' }))
    await waitFor(() => expect(changed).toHaveBeenCalled())
  })

  it('lista vínculo ativo e exige confirmação para desfazer', async () => {
    const changed = vi.fn()
    vi.stubGlobal('confirm', vi.fn(() => true))
    mockApi([{ id: 'link-1', familyId: 'family-1', plannedTransactionId: 'planned-1', creditCardInvoiceId: 'invoice-1', createdAt: '2026-10-01T00:00:00Z', removedAt: null }])
    render(<FinancialInvoiceReconciliations familyId="family-1" onChanged={changed} />)
    expect(await screen.findByText('Fatura prevista')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Desfazer vínculo' }))
    await waitFor(() => expect(changed).toHaveBeenCalled())
    expect(window.confirm).toHaveBeenCalled()
  })
})
