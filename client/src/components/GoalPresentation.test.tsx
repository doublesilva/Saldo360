import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { FinancialGoals } from './FinancialGoals'
import { barPercent, deadlineInfo, goalStanding, percentLabel } from './goals/goalText'

const api = vi.hoisted(() => ({
  listGoals: vi.fn(), getGoalSummary: vi.fn(), getGoalAvailability: vi.fn(), getProgress: vi.fn(), listAllocations: vi.fn(),
  createContribution: vi.fn(), createWithdrawal: vi.fn(), createGoal: vi.fn(), archiveGoal: vi.fn(), cancelAllocation: vi.fn(), changeGoal: vi.fn(),
}))
const accountApi = vi.hoisted(() => ({ listAccounts: vi.fn() }))
vi.mock('../api/goals', () => api)
vi.mock('../api/accounts', () => accountApi)

const goal = (overrides: Record<string, unknown>) => ({ id: 'g', workspaceId: 'w', name: 'Objetivo', description: null, goalType: 'Travel', targetAmount: '1000.00', targetDate: null, priority: 'High', status: 'Active', currency: 'BRL', createdAt: '', updatedAt: '', completedAt: null, archivedAt: null, ...overrides })
const progress = (goalId: string, allocated: string, remaining: string, percent: string, funded = false) => ({ goalId, targetAmount: '1000.00', allocatedAmount: allocated, remainingToTarget: remaining, progressPercentage: percent, isFunded: funded })
const text = (element: Element | null) => (element?.textContent ?? '').replace(/ /g, ' ')

beforeEach(() => {
  vi.clearAllMocks()
  api.getGoalSummary.mockResolvedValue({ allocatedToGoals: '400.00', realizedBalance: '5000.00', unallocatedRealizedBalance: '4600.00', activeGoals: 2, completedGoals: 1, pausedGoals: 1, coverageComplete: true, accounts: [] })
  api.getGoalAvailability.mockResolvedValue([{ financialAccountId: 'a1', realizedBalance: '5000.00', allocatedAmount: '400.00', unallocatedBalance: '4600.00' }])
  accountApi.listAccounts.mockResolvedValue([{ id: 'a1', name: 'Itaú corrente', isActive: true }])
  api.listAllocations.mockResolvedValue([])
})
afterEach(cleanup)

describe('Objetivos — apresentação', () => {
  it('mostra alvo, alocado, restante, percentual e estado em texto, com e sem prazo, e o aviso de que objetivo não é orçamento', async () => {
    api.listGoals.mockResolvedValue([goal({ id: 'a', name: 'Viagem', targetDate: '2999-12-31' }), goal({ id: 'b', name: 'Reserva', goalType: 'EmergencyFund' })])
    api.getProgress.mockImplementation((_w: string, id: string) => Promise.resolve(id === 'a' ? progress('a', '250.00', '750.00', '25.00') : progress('b', '0.00', '1000.00', '0.00')))
    render(<FinancialGoals workspaceId="w" />)

    const trip = await screen.findByRole('listitem', { name: 'Objetivo Viagem' })
    expect(await within(trip).findByText('25%')).toBeTruthy()
    expect(text(within(trip).getByText('Alvo').nextElementSibling)).toBe('R$ 1.000,00')
    expect(text(within(trip).getByText('Alocado').nextElementSibling)).toBe('R$ 250,00')
    expect(text(within(trip).getByText('Restante').nextElementSibling)).toBe('R$ 750,00')
    expect(text(within(trip).getByText('Prazo').nextElementSibling)).toContain('31/12/2999')
    expect(within(trip).getByText('Em andamento')).toBeTruthy()
    expect(within(trip).getByRole('progressbar', { name: 'Progresso de Viagem' }).getAttribute('aria-valuetext')?.replace(/ /g, ' ')).toBe('25%: R$ 250,00 de R$ 1.000,00')

    const reserve = screen.getByRole('listitem', { name: 'Objetivo Reserva' })
    expect(await within(reserve).findByText('Nada alocado ainda')).toBeTruthy()
    expect(text(within(reserve).getByText('Prazo').nextElementSibling)).toBe('Sem prazo definido')
    expect(screen.getByText(/diferentes dos limites do Orçamento/)).toBeTruthy()
    expect(screen.queryByText(/em dia|em risco/i)).toBeNull() // sem regra que sustente essa conclusão
  })

  it('trata prazo vencido, concluído, pausado, acima do alvo e progresso indisponível sem esconder nem inventar valores', async () => {
    api.listGoals.mockResolvedValue([
      goal({ id: 'late', name: 'Atrasado', targetDate: '2020-01-10' }), goal({ id: 'done', name: 'Pronto', status: 'Completed', targetDate: '2020-01-10' }),
      goal({ id: 'pause', name: 'Parado', status: 'Paused' }), goal({ id: 'over', name: 'Passou', targetDate: '2020-01-10' }), goal({ id: 'broken', name: 'Sem leitura' }),
    ])
    api.getProgress.mockImplementation((_w: string, id: string) => id === 'broken' ? Promise.reject(new Error('falha')) : Promise.resolve(({
      late: progress('late', '100.00', '900.00', '10.00'), done: progress('done', '1000.00', '0.00', '100.00', true), pause: progress('pause', '300.00', '700.00', '30.00'), over: progress('over', '1200.00', '0.00', '120.00', true),
    } as Record<string, unknown>)[id]))
    render(<FinancialGoals workspaceId="w" />)

    const late = await screen.findByRole('listitem', { name: 'Objetivo Atrasado' })
    expect(await within(late).findByText(/Prazo vencido em 10\/01\/2020/)).toBeTruthy() // vencido, em texto
    const done = screen.getByRole('listitem', { name: 'Objetivo Pronto' })
    expect(await within(done).findByText('Alvo atingido')).toBeTruthy()
    expect(within(done).queryByText(/Prazo vencido/)).toBeNull() // atingido: prazo passado não é atraso
    expect(within(done).getByText('Concluído', { selector: '.badge' })).toBeTruthy()
    const paused = screen.getByRole('listitem', { name: 'Objetivo Parado' })
    expect(await within(paused).findByText(/novas alocações ficam bloqueadas até retomar/)).toBeTruthy()
    const over = screen.getByRole('listitem', { name: 'Objetivo Passou' })
    expect(await within(over).findByText('Alocado acima do alvo')).toBeTruthy()
    expect(within(over).getByText('120%')).toBeTruthy()
    expect(within(over).getByRole('progressbar').getAttribute('aria-valuenow')).toBe('100') // a barra para em 100%, o texto não
    expect(text(within(over).getByText('Restante').nextElementSibling)).toBe('R$ 0,00')
    const broken = screen.getByRole('listitem', { name: 'Objetivo Sem leitura' })
    expect(await within(broken).findByText(/Progresso indisponível no momento/)).toBeTruthy()
    expect(text(within(broken).getByText('Alocado').nextElementSibling)).toBe('Indisponível') // nunca zero
  })

  it('o detalhe lista as alocações que explicam o progresso, com conta e data, e diz que alocar não cria movimentação', async () => {
    api.listGoals.mockResolvedValue([goal({ id: 'a', name: 'Viagem' })])
    api.getProgress.mockResolvedValue(progress('a', '300.00', '700.00', '30.00'))
    api.listAllocations.mockResolvedValue([
      { id: 'x1', workspaceId: 'w', goalId: 'a', financialAccountId: 'a1', operationType: 'Contribution', amount: '500.00', effectiveDate: '2026-10-05', operationId: 'o1', description: null, createdAt: '', cancelledAt: null },
      { id: 'x2', workspaceId: 'w', goalId: 'a', financialAccountId: 'a1', operationType: 'Withdrawal', amount: '200.00', effectiveDate: '2026-10-10', operationId: 'o2', description: null, createdAt: '', cancelledAt: null },
      { id: 'x3', workspaceId: 'w', goalId: 'a', financialAccountId: 'a1', operationType: 'Contribution', amount: '50.00', effectiveDate: '2026-10-11', operationId: 'o3', description: null, createdAt: '', cancelledAt: '2026-10-12T00:00:00Z' },
    ])
    render(<FinancialGoals workspaceId="w" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Ver detalhe de Viagem' }))

    const details = await screen.findByRole('article', { name: 'Detalhe de Viagem' })
    const rows = await within(details).findAllByRole('listitem')
    expect(rows.map(row => text(row))).toEqual([
      expect.stringContaining('Aporte · R$ 500,00 · Itaú corrente05/10/2026 · Ativo (soma ao progresso)'),
      expect.stringContaining('Retirada · R$ 200,00 · Itaú corrente10/10/2026 · Ativo (subtrai do progresso)'),
      expect.stringContaining('Cancelado (não conta no progresso)'),
    ])
    expect(within(details).getByText(/Alocado = aportes ativos − retiradas ativas \(2 ativa\(s\), 1 cancelada\(s\) fora da conta\)/)).toBeTruthy()
    expect(within(details).getByText(/Não cria receita, despesa nem transferência, não altera o saldo bancário/)).toBeTruthy()
  })

  it('o resumo usa o nome da conta, explica que o alocado está dentro do saldo e não mostra cobertura incompleta como zero', async () => {
    api.listGoals.mockResolvedValue([])
    api.getGoalSummary.mockResolvedValue({ allocatedToGoals: '0.00', realizedBalance: '0.00', unallocatedRealizedBalance: '0.00', activeGoals: 0, completedGoals: 0, pausedGoals: 0, coverageComplete: false, accounts: [] })
    render(<FinancialGoals workspaceId="w" />)
    expect(await screen.findByText(/Itaú corrente: R\$ 4\.600,00 não alocado/)).toBeTruthy()
    expect(screen.getByText(/é uma reserva dentro dele, não um valor a mais/)).toBeTruthy()
    expect(screen.getByText(/não devem ser lidos como zero/)).toBeTruthy()
    expect(screen.getByText(/Nenhum objetivo cadastrado/)).toBeTruthy()
  })

  it('o formulário aceita prazo opcional e o envia ao criar o objetivo', async () => {
    api.listGoals.mockResolvedValue([])
    api.createGoal.mockResolvedValue({})
    render(<FinancialGoals workspaceId="w" />)
    fireEvent.change(await screen.findByLabelText('Nome'), { target: { value: 'Carro' } })
    fireEvent.change(screen.getByLabelText('Valor-alvo'), { target: { value: '30000.00' } })
    fireEvent.change(screen.getByLabelText('Prazo (opcional)'), { target: { value: '2027-12-31' } })
    fireEvent.click(screen.getByRole('button', { name: 'Criar objetivo' }))
    await screen.findByText(/Nenhum objetivo cadastrado/)
    expect(api.createGoal).toHaveBeenCalledWith('w', expect.objectContaining({ name: 'Carro', targetAmount: '30000.00', targetDate: '2027-12-31' }))
  })
})

describe('goalText', () => {
  it('prazo: sem data, futuro, hoje, vencido e atingido', () => {
    const base = { status: 'Active' as const }
    expect(deadlineInfo({ ...base, targetDate: null }, false, '2026-10-30')).toEqual({ text: 'Sem prazo definido', overdue: false })
    expect(deadlineInfo({ ...base, targetDate: '2026-11-01' }, false, '2026-10-30').text).toBe('Prazo: 01/11/2026 (em 2 dias)')
    expect(deadlineInfo({ ...base, targetDate: '2026-10-30' }, false, '2026-10-30').text).toBe('Prazo: 30/10/2026 (hoje)')
    expect(deadlineInfo({ ...base, targetDate: '2026-10-29' }, false, '2026-10-30')).toEqual({ text: 'Prazo vencido em 29/10/2026 (há 1 dia)', overdue: true })
    expect(deadlineInfo({ ...base, targetDate: '2026-10-29' }, true, '2026-10-30').overdue).toBe(false)
    expect(deadlineInfo({ status: 'Completed', targetDate: '2026-10-29' }, false, '2026-10-30').overdue).toBe(false)
  })

  it('percentual e barra: o texto é real, a barra para em 100%, valor inválido é indisponível', () => {
    expect(percentLabel('25.00')).toBe('25%')
    expect(percentLabel('33.3')).toBe('33,3%')
    expect(percentLabel('120.00')).toBe('120%')
    expect(percentLabel(null)).toBe('Indisponível')
    expect(barPercent('120.00')).toBe(100)
    expect(barPercent('abc')).toBe(0)
    expect(goalStanding(null)).toBe('Unavailable')
    expect(goalStanding({ allocatedAmount: '0.00', isFunded: false, progressPercentage: '0.00' })).toBe('Empty')
    expect(goalStanding({ allocatedAmount: '1000.00', isFunded: true, progressPercentage: '100.00' })).toBe('Funded')
  })
})
