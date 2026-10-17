import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FinancialCreditCardImports } from './FinancialCreditCardImports'

afterEach(() => { cleanup(); vi.restoreAllMocks() })

const card = { id: 'card-1', name: 'Platinum', issuer: 'Banco', holderUserId: null, creditLimit: '10000.00', closingDay: 10, dueDay: 20, defaultPaymentAccountId: null, isArchived: false, createdAt: '', updatedAt: '', archivedAt: null }
const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const csvPreview = { totalRows: 1, validRows: 1, warningRows: 0, invalidRows: 0, ignoredRows: 0, mapping: { dateColumn: 'date', descriptionColumn: 'description', amountColumn: 'amount', delimiter: ',', dateFormat: 'yyyy-MM-dd', decimalFormat: 'Invariant' }, rows: [{ rowNumber: 2, transactionDate: '2026-10-01', description: 'Compra', amount: '10.25', installmentNumber: null, installmentCount: null, externalId: null, category: null, competence: null, status: 'Valid', errors: [], warnings: [] }] }

function mockApi() {
  vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
    if (url === '/auth/csrf') return json({ token: 'csrf' })
    if (url.endsWith('/installments/') && !init?.method) return json([])
    if (url.endsWith('/categories?includeInactive=true')) return json([{ id: 'cat-1', name: 'Compras', type: 'Expense', isActive: true, createdAt: '' }])
    if (url.endsWith('/preview') && init?.method === 'POST') return json(csvPreview)
    if (url.endsWith('/confirm') && init?.method === 'POST') return json({ importId: 'import-1', status: 'Completed', importedCount: 1, items: [{ rowNumber: 2, status: 'Imported', purchaseId: 'purchase-1', errors: [] }] })
    return json([])
  }))
}

describe('FinancialCreditCardImports', () => {
  it('pré-visualiza CSV, exige categoria e confirma somente a linha selecionada', async () => {
    mockApi()
    render(<FinancialCreditCardImports familyId="family-1" cards={[card]} selectedCardId="card-1" />)
    const file = new File(['date,description,amount\n2026-10-01,Compra,10.25'], 'extrato.csv', { type: 'text/csv' })
    fireEvent.change(screen.getByLabelText('Arquivo'), { target: { files: [file] } })
    fireEvent.click(screen.getByRole('button', { name: 'Executar prévia' }))
    await screen.findAllByText(/Compra/)
    expect(screen.getByText(/1 linhas/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar elegíveis' }))
    fireEvent.change(screen.getByLabelText('Categoria da linha 2'), { target: { value: 'cat-1' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar 1' }))
    await waitFor(() => expect(screen.getByText(/1 compra\(s\) confirmada/)).toBeTruthy())
  })

  it('analisa PDF e não oferece importação definitiva', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (url === '/auth/csrf') return json({ token: 'csrf' })
      if (url.endsWith('/installments/') && !init?.method) return json([])
      if (url.endsWith('/categories?includeInactive=true')) return json([])
      if (url.endsWith('/pdf/preview')) return json({ fileName: 'fatura.pdf', pageCount: 1, parser: 'ItauMastercard', statement: { issuer: 'Itaú', cardLastDigits: '1234', competence: '2026-10', closingDate: '2026-10-08', dueDate: '2026-10-15', totalAmount: '10.00' }, summary: { recognized: 1, warnings: 0, invalid: 0, unknown: 0 }, reconciliation: { status: 'Reconciled', officialTotal: '10.00', componentsTotal: '10.00', difference: '0.00', hasUnknownCurrentItems: false }, items: [{ rowNumber: 1, page: 1, sourceText: '01/10 COMPRA 10,00', transactionDate: '2026-10-01', description: 'COMPRA', originalDescription: 'COMPRA', normalizedDescription: 'COMPRA', amount: '10.00', type: 'Purchase', nature: 'Debit', includedInCurrentTotal: true, installmentNumber: null, installmentCount: null, competence: '2026-10', status: 'Valid', errors: [], warnings: [] }], warnings: [] })
      return json([])
    }))
    render(<FinancialCreditCardImports familyId="family-1" cards={[card]} selectedCardId="card-1" />)
    fireEvent.change(screen.getByLabelText('Formato'), { target: { value: 'pdf' } })
    fireEvent.change(screen.getByLabelText('Arquivo'), { target: { files: [new File(['%PDF'], 'fatura.pdf', { type: 'application/pdf' })] } })
    fireEvent.click(screen.getByRole('button', { name: 'Executar prévia' }))
    await screen.findByText('Prévia PDF — somente análise')
    expect(screen.getByText(/Conferência: Reconciled/)).toBeTruthy()
    expect(screen.getByText(/Purchase · Debit/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: /Confirmar/ })).toBeNull()
    expect(screen.getByText(/Nenhuma informação financeira foi gravada/)).toBeTruthy()
  })

  it('mantém o mesmo importId após perda da resposta de confirmação', async () => {
    const bodies: unknown[] = []
    let confirmationAttempt = 0
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (url === '/auth/csrf') return json({ token: 'csrf' })
      if (url.endsWith('/installments/') && !init?.method) return json([])
      if (url.endsWith('/categories?includeInactive=true')) return json([{ id: 'cat-1', name: 'Compras', type: 'Expense', isActive: true, createdAt: '' }])
      if (url.endsWith('/preview')) return json(csvPreview)
      if (url.endsWith('/confirm')) { bodies.push(JSON.parse(String(init?.body))); confirmationAttempt += 1; if (confirmationAttempt === 1) return Promise.reject(new Error('timeout')); return json({ importId: 'same', status: 'Completed', importedCount: 1, items: [] }) }
      return json([])
    }))
    render(<FinancialCreditCardImports familyId="family-1" cards={[card]} selectedCardId="card-1" />)
    fireEvent.change(screen.getByLabelText('Arquivo'), { target: { files: [new File(['csv'], 'extrato.csv', { type: 'text/csv' })] } })
    fireEvent.click(screen.getByRole('button', { name: 'Executar prévia' }))
    await screen.findByRole('button', { name: 'Selecionar elegíveis' })
    fireEvent.click(screen.getByRole('button', { name: 'Selecionar elegíveis' }))
    fireEvent.change(screen.getByLabelText('Categoria da linha 2'), { target: { value: 'cat-1' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar 1' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Confirmar 1' })).toBeTruthy())
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar 1' }))
    await waitFor(() => expect(screen.getByText(/compra\(s\) confirmada/)).toBeTruthy())
    expect(bodies).toHaveLength(2)
    expect((bodies[0] as { importId: string }).importId).toBe((bodies[1] as { importId: string }).importId)
  })
})
