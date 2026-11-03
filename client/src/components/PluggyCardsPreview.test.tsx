import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AvailableAccount, CardPreview } from '../api/pluggyIntegration'
import { PluggyCardsPreview } from './PluggyCardsPreview'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')

const card = (id: string, name: string, over: Partial<AvailableAccount> = {}): AvailableAccount => ({
  pluggyAccountId: id, kind: 'Card', displayName: name, maskedNumber: '••••1234', pluggyType: 'CREDIT', pluggySubtype: 'CREDIT_CARD', suggestedAccountType: null, subtypeRecognized: true,
  balance: null, currency: 'BRL', card: null, linkedLinkId: null, linkable: true, notLinkableReason: null, ...over,
})
const bank = card('bank-1', 'Conta corrente', { kind: 'Bank' })
const connection = { id: 'c1', label: null, itemReference: 'item', institution: 'Banco Teste', institutionType: null, isOpenFinance: true, state: 'Healthy', status: null, executionStatus: null, lastUpdatedAt: null, consentExpiresAt: null, errorCode: null, errorMessage: null, integrationError: null, createdAt: '2026-01-01T00:00:00Z' }
const preview = (over: Partial<CardPreview> = {}): CardPreview => ({
  accountName: 'Cartão Ouro', brand: 'VISA', creditLimit: '1000.00', availableLimit: '400.00', usedLimit: '600.00', dueDay: 13, billsRead: 2, lastBillDue: '2026-10-13', lastBillTotal: '250.00',
  transactionsRead: 5, posted: 1, pending: 4, withInstallments: 2, bills: [], upcoming: [{ month: '2026-11', entries: 2, installments: 1, amount: '120.00' }], flags: ['AwaitingBankBill'], cutoff: null, ...over,
})

const stub = (accounts: AvailableAccount[], previewBody: CardPreview = preview()) => {
  const fetchMock = vi.fn((input: string, _init?: RequestInit) => {
    const path = new URL(String(input), 'http://x').pathname
    if (path.endsWith('/card-preview')) return json(previewBody)
    if (path.endsWith('/connections/c1/accounts')) return json(accounts)
    if (path.endsWith('/connections')) return json([connection])
    return json({}, 404)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('PluggyCardsPreview', () => {
  it('lista só cartões e distingue disponível, já vinculado e indisponível, sem oferecer vínculo', async () => {
    const fetchMock = stub([bank, card('a', 'Cartão A'), card('b', 'Cartão B', { linkable: false, notLinkableReason: 'AlreadyLinked' }), card('c', 'Cartão C', { linkable: false, notLinkableReason: 'UnsupportedType' })])
    render(<PluggyCardsPreview workspaceId="w" />)

    const items = await screen.findAllByRole('listitem')
    expect(items).toHaveLength(3)
    expect(text(items[0])).toContain('Disponível para vincular')
    expect(text(items[1])).toContain('Já vinculado ao Saldo360')
    expect(text(items[2])).toContain('Indisponível para integração')
    expect(text(items[0])).toContain('Banco Teste')
    expect(text(items[0])).toContain('final 1234')
    expect(screen.queryByText('Conta corrente')).toBeNull()
    expect(screen.queryByRole('button', { name: /vincular/i })).toBeNull()
    expect(fetchMock.mock.calls.every(([, init]) => !(init as RequestInit | undefined)?.method || (init as RequestInit).method === 'GET')).toBe(true)
  })

  it('a prévia só é consultada ao pedir e mostra o que o banco informou e o que não veio', async () => {
    const fetchMock = stub([card('a', 'Cartão A')], preview({ creditLimit: null, usedLimit: null, availableLimit: null, dueDay: null, lastBillDue: null, lastBillTotal: null, upcoming: [], flags: ['NoClosingDate'] }))
    render(<PluggyCardsPreview workspaceId="w" />)
    await screen.findByRole('button', { name: 'Ver prévia do cartão' })
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/card-preview'))).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Ver prévia do cartão' }))
    const note = await screen.findByLabelText('Pontos de atenção do cartão')
    expect(text(note)).toContain('não informou a data de fechamento')
    expect(text(screen.getByText('Limite').parentElement)).toContain('Não informado pelo banco')
    expect(text(screen.getByText('Última fatura').parentElement)).toContain('Nenhuma devolvida')
    expect(screen.getByText(/Isso não significa gasto zero/)).toBeTruthy()
  })

  it('mostra limite, última fatura e cobranças futuras conhecidas, sem apresentá-las como fatura', async () => {
    stub([card('a', 'Cartão A')])
    render(<PluggyCardsPreview workspaceId="w" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Ver prévia do cartão' }))

    const table = await screen.findByRole('table')
    expect(text(table.querySelector('caption'))).toContain('não é a fatura nem a meta')
    expect(text(within(table).getByRole('row', { name: /novembro/i }))).toContain('R$ 120,00')
    expect(text(screen.getByText('Limite').parentElement)).toContain('R$ 1.000,00')
    expect(text(screen.getByText('Última fatura').parentElement)).toContain('R$ 250,00')
  })

  it('sem cartões identificados, diz isso em vez de mostrar uma lista vazia', async () => {
    stub([bank])
    render(<PluggyCardsPreview workspaceId="w" />)
    expect(await screen.findByText(/Nenhum cartão de crédito foi identificado/)).toBeTruthy()
  })

  it('propõe 01/10/2026 como corte, envia a data na prévia e mostra o impacto sem ampliar o histórico', async () => {
    const cutoff = { importFrom: '2026-10-01', excludedTransactions: 120, importedTransactions: 30, installmentSeriesStartedBefore: 4, installmentsInsideWindow: 11,
      billsSpanningCutoff: [{ dueDate: '2026-10-15', bankTotal: '6806.73', fromCutoff: '5000.00', beforeCutoff: '1806.73' }] }
    const fetchMock = stub([card('a', 'Cartão A')], preview({ cutoff, flags: ['HistoryBeforeCutoffExcluded', 'InstallmentsFromBeforeCutoff', 'BillsSpanCutoff'] }))
    render(<PluggyCardsPreview workspaceId="w" />)

    expect((await screen.findByLabelText('Data inicial de importação (proposta)') as HTMLInputElement).value).toBe('2026-10-01')
    fireEvent.click(await screen.findByRole('button', { name: 'Ver prévia do cartão' }))

    const impact = await screen.findByLabelText('Impacto da data de corte')
    expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/card-preview?importFrom=2026-10-01'))).toBe(true)
    expect(text(impact)).toContain('120 anterior(es) ficariam de fora')
    expect(text(impact)).toContain('4 compra(s) parcelada(s) feita(s) antes do corte têm 11 parcela(s) dentro da janela')
    expect(text(within(impact).getByRole('row', { name: /15\/10\/2026/ }))).toContain('R$ 1.806,73')
    expect(text(screen.getByLabelText('Pontos de atenção do cartão'))).toContain('Considere um corte anterior')
  })
})
