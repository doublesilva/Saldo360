import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CardInstallments as CardInstallmentsData, CardInvoiceView, CardOverviewItem, InvoiceOverview, ObligationProjection } from '../api/cardProjection'
import { CardInstallments } from './CardInstallments'
import { CardOverview } from './CardOverview'
import { CardPlanning } from './CardPlanning'
import { FinancialCreditCardsPage } from './FinancialCreditCards'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const complete = { complete: true, reasons: [] as string[] }
/** O formatador usa espaço não separável depois de R$: normaliza para comparar. */
const text = (element: Element | null) => (element?.textContent ?? '').replace(/ /g, ' ')

const closed: CardInvoiceView = {
  invoiceId: 'i1', competence: '2026-10', cycleStart: '2026-09-09', closing: '2026-10-08', due: '2026-10-15', cycle: 'Closed', phase: 'Closed', basis: 'BankTotal', provisional: false,
  awaitingBankBill: false, bankTotal: '500.00', bankTotalSource: 'Pluggy', projectedTotal: '230.00', obligation: '500.00', paid: '200.00', remaining: '300.00', paymentState: 'Partial',
  reconciliation: 'Divergent', reconciliationReason: 'TotalMismatch', difference: '270.00', bankReportedPaid: '200.00',
  components: { composition: '230.00', pendingKnown: '0.00', knownInstallments: '0.00', estimated: '0.00' }, flags: [], absorbedItems: 0, unprojectedInstallments: 0,
}
const open: CardInvoiceView = {
  ...closed, invoiceId: null, competence: '2026-11', closing: '2026-11-08', due: '2026-11-15', cycle: 'Open', phase: 'Open', basis: 'ProjectedOpen', provisional: true, bankTotal: null,
  bankTotalSource: null, projectedTotal: '130.00', obligation: '130.00', paid: '0.00', remaining: '130.00', paymentState: 'Unpaid', reconciliation: 'NotVerified', reconciliationReason: null,
  difference: null, bankReportedPaid: null, components: { composition: '0.00', pendingKnown: '30.00', knownInstallments: '100.00', estimated: '0.00' }, unprojectedInstallments: 2,
}
const card = (overrides: Partial<CardOverviewItem> = {}): CardOverviewItem => ({
  cardId: 'c1', name: 'Itaú Empresas Visa', issuer: 'MeuPluggy', lastFour: '3631', scope: 'PJ', origin: 'Pluggy', creditLimit: '10000.00', availableLimit: '7500.00', limitSource: 'Bank',
  payer: { accountId: 'a1', name: 'Itaú PF', scope: 'PF', isPrimary: true }, sync: { status: 'Active', lastSyncedAt: '2026-10-29T10:00:00Z' },
  schedule: { closingDay: 8, dueDay: 15, source: 'BankHistory', strong: true }, currentInvoice: open, nextDue: closed, overdueRemaining: '0.00', invoices: [closed], coverage: complete, ...overrides,
})
const overview = (cards: CardOverviewItem[], coverage = complete): InvoiceOverview => ({ competence: '2026-10', today: '2026-10-29', cards, obligation: '500.00', paid: '200.00', remaining: '300.00', coverage })

describe('CardOverview', () => {
  it('mostra fechamento, pagamento e conciliação como dimensões separadas, com o total do banco como obrigação', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(overview([card()]))))
    render(<CardOverview workspaceId="w" />)

    const article = await screen.findByRole('article', { name: 'Itaú Empresas Visa' })
    expect(within(article).getByText('Cartão PJ')).toBeTruthy()
    expect(within(article).getByText('Itaú PF (PF)')).toBeTruthy() // PJ pago pela conta PF: a titularidade do cartão continua PJ
    const invoice = within(article).getByLabelText('Fatura 2026-10')
    expect(within(invoice).getByText('Fechada pelo banco')).toBeTruthy()
    expect(within(invoice).getByText('Parcialmente paga')).toBeTruthy()
    expect(within(invoice).getByText('Divergente do total do banco')).toBeTruthy()
    expect(text(invoice)).toContain('R$ 500,00') // total bancário
    expect(text(invoice)).toContain('R$ 230,00') // total projetado pelo Saldo360
    expect(text(invoice)).toContain('R$ 300,00') // saldo restante
    expect(text(invoice)).toMatch(/Diferença de conciliação.*R\$ 270,00.*nada é compensado/)
    expect(within(article).getByText(/Fatura atual \(provisória\)/)).toBeTruthy()
    expect(within(article).getByText(/não é saldo/)).toBeTruthy()
  })

  it('sinaliza dados incompletos sem esconder o valor conhecido', async () => {
    const incomplete = { complete: false, reasons: ['UnprojectedInstallments', 'MissingPayerAccount'] }
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(overview([card({ payer: null, coverage: incomplete, invoices: [open] })], incomplete))))
    render(<CardOverview workspaceId="w" />)

    await screen.findByRole('article', { name: 'Itaú Empresas Visa' })
    expect(screen.getAllByText(/Dados incompletos/).length).toBeGreaterThan(0)
    expect(screen.getAllByText('Há cartão sem conta pagadora definida.').length).toBeGreaterThan(0)
    expect(screen.getByText('Não definida')).toBeTruthy()
    const invoice = screen.getByLabelText('Fatura 2026-11')
    expect(text(invoice)).toContain('R$ 130,00')
    expect(text(invoice)).toMatch(/2 parcela\(s\) restante\(s\) sem evidência/)
    expect(screen.getAllByText('Provisória').length).toBeGreaterThan(0)
  })

  it('consulta a competência escolhida no backend e não soma nada no navegador', async () => {
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) => json(overview([card()])))
    vi.stubGlobal('fetch', fetchMock)
    render(<CardOverview workspaceId="w" />)
    await screen.findByRole('article', { name: 'Itaú Empresas Visa' })

    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2026-12' } })

    await waitFor(() => expect(fetchMock.mock.calls.some(call => String(call[0]).includes('competence=2026-12'))).toBe(true))
    expect(fetchMock.mock.calls.every(call => !call[1]?.method || call[1].method === 'GET')).toBe(true)
  })
})

describe('CardPlanning', () => {
  const month = (m: string, extra: Partial<ObligationProjection['items'][number]> = {}) => ({
    month: m, confirmedInvoices: '0.00', openInvoicesProjected: '0.00', futureInstallments: '0.00', estimatedCommitments: '0.00', knownCommitments: '0.00', estimatedIncluded: '0.00',
    obligation: '0.00', paid: '0.00', remaining: '0.00', cards: 5, coverage: complete, byCard: [], byPayer: [], ...extra,
  })
  const projection: ObligationProjection = {
    from: '2026-10', months: 3, today: '2026-10-29', carryover: '45.50', coverage: { complete: false, reasons: ['WeakSchedule'] },
    items: [
      month('2026-10', { confirmedInvoices: '500.00', paid: '200.00', remaining: '300.00', obligation: '500.00',
        byCard: [{ cardId: 'c1', name: 'Itaú Empresas Visa', scope: 'PJ', payerAccountId: 'a1', obligation: '500.00', paid: '200.00', remaining: '300.00', confirmed: '500.00', projected: '0.00', estimated: '0.00' }],
        byPayer: [{ accountId: 'a1', name: 'Itaú PF', scope: 'PF', obligation: '500.00', paid: '200.00', remaining: '300.00', cardIds: ['c1'] }] }),
      month('2026-11', { openInvoicesProjected: '130.00', obligation: '130.00', remaining: '130.00', coverage: { complete: false, reasons: ['WeakSchedule'] },
        byCard: [{ cardId: 'c1', name: 'Itaú Empresas Visa', scope: 'PJ', payerAccountId: 'a1', obligation: '130.00', paid: '0.00', remaining: '130.00', confirmed: '0.00', projected: '130.00', estimated: '0.00' }],
        byPayer: [{ accountId: 'a1', name: 'Itaú PF', scope: 'PF', obligation: '130.00', paid: '0.00', remaining: '130.00', cardIds: ['c1'] }] }),
      month('2026-12', { futureInstallments: '100.00', estimatedCommitments: '100.00', obligation: '200.00', remaining: '200.00',
        byPayer: [{ accountId: null, name: null, scope: null, obligation: '200.00', paid: '0.00', remaining: '200.00', cardIds: ['c9'] }] }),
    ],
  }

  it('separa confirmado, projetado e estimado por mês e mostra o saldo em atraso fora da janela', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(projection)))
    render(<CardPlanning workspaceId="w" />)

    const table = (await screen.findAllByRole('table'))[0]
    const rows = within(table).getAllByRole('row')
    expect(rows).toHaveLength(4)
    expect(text(rows[1])).toContain('R$ 500,00') // confirmada pelo banco
    expect(text(rows[2])).toContain('R$ 130,00') // aberta, projetada
    expect(text(rows[3])).toContain('R$ 100,00') // parcela futura e estimada, em colunas diferentes
    expect(within(rows[3]).getAllByRole('cell')[3].className).toBe('estimated')
    expect(text(screen.getByText(/Saldo em atraso de meses anteriores/).parentElement)).toContain('R$ 45,50')
    expect(within(rows[2]).getByText('Incompletos')).toBeTruthy()
    expect(within(rows[1]).getByText('Completos')).toBeTruthy()
  })

  it('agrupa por conta pagadora e por cartão, inclusive o que ainda não tem pagadora', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(projection)))
    render(<CardPlanning workspaceId="w" />)
    await screen.findAllByRole('table')

    const [, payers, cards] = screen.getAllByRole('table')
    expect(within(payers).getByText('Itaú PF')).toBeTruthy()
    expect(within(payers).getByText('Sem conta pagadora')).toBeTruthy()
    expect(within(cards).getByText('Itaú Empresas Visa')).toBeTruthy()
    expect(within(cards).getAllByText('PJ').length).toBeGreaterThan(0)
  })

  it('troca o horizonte pelo backend', async () => {
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) => json(projection))
    vi.stubGlobal('fetch', fetchMock)
    render(<CardPlanning workspaceId="w" />)
    await screen.findAllByRole('table')

    fireEvent.change(screen.getByLabelText('Horizonte'), { target: { value: '12' } })

    await waitFor(() => expect(fetchMock.mock.calls.some(call => String(call[0]).includes('months=12'))).toBe(true))
  })
})

describe('CardInstallments', () => {
  const data: CardInstallmentsData = {
    cardId: 'c1', name: 'Cartão', today: '2026-10-29', coverage: complete,
    chains: [{
      key: 'k1', description: 'Sofá', total: 4, amount: '100.00', status: 'Estimated', reason: null, estimated: 1, unprojected: 0,
      installments: [
        { number: 1, total: 4, amount: '100.00', closing: '2026-10-08', due: '2026-10-15', origin: 'InInvoice', absorbed: false },
        { number: 2, total: 4, amount: '100.00', closing: '2026-11-08', due: '2026-11-15', origin: 'Known', absorbed: false },
        { number: 4, total: 4, amount: '100.00', closing: '2027-01-08', due: '2027-01-15', origin: 'Estimated', absorbed: false },
      ],
    }, {
      key: 'k2', description: 'Loja X', total: 5, amount: '40.00', status: 'Ambiguous', reason: 'ParallelIdenticalPurchases', estimated: 0, unprojected: 3, installments: [],
    }],
  }

  it('identifica as parcelas estimadas e avisa o que ficou sem projeção', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(data)))
    render(<CardInstallments workspaceId="w" cardId="c1" />)
    fireEvent.click(screen.getByText('Parcelas projetadas (informadas e estimadas)'))

    expect(await screen.findByText('Sofá')).toBeTruthy()
    expect(screen.getByText('Informada pelo banco')).toBeTruthy()
    const estimated = screen.getByText('Estimada').closest('tr')!
    expect(estimated.className).toBe('estimated')
    expect(text(estimated)).toContain('15/01/2027')
    expect(screen.getByText('Ambígua — revisar')).toBeTruthy()
    expect(screen.getByText(/3 parcela\(s\) restante\(s\) sem evidência para projetar/)).toBeTruthy()
  })
})

describe('FinancialCreditCardsPage — abas de visão e planejamento', () => {
  it('abre a visão geral e o planejamento sem perder o módulo de cartões', async () => {
    const cardRow = { id: 'c1', name: 'Platinum', issuer: 'Banco', lastFourDigits: '0012', holderUserId: null, creditLimit: '10000.00', closingDay: 10, dueDay: 20, defaultPaymentAccountId: null, isArchived: false, createdAt: '', updatedAt: '', archivedAt: null }
    const fetchMock = vi.fn((url: string) => {
      if (url.includes('/invoice-overview')) return json(overview([card({ name: 'Platinum' })]))
      if (url.includes('/obligation-projection')) return json({ from: '2026-10', months: 6, today: '2026-10-29', carryover: '0.00', coverage: complete, items: [] })
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: '0.00', availableLimit: '10000.00', overLimitAmount: '0.00' })
      if (url.includes('/projected-installments')) return json({ cardId: 'c1', name: 'Platinum', today: '2026-10-29', chains: [], coverage: complete })
      if (url.includes('/credit-cards?')) return json([cardRow])
      if (url.includes('/summary?')) return json({ cardCount: 1, totalLimit: '10000.00', invoiceTotal: '0.00', availableLimit: '10000.00', cards: [] })
      return json([])
    })
    vi.stubGlobal('fetch', fetchMock)
    render(<FinancialCreditCardsPage workspaceId="w" />)
    await screen.findAllByText('Platinum')

    fireEvent.click(screen.getByRole('button', { name: 'Visão geral' }))
    expect(await screen.findByRole('article', { name: 'Platinum' })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Planejamento' }))
    expect(await screen.findByRole('heading', { name: 'Planejamento dos cartões' })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Cartões' }))
    expect(await screen.findByText('Parcelas projetadas (informadas e estimadas)')).toBeTruthy()
  })
})
