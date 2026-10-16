import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FinancialRecurrences } from './FinancialRecurrences'

afterEach(() => { cleanup(); vi.restoreAllMocks() })
const recurrence = { id: 'r1', familyId: 'f1', accountId: 'a1', categoryId: 'c1', type: 'Expense', description: 'Internet', amount: '380.00', isEstimated: true, frequency: 'Monthly', interval: 1, startDate: '2026-11-10', endDate: null, occurrenceCount: null, dayOfMonth: 10, status: 'Active', currency: 'BRL', createdAt: '', updatedAt: '', archivedAt: null }
const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))

function mockApi(items: unknown[] = []) {
  vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
    if (url === '/auth/csrf') return json({ token: 'csrf' })
    if (url.includes('/recurrences?')) return json(items)
    if (url.endsWith('/accounts?includeInactive=true')) return json([{ id: 'a1', name: 'Conta', isActive: true }])
    if (url.endsWith('/categories?includeInactive=true')) return json([{ id: 'c1', name: 'Internet', type: 'Expense', isActive: true }])
    if (url.includes('/occurrences?')) return json([])
    if (url.includes('/materializations?')) return json([])
    if (url.endsWith('/recurrences') && init?.method === 'POST') return json(recurrence, 201)
    return json([])
  }))
}

describe('FinancialRecurrences', () => {
  it('exibe estado vazio e abre o cadastro', async () => {
    mockApi()
    render(<FinancialRecurrences familyId="f1" />)
    await screen.findByText('Nenhuma recorrência cadastrada.')
    fireEvent.click(screen.getByRole('button', { name: 'Cadastrar recorrência' }))
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Nova recorrência' })).toBeTruthy()
  })

  it('lista recorrência e preserva o valor decimal como texto', async () => {
    mockApi([recurrence])
    render(<FinancialRecurrences familyId="f1" />)
    const recurrenceButton = await screen.findByRole('button', { name: /Internet/ })
    expect(screen.getAllByText(/R\$\s*380,00/).length).toBeGreaterThan(0)
    fireEvent.click(recurrenceButton)
    await waitFor(() => expect(screen.getByText(/ocorrência\(s\) calculada/)).toBeTruthy())
  })
})
