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
