import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import { Accounts } from './Accounts'

const api = vi.hoisted(() => ({
  isPluggyIntegrationAvailable: vi.fn(), listIntegrationConnections: vi.fn(), listAvailableAccounts: vi.fn(), linkAccount: vi.fn(), getLinkDetail: vi.fn(),
  applyOpeningBalanceSuggestion: vi.fn(), reprocessImported: vi.fn(), listReview: vi.fn(), resolveReview: vi.fn(), listLinks: vi.fn(),
}))
vi.mock('../api/pluggyIntegration', () => api)

const manual: FinancialAccount = {
  id: 'a1', name: 'Carteira', institution: null, type: 'Cash', financialScope: 'PF', responsible: null, openingBalance: null, openingBalanceDate: null, isActive: true,
  currency: 'BRL', createdAt: '2026-10-10T00:00:00Z', origin: 'Manual', integration: null,
}
const integrated: FinancialAccount = {
  ...manual, id: 'a2', name: 'Itaú corrente', institution: 'Itaú', type: 'Checking', origin: 'Pluggy',
  integration: { linkId: 'l1', connectionId: 'c1', displayName: 'Itaú · Conta corrente ••••6789', institutionName: 'Itaú', status: 'Active', lastSyncedAt: '2026-10-28T11:30:00Z', importFrom: null, observedBalance: '1500.75', observedAt: '2026-10-28T10:00:00Z' },
}

function mockFetch() {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
    if (/\/balance$/.test(url)) return Response.json({ accountId: 'x', asOf: '2026-10-28', openingBalance: null, openingBalanceDate: null, totalIncome: null, totalExpense: null, totalTransferIn: null, totalTransferOut: null, balance: null, unavailableReason: 'OpeningBalanceNotConfigured' })
    if (/\/projection\?/.test(url)) return Response.json({ accountId: 'x', baseDate: '2026-10-28', through: '2026-10-31', realizedBalance: null, overdueIncome: null, overdueExpense: null, plannedIncome: null, plannedExpense: null, projectedBalance: null, unavailableReason: 'OpeningBalanceNotConfigured' })
    return Response.json([manual, integrated])
  }))
}

beforeEach(() => {
  vi.clearAllMocks()
  mockFetch()
  api.isPluggyIntegrationAvailable.mockResolvedValue(true)
  api.listLinks.mockResolvedValue([])
  api.listIntegrationConnections.mockResolvedValue([])
  api.listReview.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25, counts: { applied: 0, waiting: 0, needsReview: 0, covered: 0, ignored: 0 } })
  api.getLinkDetail.mockResolvedValue({
    link: { id: 'l1', connectionId: 'c1', origin: 'Pluggy', kind: 'Bank', financialAccountId: 'a2', creditCardId: null, displayName: 'Itaú · Conta corrente ••••6789', institutionName: 'Itaú', pluggyType: 'BANK',
      pluggySubtype: 'CHECKING_ACCOUNT', status: 'Active', importFrom: null, linkedAt: '2026-10-28T10:00:00Z', lastSyncedAt: '2026-10-28T11:30:00Z',
      observed: { balance: '1500.75', observedAt: null, overdraftContractedLimit: null, overdraftUsedLimit: null, creditLimit: null, availableCreditLimit: null }, postings: { applied: 0, waiting: 0, needsReview: 0, covered: 0, ignored: 0 } },
    reconciliation: { observedBalance: '1500.75', observedAt: null, calculatedBalance: null, calculatedAsOf: null, difference: null, calculatedUnavailableReason: 'OpeningBalanceNotConfigured', reviewCount: 0, waitingCount: 0,
      suggestedOpeningBalance: null, suggestedOpeningBalanceDate: null, suggestionUnavailableReason: 'NeverSynced', card: null, notes: [] },
  })
})
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('Accounts — origem Pluggy', () => {
  it('mostra a origem de cada conta e o painel de integração apenas nas contas integradas', async () => {
    render(<Accounts workspaceId="w1" members={[]} />)

    const manualItem = (await screen.findByText('Carteira', { selector: '.account__name' })).closest('li')!
    const pluggyItem = screen.getByText('Itaú corrente', { selector: '.account__name' }).closest('li')!
    expect(within(manualItem).getByText('Origem: Manual')).toBeTruthy()
    expect(within(manualItem).queryByLabelText(/Integração/)).toBeNull()
    expect(within(pluggyItem).getByText('Origem: Pluggy')).toBeTruthy()
    expect(await within(pluggyItem).findByLabelText(/Integração Itaú · Conta corrente/)).toBeTruthy()
    expect(within(pluggyItem).getByText(/Saldo informado pelo banco/)).toBeTruthy()
  })

  it('"Nova conta" oferece a origem Pluggy e abre o fluxo de seleção da conta do banco; manual continua disponível', async () => {
    render(<Accounts workspaceId="w1" members={[]} />)
    await screen.findByText('Carteira', { selector: '.account__name' })
    await screen.findAllByText('Origem: Pluggy')

    fireEvent.click(screen.getByRole('button', { name: 'Nova conta' }))
    const manualOrigin = await screen.findByRole('radio', { name: /Manual/ })
    expect((manualOrigin as HTMLInputElement).checked).toBe(true)
    expect(screen.getByLabelText('Nome')).toBeTruthy() // formulário manual preservado

    fireEvent.click(screen.getByRole('radio', { name: /Pluggy — integrar/ }))

    expect(await screen.findByText('Nova conta integrada (Pluggy)')).toBeTruthy()
    expect(screen.getByLabelText('Conexão (Item)')).toBeTruthy()
    expect(screen.queryByLabelText('Nome')).toBeNull()
  })

  it('sem integração habilitada não há escolha de origem: só o cadastro manual', async () => {
    api.isPluggyIntegrationAvailable.mockResolvedValue(false)
    render(<Accounts workspaceId="w1" members={[]} />)
    await screen.findByText('Carteira', { selector: '.account__name' })

    fireEvent.click(screen.getByRole('button', { name: 'Nova conta' }))

    expect(await screen.findByLabelText('Nome')).toBeTruthy()
    expect(screen.queryByRole('radio', { name: /Pluggy/ })).toBeNull()
  })
})
