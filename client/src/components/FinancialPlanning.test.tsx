import { render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialPlanning } from './FinancialPlanning'

const scenario = { id: 'scenario-1', familyId: 'family-1', recoveryAccountId: 'account-1', name: 'Recuperação', description: null, startMonth: '2026-11', months: 1, status: 'Active' as const, createdAt: '', updatedAt: '', archivedAt: null }
const projection = { scenarioId: 'scenario-1', from: '2026-11', months: 1, items: [{ competence: '2026-11', openingBalance: '-1000.00', realizedIncome: '0.00', realizedExpense: '0.00', pendingIncome: '0.00', pendingExpense: '0.00', creditCardOutstanding: '0.00', baseClosingBalance: '-1000.00', additionalIncome: '0.00', additionalExpense: '0.00', cumulativeAdjustment: '0.00', scenarioClosingBalance: '-1000.00', contingencyReserve: '0.00', plannedAvailable: '-1000.00', projectionComplete: true, usesBaseProjectionOnly: true, hasUnallocatedObligations: false, recoveryTargetPercentage: '85.00', recoveryAccountId: 'account-1', openingNegativeBalance: '1000.00', closingNegativeBalance: '1000.00', projectedRecovery: '0.00', cumulativeRecovery: '0.00', recoveryCapacity: '0.00', recoveryTarget: '0.00', uncommittedMargin: '0.00', remainingNegativeBalance: '1000.00', recoveryStatus: 'Stable' as const, recoveryProjectionComplete: true }] }

describe('FinancialPlanning', () => {
  beforeEach(() => { vi.restoreAllMocks() })

  it('exibe o estado vazio sem cenários', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify([]), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    render(<FinancialPlanning familyId="family-1" />)
    expect(await screen.findByText('Nenhum cenário cadastrado.')).toBeTruthy()
  })

  it('exibe projeção e indicadores de recuperação vindos da API como texto', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input)
      if (url.includes('/accounts')) return new Response(JSON.stringify([{ id: 'account-1', name: 'Conta negativa', isActive: true }]), { status: 200 })
      if (url.endsWith('planning-scenarios?includeArchived=true')) return new Response(JSON.stringify([scenario]), { status: 200 })
      if (url.includes('/parameters')) return new Response(JSON.stringify([]), { status: 200 })
      if (url.includes('/projection')) return new Response(JSON.stringify(projection), { status: 200 })
      return new Response('{}', { status: 200 })
    })
    render(<FinancialPlanning familyId="family-1" />)
    await waitFor(() => expect(screen.getByText('Estável')).toBeTruthy())
    expect(screen.getByText('Estável')).toBeTruthy()
    expect(screen.getByText('Conta negativa')).toBeTruthy()
    expect(screen.getByRole('table', { name: /Projeção-base/ })).toBeTruthy()
  })
})
