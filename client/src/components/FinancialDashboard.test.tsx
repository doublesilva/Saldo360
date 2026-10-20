import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialDashboard } from './FinancialDashboard'

const dashboard = {
  competence: '2026-10', months: 6,
  position: { realizedBalance: '5000.00', accounts: [], consideredAccounts: 1, negativeBalance: '0.00', allocatedToGoals: '0.00', unallocatedRealizedBalance: '5000.00' },
  cashFlow: { realizedIncome: '5000.00', realizedExpense: '2000.00', pendingIncome: '1500.00', pendingExpense: '800.00' },
  obligations: { pendingIncome: '1500.00', pendingExpense: '800.00', creditCardOutstanding: '300.00', debtOutstanding: '0.00', pendingItems: 2 },
  budgets: { targetCount: 2, planned: '3000.00', committed: '800.00', realized: '2000.00', exceededCount: 0, attentionCount: 1 },
  creditCards: { count: 1, outstanding: '300.00', overdueInvoices: 0, futureInvoices: 1, hasUnallocatedObligations: false, availableLimit: '9700.00' },
  debts: { activeContracts: 0, overdueInstallments: 0, installmentsDueInHorizon: '0.00', declaredOutstandingBalance: '0.00', unallocatedObligations: 0 },
  goals: { active: 1, completed: 0, paused: 0, allocated: '500.00', unallocated: '4500.00', coverageComplete: true },
  monthlyProjection: [{ competence: '2026-10', closingBalance: '5500.00', realizedIncome: '5000.00', realizedExpense: '2000.00', pendingIncome: '1500.00', pendingExpense: '800.00', creditCardOutstanding: '300.00', projectionComplete: true }],
  coverage: { complete: true, hasUnallocatedCreditCardObligations: false, hasUnallocatedDebtObligations: false, reason: null },
  indicators: { realizedBalance: '5000.00', projectedBalance: '5500.00', projectedVariation: '500.00', projectedVariationReference: 'realized', realizedNet: '3000.00', pendingNet: '700.00', knownNet: '3700.00', commitment: '1100.00', unallocatedAvailability: '4500.00', trend: 'Improving', nextCommitment: { source: 'PlannedTransaction', id: 'p1', date: '2026-10-20', amount: '800.00', description: 'Internet', accountId: 'a1' }, capacityBeforeNextIncome: { status: 'Unknown', nextIncomeDate: null, nextIncomeAmount: null, projectedBalanceBeforeIncome: null, minimumProjectedBalance: null } },
  alerts: [{ code: 'budget-attention', severity: 'Warning', title: 'Orçamento próximo do limite', description: 'A categoria está acima de 85%.', competence: '2026-10', amount: '800.00', sourceId: 'b1', context: null, referenceDate: '2026-10-01' }],
  asOf: '2026-10-16T12:00:00Z',
}

describe('FinancialDashboard', () => {
  beforeEach(() => { vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json(dashboard))) })
  afterEach(() => { cleanup(); vi.restoreAllMocks() })

  it('exibe o resumo, projeção, alertas e mantém valores monetários formatados', async () => {
    const onNavigate = vi.fn()
    render(<FinancialDashboard workspaceId="family-1" onNavigate={onNavigate} />)

    expect(await screen.findByRole('heading', { name: 'Visão Geral' })).toBeTruthy()
    expect(screen.getAllByText(/R\$.*5\.000,00/).length).toBeGreaterThan(0)
    expect(screen.getByText('Evolução projetada')).toBeTruthy()
    expect(screen.getByText(/Orçamento próximo do limite/)).toBeTruthy()
    expect(screen.getByText(/Disponibilidade antes do próximo recebimento indeterminada/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Ver módulo' }))
    expect(onNavigate).toHaveBeenCalledWith('budgets')
  })

  it('envia competência e horizonte selecionados e mostra cobertura incompleta', async () => {
    const fetchMock = vi.mocked(fetch)
    const incomplete = { ...dashboard, coverage: { ...dashboard.coverage, complete: false, reason: 'Fatura sem conta de pagamento.' } }
    fetchMock.mockImplementation((input) => Promise.resolve(Response.json(String(input).includes('competence=2026-11') ? incomplete : dashboard)))
    render(<FinancialDashboard workspaceId="family-1" />)
    await screen.findByText('Evolução projetada')
    fireEvent.change(screen.getByLabelText('Competência do dashboard'), { target: { value: '2026-11' } })
    fireEvent.change(screen.getByLabelText('Horizonte da projeção'), { target: { value: '12' } })
    await waitFor(() => expect(fetchMock).toHaveBeenLastCalledWith(expect.stringContaining('competence=2026-11'), expect.anything()))
    expect(fetchMock).toHaveBeenLastCalledWith(expect.stringContaining('months=12'), expect.anything())
    expect(await screen.findByText(/Projeção parcial/)).toBeTruthy()
  })

  it('destaca saldo negativo, compromisso vencido e permite expandir alertas', async () => {
    const onNavigate = vi.fn()
    const negative = {
      ...dashboard,
      indicators: { ...dashboard.indicators, projectedBalance: '-120.50', nextCommitment: { ...dashboard.indicators.nextCommitment!, date: '2026-10-01' } },
      alerts: Array.from({ length: 6 }, (_, index) => ({ ...dashboard.alerts[0], code: `balance-${index}`, severity: 'Critical' as const, title: `Risco ${index}` })),
    }
    vi.mocked(fetch).mockResolvedValue(Response.json(negative))
    render(<FinancialDashboard workspaceId="family-1" onNavigate={onNavigate} />)
    expect(await screen.findByText('Risco de saldo projetado negativo.')).toBeTruthy()
    expect(screen.getByText('Vencido')).toBeTruthy()
    expect(screen.getByRole('button', { name: /Mostrar todos os alertas/ })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: /Mostrar todos os alertas/ }))
    expect(screen.getByText(/Risco 5/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Ver Contas' }))
    expect(onNavigate).toHaveBeenCalledWith('accounts')
  })

  it('apresenta estados vazios específicos e dados desconhecidos sem substituir por zero', async () => {
    const empty = {
      ...dashboard,
      monthlyProjection: [],
      budgets: { ...dashboard.budgets, targetCount: 0 },
      creditCards: { ...dashboard.creditCards, count: 0 },
      debts: { ...dashboard.debts, activeContracts: 0 },
      goals: { ...dashboard.goals, active: 0, completed: 0 },
      alerts: [],
      indicators: { ...dashboard.indicators, nextCommitment: null },
      cashFlow: { realizedIncome: '0.00', realizedExpense: '0.00', pendingIncome: '0.00', pendingExpense: '0.00' },
    }
    vi.mocked(fetch).mockResolvedValue(Response.json(empty))
    render(<FinancialDashboard workspaceId="family-1" />)
    expect(await screen.findByText('Nenhuma projeção disponível para o período selecionado.')).toBeTruthy()
    expect(screen.getByText('Nenhum cartão cadastrado.')).toBeTruthy()
    expect(screen.getByText(/Disponibilidade antes do próximo recebimento indeterminada/)).toBeTruthy()
  })

  it('permite repetir uma leitura com falha e só exibe o sucesso confirmado', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(new Response('{}', { status: 503 }))
      .mockResolvedValueOnce(Response.json(dashboard))
    render(<FinancialDashboard workspaceId="family-1" />)
    expect(await screen.findByRole('alert')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByText('Evolução projetada')).toBeTruthy()
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('não mostra resposta obsoleta depois da troca rápida de família', async () => {
    let resolveOld!: (value: Response) => void
    vi.mocked(fetch).mockImplementation((input) => {
      if (String(input).includes('family-old')) return new Promise<Response>((resolve) => { resolveOld = resolve })
      return Promise.resolve(new Response('{}', { status: 503 }))
    })
    const view = render(<FinancialDashboard workspaceId="family-old" />)
    await screen.findByText('Carregando painel financeiro…')
    view.rerender(<FinancialDashboard workspaceId="family-new" />)
    expect(await screen.findByRole('alert')).toBeTruthy()
    resolveOld(Response.json(dashboard))
    await Promise.resolve()
    expect(screen.queryByText('Evolução projetada')).toBeNull()
  })
})
