import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AccountReconciliation, ReconciliationOverview, ReconciliationPeriod } from '../api/accountReconciliation'
import { AccountBalanceReconciliation } from './AccountBalanceReconciliation'
import { FinancialDashboard } from './FinancialDashboard'
import { dashboard, dashboardResponse } from './dashboardFixtures'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
/** O formatador usa espaço não separável depois de R$. */
const text = (element: Element | null) => (element?.textContent ?? '').replace(/ /g, ' ')

const account = (overrides: Partial<AccountReconciliation> = {}): AccountReconciliation => ({
  accountId: 'a1', name: 'Itaú PF — corrente', scope: 'PF', isActive: true,
  bank: { amount: '-13811.51', kind: 'CurrentBalance', observedAt: '2026-10-28T22:17:27Z', asOfDate: '2026-10-28', source: 'Pluggy', reliability: 'Provisional', reliabilityReason: 'ExcludedMovementsLimitHistory',
    overdraft: { contractedLimit: '20000.00', usedLimit: '13811.51', note: 'O limite do cheque especial é crédito do banco: não compõe o saldo nem é dinheiro próprio.' } },
  calculated: { amount: '-13811.51', asOf: '2026-10-28', openingSource: 'BalanceAnchor', calculableFrom: '2026-10-07', unavailableReason: null },
  difference: '0.00', status: 'MovementsInReview', statusReason: 'ReviewLimitsCalculableHistory', explainedByReview: null, unexplained: null, lastSyncedAt: '2026-10-29T10:00:00Z', linkStatus: 'Active',
  coverage: { importedFrom: '2025-10-28', importedThrough: '2026-10-28', bankSideFrom: '2025-10-29', ledgerFrom: '2026-10-07', quality: 'LimitedByReview', postedMovements: 722, reason: 'LimitedByReview' },
  review: { count: 21, netAmount: '350.00', firstDate: '2025-11-07', lastDate: '2026-10-06', beforeLedgerStart: 21, insideLedgerRange: 0, pending: 2 },
  consistency: { checked: 0, matching: 0, note: '' }, ...overrides,
})

const overview = (accounts: AccountReconciliation[], complete = true): ReconciliationOverview => ({
  today: '2026-10-29', accounts,
  byScope: [{ scope: 'PF', accounts: 1, bank: complete ? '-13811.51' : null, calculated: complete ? '-13811.51' : null, difference: complete ? '0.00' : null, complete, reasons: complete ? [] : ['Itaú PJ: referência bancária insuficiente'] }],
  consolidated: { scope: 'All', accounts: accounts.length, bank: complete ? '-13811.51' : null, calculated: complete ? '-13811.51' : null, difference: complete ? '0.00' : null, complete, reasons: [] },
})

const period: ReconciliationPeriod = {
  accountId: 'a1', name: 'Itaú PF — corrente', scope: 'PF', from: '2026-05', through: '2026-10',
  months: [
    { month: '2026-09', ledgerOpening: null, ledgerReason: 'BeforeCalculableStart', bankOpening: '-9000.00', bankReason: null, difference: null, reviewInMonth: 3, reviewNetInMonth: '120.00' },
    { month: '2026-10', ledgerOpening: null, ledgerReason: 'BeforeCalculableStart', bankOpening: '-12000.00', bankReason: null, difference: null, reviewInMonth: 1, reviewNetInMonth: '50.00', blockingReview: 1 },
  ],
  observations: [{ asOf: '2026-10-27', observedAt: '2026-10-27T22:00:00Z', observed: '-13500.00', ledger: null, ledgerDifference: null, bankSide: '-13500.00', bankSideDifference: '0.00', status: 'Match' }],
}

describe('AccountBalanceReconciliation', () => {
  it('mostra banco, calculado, diferença, referência, última sincronização, histórico calculável e revisões, sem pedir saldo inicial manual', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(overview([account()]))))
    render(<AccountBalanceReconciliation workspaceId="w" />)

    const card = await screen.findByRole('article', { name: 'Conciliação de Itaú PF — corrente' })
    expect(within(card).getByText('Movimentações em revisão', { selector: '.badge' })).toBeTruthy()
    expect(within(card).getByText(/histórico calculável começa depois delas/)).toBeTruthy()
    expect(text(within(card).getByText('Saldo informado pelo banco').nextElementSibling)).toBe('-R$ 13.811,51')
    expect(text(within(card).getByText('Saldo calculado pelo Saldo360').nextElementSibling)).toBe('-R$ 13.811,51')
    expect(text(within(card).getByText('Diferença').nextElementSibling)).toBe('R$ 0,00')
    expect(within(card).getByText('Provisória')).toBeTruthy()
    expect(within(card).getByText('Saldo contábil (atual)')).toBeTruthy()
    expect(within(card).getByText('desde 07/10/2026')).toBeTruthy()
    expect(text(within(card).getByText('Movimentações em revisão', { selector: 'dt' }).nextElementSibling)).toContain('21')
    expect(within(card).getByText(/não é preciso informar saldo inicial manual/)).toBeTruthy()
    expect(within(card).getByText(/2 movimentação\(ões\) pendente\(s\) ou futura\(s\) não entram no saldo realizado/)).toBeTruthy()
    expect(text(card)).toMatch(/Cheque especial: limite R\$ 20\.000,00, em uso R\$ 13\.811,51\. O limite do cheque especial é crédito do banco/)
  })

  it('não apresenta total parcial como completo e mantém PF e PJ separados', async () => {
    const insufficient = account({ accountId: 'a2', name: 'Itaú PJ', scope: 'PJ', status: 'InsufficientBankReference', statusReason: 'BalanceNatureAmbiguous', bank: null, difference: null,
      calculated: { amount: null, asOf: '2026-10-29', openingSource: 'None', calculableFrom: null, unavailableReason: 'OpeningBalanceNotConfigured' } })
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(overview([account(), insufficient], false))))
    render(<AccountBalanceReconciliation workspaceId="w" />)

    await screen.findByRole('article', { name: 'Conciliação de Itaú PJ' })
    expect(screen.getByText('Referência bancária insuficiente', { selector: '.badge' })).toBeTruthy()
    expect(screen.getByText(/incoerente com o cheque especial/)).toBeTruthy()
    expect(screen.getByText('Indisponível: falta saldo em alguma conta')).toBeTruthy()
    expect(screen.getByText('Contas PF — banco / calculado').nextElementSibling?.textContent).toBe('Indisponível')
  })

  it('consulta as divergências por período sem somar nada no navegador', async () => {
    const fetchMock = vi.fn((url: string, _init?: RequestInit) => url.includes('?from=') ? json(period) : json(overview([account()])))
    vi.stubGlobal('fetch', fetchMock)
    render(<AccountBalanceReconciliation workspaceId="w" />)
    const card = await screen.findByRole('article', { name: 'Conciliação de Itaú PF — corrente' })

    fireEvent.click(within(card).getByRole('button', { name: 'Divergências por período' }))

    expect(await within(card).findByText('Setembro de 2026')).toBeTruthy()
    const rows = within(card).getAllByRole('row')
    expect(text(rows[1])).toContain('Antes do histórico calculável')
    expect(text(rows[1])).toContain('-R$ 9.000,00')
    expect(text(rows[1])).toContain('3 (R$ 120,00)')
    expect(text(rows[2])).toContain('resolva 1 movimentação(ões) em revisão deste mês em diante') // o que bloqueia a abertura calculada de outubro
    expect(within(card).getByText(/Confere/)).toBeTruthy()
    const periodCall = fetchMock.mock.calls.find(call => String(call[0]).includes('?from='))!
    expect(String(periodCall[0])).toMatch(/balance-reconciliation\?from=\d{4}-\d{2}&through=\d{4}-\d{2}/)
    expect(fetchMock.mock.calls.every(call => !call[1]?.method || call[1].method === 'GET')).toBe(true)

    fireEvent.change(within(card).getByLabelText('De'), { target: { value: '2026-01' } })
    await waitFor(() => expect(fetchMock.mock.calls.some(call => String(call[0]).includes('from=2026-01'))).toBe(true))
  })

  it('some quando não há conta integrada', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(overview([]))))
    const { container } = render(<AccountBalanceReconciliation workspaceId="w" />)

    await waitFor(() => expect(container.querySelector('.balance-reconciliation')).toBeNull())
  })
})

describe('FinancialDashboard — referência bancária das contas (T09.5)', () => {
  it('mostra escopo, referência provisória e revisões por conta, subtotais PF/PJ e o aviso de ressalvas', async () => {
    const data = {
      ...dashboard,
      position: { ...dashboard.position, realizedBalance: null, accounts: [
        { accountId: 'a1', name: 'Itaú PF', realizedBalance: '1500.75', complete: true, isPrimary: true, scope: 'PF', referenceSource: 'BalanceAnchor', referenceReliability: 'Provisional', reconciliationStatus: 'MovementsInReview', reviewCount: 21 },
        { accountId: 'a2', name: 'Reserva PJ', realizedBalance: null, complete: false, scope: 'PJ', referenceSource: null, referenceReliability: null, reconciliationStatus: null, reviewCount: 0 },
      ], byScope: [{ scope: 'PF', realizedBalance: '1500.75', accounts: 1, complete: true }, { scope: 'PJ', realizedBalance: null, accounts: 1, complete: false }], hasReferenceCaveats: true },
    }
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => String(input).includes('/financial-dashboard') ? Response.json(data) : dashboardResponse(input)))
    render(<FinancialDashboard workspaceId="personal" />)

    const row = (await screen.findByText('Itaú PF')).closest('li')!
    expect(text(row)).toContain('referência provisória')
    expect(text(row)).toContain('21 em revisão')
    expect(text(screen.getByLabelText('Saldo realizado por tipo de conta'))).toBe('PF: R$ 1.500,75 · PJ: indisponível')
    expect(screen.getByText(/referência bancária provisória, movimentações em revisão ou histórico incompleto/)).toBeTruthy()
    expect(screen.getByText('Saldo realizado · efetivo').closest('article')?.textContent).toContain('Indisponível') // total parcial não vira consolidado completo
  })
})
