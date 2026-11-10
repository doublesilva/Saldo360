import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialPlanning } from './FinancialPlanning'

const scenario = { id: 'scenario-1', workspaceId: 'family-1', recoveryAccountId: 'account-1', name: 'Recuperação', description: null, startMonth: '2026-11', months: 1, status: 'Active' as const, createdAt: '', updatedAt: '', archivedAt: null }
const projection = { scenarioId: 'scenario-1', from: '2026-11', months: 1, items: [{ competence: '2026-11', openingBalance: '-1000.00', realizedIncome: '0.00', realizedExpense: '0.00', pendingIncome: '0.00', pendingExpense: '0.00', creditCardOutstanding: '0.00', baseClosingBalance: '-1000.00', additionalIncome: '0.00', additionalExpense: '0.00', cumulativeAdjustment: '0.00', scenarioClosingBalance: '-1000.00', contingencyReserve: '0.00', plannedAvailable: '-1000.00', projectionComplete: true, usesBaseProjectionOnly: true, hasUnallocatedObligations: false, recoveryTargetPercentage: '85.00', recoveryAccountId: 'account-1', openingNegativeBalance: '1000.00', closingNegativeBalance: '1000.00', projectedRecovery: '0.00', cumulativeRecovery: '0.00', recoveryCapacity: '0.00', recoveryTarget: '0.00', uncommittedMargin: '0.00', remainingNegativeBalance: '1000.00', recoveryStatus: 'Stable' as const, recoveryProjectionComplete: true }] }

// T14.1: mês com +R$ 500 atribuídos à conta-alvo: limite utilizado 1.000 → 200 no simulado, 700 na base.
const recoveryMonth = { ...projection.items[0], openingNegativeBalance: '1000.00', closingNegativeBalance: '200.00', projectedRecovery: '800.00', recoveryStatus: 'Recovering' as const, recoveryTarget: '240.00', recoveryDeviation: '560.00', recoveryTargetSource: 'BaseProjection' as const,
  targetAdjustment: '500.00', targetCumulativeAdjustment: '500.00', unattributedAdjustment: '0.00', targetOpeningBalance: '-1000.00', targetClosingBalance: '-200.00', targetBaseClosingBalance: '-700.00',
  targetRealizedIncome: '0.00', targetRealizedExpense: '0.00', targetPendingIncome: '300.00', targetPendingExpense: '0.00', baseClosingNegativeBalance: '700.00', baseProjectedRecovery: '300.00', contractedLimit: '2000.00', officialRecoveryTarget: null }

const plain = (text: string | null) => (text ?? '').replace(/\s/g, ' ')

function mockPlanning(items: unknown[]) {
  vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
    const url = String(input)
    if (url.includes('/accounts')) return new Response(JSON.stringify([{ id: 'account-1', name: 'Conta negativa', isActive: true }]), { status: 200 })
    if (url.endsWith('planning-scenarios?includeArchived=true')) return new Response(JSON.stringify([scenario]), { status: 200 })
    if (url.includes('/parameters')) return new Response(JSON.stringify([]), { status: 200 })
    if (url.includes('/projection')) return new Response(JSON.stringify({ ...projection, items }), { status: 200 })
    return new Response('{}', { status: 200 })
  })
}

describe('FinancialPlanning', () => {
  beforeEach(() => { vi.restoreAllMocks() })
  afterEach(() => cleanup())

  it('exibe o estado vazio sem cenários', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify([]), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    render(<FinancialPlanning workspaceId="family-1" />)
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
    render(<FinancialPlanning workspaceId="family-1" />)
    await waitFor(() => expect(screen.getByText('Estável')).toBeTruthy())
    expect(screen.getByText('Estável')).toBeTruthy()
    expect(screen.getAllByText('Conta negativa').length).toBeGreaterThan(0)
    expect(screen.getByRole('table', { name: /Projeção-base/ })).toBeTruthy()
  })
  it('alterna entre o cenário simulado e a base, mostrando limite utilizado, recuperação, meta e desvio da conta-alvo', async () => {
    mockPlanning([recoveryMonth])
    render(<FinancialPlanning workspaceId="family-1" />)
    const table = await screen.findByRole('table', { name: /Projeção-base/ })
    const row = within(table).getByRole('row', { name: /2026-11/ })
    expect(plain(row.textContent)).toContain('R$ 200,00'); expect(plain(row.textContent)).toContain('R$ 800,00'); expect(plain(row.textContent)).toContain('R$ 240,00'); expect(plain(row.textContent)).toContain('R$ 560,00'); expect(plain(row.textContent)).toContain('Em recuperação')
    fireEvent.click(screen.getByLabelText('Cenário-base'))
    const base = within(table).getByRole('row', { name: /2026-11/ })
    expect(plain(base.textContent)).toContain('R$ 700,00'); expect(plain(base.textContent)).toContain('R$ 300,00'); expect(plain(base.textContent)).not.toContain('R$ 800,00')
  })

  it('detalha os componentes do mês: realizado, previsto, ajuste atribuído e o que não chega à conta', async () => {
    mockPlanning([{ ...recoveryMonth, unattributedAdjustment: '120.00' }])
    render(<FinancialPlanning workspaceId="family-1" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Detalhar 2026-11' }))
    const detail = screen.getByText('Ajuste atribuído à conta (simulado)').closest('dl')!
    expect(plain(detail.textContent)).toContain('R$ 500,00'); expect(plain(detail.textContent)).toContain('R$ 120,00'); expect(plain(detail.textContent)).toContain('regra sobre a projeção-base')
    expect(plain(detail.textContent)).toContain('Outros impactos no saldo (não discriminados)')
  })

  it('oferece a conta do ajuste em cada competência, sem distribuição automática', async () => {
    mockPlanning([recoveryMonth])
    render(<FinancialPlanning workspaceId="family-1" />)
    const select = await screen.findByLabelText('Conta do ajuste 2026-11') as HTMLSelectElement
    expect(select.value).toBe(''); expect(within(select).getByText('Somente consolidado (nenhuma conta)')).toBeTruthy(); expect(within(select).getByText('Conta negativa')).toBeTruthy()
  })

  it('não inventa recuperação quando a projeção da conta está incompleta', async () => {
    mockPlanning([{ ...recoveryMonth, recoveryProjectionComplete: false, recoveryStatus: 'IncompleteProjection' as const, projectedRecovery: null, baseProjectedRecovery: null, recoveryDeviation: null, recoveryTarget: null, closingNegativeBalance: null, baseClosingNegativeBalance: null, targetClosingBalance: null, targetBaseClosingBalance: null }])
    render(<FinancialPlanning workspaceId="family-1" />)
    const table = await screen.findByRole('table', { name: /Projeção-base/ })
    const row = within(table).getByRole('row', { name: /2026-11/ })
    expect(plain(row.textContent)).toContain('Projeção incompleta'); expect(plain(row.textContent)).toContain('Indisponível'); expect(plain(row.textContent)).not.toContain('R$ 0,00')
  })
})
