import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialDashboard } from './FinancialDashboard'

import { dashboard, dashboardResponse } from './dashboardFixtures'

describe('FinancialDashboard', () => {
  beforeEach(() => { vi.stubGlobal('fetch', vi.fn(async input => dashboardResponse(input))) })
  afterEach(() => { cleanup(); vi.restoreAllMocks() })

  it('opens in the current competence of Sao Paulo, not the browser UTC date', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-11-01T01:30:00Z'))
    try {
      render(<FinancialDashboard workspaceId="personal" />)
      expect((screen.getByLabelText('Competência do dashboard') as HTMLInputElement).value).toBe('2026-10')
      await screen.findByRole('heading', { name: 'Resumo do mês' })
      expect(fetch).toHaveBeenCalledWith(expect.stringContaining('competence=2026-10'), expect.anything())
    } finally { vi.useRealTimers() }
  })

  it('routes all four quick actions to existing modules and prioritizes overdue alerts', async () => {
    const navigate = vi.fn()
    vi.mocked(fetch).mockImplementation(async input => dashboardResponse(input, { ...dashboard, alerts: [dashboard.alerts[0], { ...dashboard.alerts[0], code: 'incomplete-coverage', title: 'Cobertura parcial' }, { ...dashboard.alerts[0], code: 'overdue-obligation', title: 'Despesa atrasada' }] }))
    render(<FinancialDashboard workspaceId="personal" onNavigate={navigate} />)
    const actions = (await screen.findByRole('heading', { name: 'Ações rápidas' })).closest('section')!
    for (const label of ['Novo lançamento', 'Transferir', 'Abrir Agenda', 'Ver cartões']) fireEvent.click(within(actions).getByRole('button', { name: label }))
    expect(navigate.mock.calls.map(([section]) => section)).toEqual(['transactions', 'transfers', 'calendar', 'cards'])
    const alerts = screen.getByRole('heading', { name: 'Alertas prioritários' }).closest('section')!
    expect(within(alerts).getAllByRole('listitem')[0].textContent).toContain('Despesa atrasada')
  })

  it('shows four top indicators from the response and a month composition with no invented balance', async () => {
    const { container } = render(<FinancialDashboard workspaceId="personal" />)
    await screen.findByRole('heading', { name: 'Resumo do mês' })
    expect(container.querySelectorAll('.dashboard-summary > article')).toHaveLength(4)
    expect(screen.getByText('Receitas do mês · realizadas')).toBeTruthy()
    expect(screen.getByText('Despesas do mês · realizadas')).toBeTruthy()
    expect(screen.getByText('Fechamento projetado · 2026-10')).toBeTruthy()
    expect(screen.getByRole('img', { name: /Composição/ })).toBeTruthy()
  })

  it('keeps unavailable balances and partial coverage explicit in the top indicators', async () => {
    vi.mocked(fetch).mockResolvedValue(Response.json({ ...dashboard, position: { ...dashboard.position, realizedBalance: null }, indicators: { ...dashboard.indicators, projectedBalance: null }, coverage: { ...dashboard.coverage, complete: false } }))
    const { container } = render(<FinancialDashboard workspaceId="personal" />)
    await screen.findByRole('heading', { name: 'Resumo do mês' })
    const metrics = container.querySelectorAll('.dashboard-summary > article')
    expect(metrics[0].textContent).toContain('Indisponível')
    expect(metrics[3].textContent).toContain('Indisponível')
    expect(metrics[3].textContent).toContain('Cobertura parcial')
    expect(metrics[3].textContent).not.toContain('0,00')
  })

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
    fetchMock.mockImplementation((input) => Promise.resolve(dashboardResponse(input, String(input).includes('competence=2026-11') ? incomplete : dashboard)))
    render(<FinancialDashboard workspaceId="family-1" />)
    await screen.findByText('Evolução projetada')
    fireEvent.change(screen.getByLabelText('Competência do dashboard'), { target: { value: '2026-11' } })
    fireEvent.change(screen.getByLabelText('Horizonte da projeção'), { target: { value: '12' } })
    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining('competence=2026-11'), expect.anything()))
    expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining('months=12'), expect.anything())
    expect(await screen.findByText(/Projeção parcial/)).toBeTruthy()
  })

  it('destaca saldo negativo, compromisso vencido e permite expandir alertas', async () => {
    const onNavigate = vi.fn()
    const negative = {
      ...dashboard,
      indicators: { ...dashboard.indicators, projectedBalance: '-120.50', nextCommitment: { ...dashboard.indicators.nextCommitment!, date: '2026-10-01' } },
      alerts: Array.from({ length: 6 }, (_, index) => ({ ...dashboard.alerts[0], code: `balance-${index}`, severity: 'Critical' as const, title: `Risco ${index}` })),
    }
    vi.mocked(fetch).mockImplementation(async input => dashboardResponse(input, negative))
    render(<FinancialDashboard workspaceId="family-1" onNavigate={onNavigate} />)
    expect(await screen.findByText('Risco de saldo projetado negativo.')).toBeTruthy()
    expect(await screen.findByText('Atrasado')).toBeTruthy()
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
    vi.mocked(fetch).mockImplementation(async input => dashboardResponse(input, empty))
    render(<FinancialDashboard workspaceId="family-1" />)
    expect(await screen.findByText('Nenhuma projeção disponível para o período selecionado.')).toBeTruthy()
    expect(await screen.findByText('Nenhum cartão cadastrado.')).toBeTruthy()
    expect(screen.getByText(/Disponibilidade antes do próximo recebimento indeterminada/)).toBeTruthy()
  })

  it('permite repetir uma leitura com falha e só exibe o sucesso confirmado', async () => {
    let tries = 0
    vi.mocked(fetch).mockImplementation(async input => String(input).includes('/financial-dashboard') && tries++ === 0 ? new Response('{}', { status: 503 }) : dashboardResponse(input))
    render(<FinancialDashboard workspaceId="family-1" />)
    expect(await screen.findByRole('alert')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByText('Evolução projetada')).toBeTruthy()
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('/financial-dashboard'))).toHaveLength(2)
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
