import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { OverviewCashFlow } from '../../api/overview'
import { OverviewCashFlowSection, OverviewCompetenceSection } from './OverviewCashFlow'

afterEach(cleanup)

const flow = (over: Partial<OverviewCashFlow> = {}): OverviewCashFlow => ({
  state: 'Complete', competence: '2026-10', from: '2026-10-01', through: '2026-10-31', inProgress: false, accountCount: 1,
  opening: '1000.00', openingMissing: [],
  inflows: { total: '12792.00', credits: '100.00', refunds: null, transfersReceived: '12692.00' },
  outflows: { total: '40.00', debits: '40.00', transfersSent: '0.00' },
  closing: '13752.00', identityHolds: true, snapshotConsistent: true,
  bankPosition: { state: 'Complete', total: '13752.00', observedAt: '2026-10-31T15:30:00Z', accounts: [], missing: [] },
  reconciliation: { status: 'Verified', bank: '13752.00', calculated: '13752.00', difference: '0.00', cutoffDate: '2026-10-31', reasons: [], accounts: [] },
  internalMoved: '0.00',
  transfers: [{ id: 't1', date: '2026-10-29', description: 'Pix enviado', source: 'Itaú PJ', destination: 'Itaú PF', amount: '12692.00', relation: 'Inbound', effect: '12692.00' }],
  byCompetence: { classifiedIncome: '100.00', expenses: '40.00', difference: '60.00' },
  ...over,
})

const renderFlow = (data: OverviewCashFlow, onNavigate = vi.fn()) => { render(<OverviewCashFlowSection flow={data} scopeAccountId="pf" onNavigate={onNavigate} />); return onNavigate }

describe('OverviewCashFlowSection', () => {
  it('mostra abertura, entradas, saídas e fechamento do servidor, com as parcelas e a ponte de saldo', () => {
    renderFlow(flow())
    expect(within(screen.getByRole('article', { name: 'Saldo de abertura' })).getByText(/1\.000,00/)).toBeTruthy()
    const inflows = screen.getByRole('article', { name: 'Entradas de caixa' })
    expect(within(inflows).getByText(/12\.792,00/)).toBeTruthy()
    expect(within(inflows).getByText('Transferências recebidas')).toBeTruthy()
    expect(within(screen.getByRole('article', { name: 'Saídas de caixa' })).getAllByText(/40,00/).length).toBe(2)
    expect(within(screen.getByRole('article', { name: 'Saldo de fechamento' })).getByText(/13\.752,00/)).toBeTruthy()
    const bridge = screen.getByRole('table', { name: 'Ponte de saldo do período' })
    expect(within(bridge).getByText(/\+ Transferência recebida/)).toBeTruthy()
    expect(within(bridge).getByText(/− Transferência enviada/)).toBeTruthy()
    expect(screen.getByText('Reconciliação verificada')).toBeTruthy()
    expect(screen.getByText(/Diferença de conciliação/)).toBeTruthy()
  })

  it('sem saldo de abertura diz "Não apurado" e que não é possível conciliar, nunca zero', () => {
    renderFlow(flow({ state: 'Partial', opening: null, closing: null, identityHolds: null, openingMissing: [{ accountId: 'pf', name: 'Itaú PF', reason: 'OpeningBalanceNotConfigured' }],
      reconciliation: { status: 'NotVerifiable', bank: null, calculated: null, difference: null, cutoffDate: null, reasons: ['NoBankPosition'], accounts: [] },
      bankPosition: { state: 'Unavailable', total: null, observedAt: null, accounts: [], missing: [] } }))
    expect(within(screen.getByRole('article', { name: 'Saldo de abertura' })).getByText('Não apurado')).toBeTruthy()
    expect(within(screen.getByRole('article', { name: 'Saldo de fechamento' })).getByText('Não apurado')).toBeTruthy()
    expect(screen.getByText(/Não é possível conciliar: Itaú PF \(sem saldo inicial configurado\)/)).toBeTruthy()
    expect(screen.getByText('Reconciliação não verificada')).toBeTruthy()
    expect(within(screen.getByRole('region', { name: 'Posição bancária mais recente' })).getByText('Indisponível')).toBeTruthy()
  })

  it('mostra a diferença de conciliação quando divergente e a data da posição bancária', () => {
    renderFlow(flow({ reconciliation: { status: 'Divergent', bank: '13752.00', calculated: '13700.00', difference: '52.00', cutoffDate: '2026-10-31', reasons: [], accounts: [] } }))
    expect(screen.getByText('Reconciliação com diferença')).toBeTruthy()
    expect(screen.getByText(/Banco atualizado em/)).toBeTruthy()
  })

  it('no consolidado a transferência interna aparece neutra e abre os lançamentos filtrados', () => {
    const onNavigate = renderFlow(flow({ internalMoved: '12692.00', transfers: [{ id: 't1', date: '2026-10-29', description: 'Pix', source: 'Itaú PJ', destination: 'Itaú PF', amount: '12692.00', relation: 'Internal', effect: '0.00' }] }))
    expect(screen.getByText(/neutro \(as duas contas estão no conjunto\)/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Ver transferências' }))
    expect(onNavigate).toHaveBeenCalledWith('transactions', { transactions: { from: '2026-10-01', to: '2026-10-31', accountId: 'pf', type: 'Transfer' } })
  })

  it('a seção de competência é independente e avisa que não explica o saldo bancário', () => {
    render(<OverviewCompetenceSection flow={flow()} />)
    expect(screen.getByText('Não explica, sozinho, o saldo bancário')).toBeTruthy()
    expect(screen.getByText('Receitas classificadas')).toBeTruthy()
  })
})

const recovery = (over: Partial<NonNullable<OverviewCashFlow['recovery']>> = {}): NonNullable<OverviewCashFlow['recovery']> => ({
  openingUsed: '10000.00', target: '4800.00', targetState: 'Fixed', targetFactor: '0.80', capacity: '6000.00', targetFixedAt: '2026-11-10T15:00:00Z', fixDate: '2026-11-10', legacyTarget: false, pendingReason: null,
  currentBalance: '-8500.00', currentAsOf: '2026-11-15', recoveryCurrent: '1500.00',
  projectedClosing: '-5000.00', projectedRecovery: '5000.00', projectedAchievementPercent: '104.2', band: 'Exceeded', finalRecovery: null, finalAchievementPercent: null,
  ...over,
})
const inProgress = (r: ReturnType<typeof recovery> | null) => flow({ competence: '2026-11', from: '2026-11-01', through: '2026-11-30', inProgress: true, opening: '-10000.00', closing: '-8500.00', recovery: r })

describe('Recuperação do cheque especial nos cards de abertura e fechamento', () => {
  it('abertura mostra saldo inicial, meta, estado Fixada e data de fixação; sem recuperação não mostra nada disso', () => {
    renderFlow(inProgress(recovery()))
    const card = screen.getByRole('article', { name: 'Saldo de abertura' })
    expect(within(card).getAllByText(/10\.000,00/).length).toBeGreaterThan(0)
    expect(within(card).getByText('Meta de recuperação do mês')).toBeTruthy()
    expect(within(card).getByText(/4\.800,00/)).toBeTruthy()
    expect(within(card).getByText('Fixada')).toBeTruthy()
    expect(within(card).getByText(/Fixada em/)).toBeTruthy()
    cleanup()
    renderFlow(flow())
    expect(screen.queryByText('Meta de recuperação do mês')).toBeNull()
    expect(screen.queryByRole('progressbar')).toBeNull()
  })

  it('meta provisória diz quando será fixada e meta pendente não mostra valor inventado', () => {
    renderFlow(inProgress(recovery({ targetState: 'Provisional', targetFixedAt: null })))
    expect(screen.getByText('Provisória')).toBeTruthy()
    expect(screen.getByText('Será fixada em')).toBeTruthy()
    cleanup()
    renderFlow(inProgress(recovery({ targetState: 'Pending', target: null, targetFixedAt: null, pendingReason: 'CardCoverageIncomplete', projectedAchievementPercent: null, band: 'NoTarget' })))
    const card = screen.getByRole('article', { name: 'Saldo de abertura' })
    expect(within(card).getAllByText('Pendente').length).toBeGreaterThan(0)
    expect(within(card).getByText(/faturas de cartão ainda não fechadas/)).toBeTruthy()
    expect(screen.queryByRole('progressbar')).toBeNull()
  })

  it('mês em andamento vira "Saldo parcial" com recuperação atual, prevista e percentual contra a meta vigente', () => {
    renderFlow(inProgress(recovery()))
    const card = screen.getByRole('article', { name: 'Saldo parcial' })
    expect(within(card).getByRole('heading', { name: /Saldo parcial · 15\/11/ })).toBeTruthy()
    expect(within(card).getByText('Recuperação em andamento')).toBeTruthy()
    expect(within(card).getByText(/1\.500,00/)).toBeTruthy()
    expect(within(card).getByText('Recuperação prevista no encerramento')).toBeTruthy()
    expect(within(card).getByText(/104,2%/)).toBeTruthy()
    expect(within(card).getByText('Meta vigente')).toBeTruthy()
  })

  it('mês encerrado mantém "Saldo de fechamento" com recuperação final contra a meta fixada', () => {
    renderFlow(flow({ opening: '-10000.00', closing: '-5200.00', recovery: recovery({ projectedRecovery: null, projectedClosing: null, projectedAchievementPercent: null, finalRecovery: '4800.00', finalAchievementPercent: '100.0', band: 'Met', currentBalance: '-5200.00', currentAsOf: '2026-10-31' }) }))
    const card = screen.getByRole('article', { name: 'Saldo de fechamento' })
    expect(within(card).getByText('Recuperação final')).toBeTruthy()
    expect(within(card).getByText('Meta fixada')).toBeTruthy()
    expect(within(card).getByText(/100,0%/)).toBeTruthy()
  })

  it('outubro histórico confirmado mostra valores, 115,9% e barra azul completa sem aplicar 80% retroativamente', () => {
    renderFlow(flow({ opening: '-15284.39', closing: '-13811.51', recovery: recovery({
      openingUsed: '15284.39', target: '1270.38', targetState: 'Fixed', targetFactor: null, capacity: null,
      targetFixedAt: '2026-10-08T15:00:00Z', fixDate: '2026-10-10', legacyTarget: true,
      currentBalance: '-13811.51', currentAsOf: '2026-10-31', recoveryCurrent: '1472.88', projectedClosing: null,
      projectedRecovery: null, projectedAchievementPercent: null, finalRecovery: '1472.88', finalAchievementPercent: '115.9', band: 'Exceeded',
    }) }))

    const opening = screen.getByRole('article', { name: 'Saldo de abertura' })
    expect(within(opening).getAllByText(/15\.284,39/).length).toBe(2)
    expect(within(opening).getByText(/1\.270,38/)).toBeTruthy()
    expect(within(opening).getByText(/08\/10\/2026.*meta histórica/)).toBeTruthy()
    const closing = screen.getByRole('article', { name: 'Saldo de fechamento' })
    expect(within(closing).getByText(/13\.811,51/)).toBeTruthy()
    expect(within(closing).getByText('◎ Recuperação do saldo')).toBeTruthy()
    expect(within(closing).getByText('115,9%')).toBeTruthy()
    expect(within(closing).getByText(/R\$ 1\.472,88 \/ R\$ 1\.270,38/)).toBeTruthy()
    const bar = within(closing).getByRole('progressbar')
    expect(bar.getAttribute('aria-valuenow')).toBe('100')
    expect(bar.querySelector('span')?.getAttribute('style')).toContain('min(100%, 115.9%)')
    expect(closing.querySelector('.ov-recovery--Exceeded .ov-recovery__track')).toBeTruthy()
  })

  it.each([
    ['Exceeded', '250.0', 'Acima da meta', 100], ['Met', '100.0', 'Meta atingida', 100], ['Met', '80.0', 'Meta atingida', 80], ['Attention', '65.5', 'Atenção', 65],
    ['Critical', '50.0', 'Crítico', 50], ['Critical', '-12.5', 'Crítico', 0],
  ] as const)('faixa %s com %s%%: texto da faixa, percentual legível, barra limitada a 0–100%% e rótulo acessível', (band, percent, label, now) => {
    renderFlow(inProgress(recovery({ band, projectedAchievementPercent: percent })))
    const bar = screen.getByRole('progressbar')
    expect(bar.getAttribute('aria-valuenow')).toBe(String(now))
    expect(bar.getAttribute('aria-label')).toContain(label)
    expect(bar.getAttribute('aria-label')).toContain(`${percent.replace('.', ',')}%`)
    const card = screen.getByRole('article', { name: 'Saldo parcial' })
    expect(within(card).getAllByText(new RegExp(label)).length).toBeGreaterThan(0)
    expect(card.querySelector('.ov-recovery')?.className).toContain(`ov-recovery--${band}`)
    const fill = bar.querySelector('span') as HTMLElement
    expect(fill.style.width).toBe(now === 0 ? '0%' : `min(100%, ${percent}%)`)
  })

  it('meta zero mostra "Sem meta" e nenhum percentual artificial', () => {
    renderFlow(inProgress(recovery({ target: '0.00', projectedAchievementPercent: null, band: 'NoTarget' })))
    const card = screen.getByRole('article', { name: 'Saldo parcial' })
    expect(within(card).getByText('Sem meta: sem percentual.')).toBeTruthy()
    expect(screen.queryByRole('progressbar')).toBeNull()
  })
})
