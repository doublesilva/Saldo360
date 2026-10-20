import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FinancialCreditCardsPage } from './FinancialCreditCards'

afterEach(() => { cleanup(); vi.restoreAllMocks() })

const card = { id: 'card-1', name: 'Platinum', issuer: 'Banco', lastFourDigits: '0012', holderUserId: null, creditLimit: '10000.00', closingDay: 10, dueDay: 20, defaultPaymentAccountId: null, isArchived: false, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', archivedAt: null }
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } }))

describe('FinancialCreditCardsPage', () => {
  it('lista cartões e preserva valores monetários como texto na exibição', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string) => {
      if (url.includes('/credit-cards?')) return json([card])
      if (url.endsWith('/accounts?includeInactive=true')) return json([])
      if (url.endsWith('/categories?includeInactive=true')) return json([])
      if (url.endsWith('/invoices?from=2026-10&through=2026-10')) return json([])
      if (url.endsWith('/purchases')) return json([])
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: '0.00', availableLimit: '10000.00', overLimitAmount: '0.00' })
      if (url.includes('/invoice-projection?')) return json([])
      return json([])
    }))
    render(<FinancialCreditCardsPage workspaceId="family-1" />)
    expect((await screen.findAllByText('Platinum')).length).toBeGreaterThan(0)
    await waitFor(() => expect(screen.getAllByText(/10\.000,00/).length).toBeGreaterThan(0))
    expect(screen.getAllByText(/•••• 0012/).length).toBeGreaterThan(0)
    expect(screen.getAllByText('Cartões').length).toBeGreaterThan(0)
  })

  it('notifica a família após arquivar para invalidar Agenda, Contas e Orçamentos', async () => {
    const changed = vi.fn()
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') return Promise.resolve(new Response(null, { status: 204 }))
      if (url.includes('/credit-cards?')) return json([card])
      if (url.endsWith('/accounts?includeInactive=true')) return json([])
      if (url.endsWith('/categories?includeInactive=true')) return json([])
      if (url.endsWith('/invoices?from=2026-10&through=2026-10')) return json([])
      if (url.endsWith('/purchases')) return json([])
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: '0.00', availableLimit: '10000.00', overLimitAmount: '0.00' })
      if (url.includes('/invoice-projection?')) return json([])
      return json([])
    }))
    vi.stubGlobal('confirm', vi.fn(() => true))
    render(<FinancialCreditCardsPage workspaceId="family-1" onChanged={changed} />)
    await screen.findAllByText('Platinum')
    fireEvent.click(screen.getByRole('button', { name: 'Arquivar' }))
    await waitFor(() => expect(changed).toHaveBeenCalled())
  })
})
