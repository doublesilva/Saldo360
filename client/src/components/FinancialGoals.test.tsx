import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialGoals } from './FinancialGoals'

describe('FinancialGoals', () => {
  beforeEach(() => vi.restoreAllMocks())
  it('exibe resumo, objetivo sem aporte e disponibilidade retornados pela API', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input)
      if (url.includes('/summary')) return new Response(JSON.stringify({ allocatedToGoals: '0.00', realizedBalance: '5000.00', unallocatedRealizedBalance: '5000.00', activeGoals: 1, completedGoals: 0, pausedGoals: 0, coverageComplete: true, accounts: [] }), { status: 200 })
      if (url.includes('/account-availability')) return new Response(JSON.stringify([]), { status: 200 })
      if (url.includes('/accounts')) return new Response(JSON.stringify([]), { status: 200 })
      return new Response(JSON.stringify([{ id: 'goal-1', familyId: 'family-1', name: 'Reserva', description: null, goalType: 'EmergencyFund', targetAmount: '1000.00', targetDate: null, priority: 'High', status: 'Active', currency: 'BRL', createdAt: '', updatedAt: '', completedAt: null, archivedAt: null }]), { status: 200 })
    })
    render(<FinancialGoals familyId="family-1" />)
    expect(await screen.findByText('Reserva')).toBeTruthy()
    expect(screen.getByText(/Saldo realizado/)).toBeTruthy()
    expect(screen.getByText(/Saldo realizado: R\$/)).toBeTruthy()
  })
})
