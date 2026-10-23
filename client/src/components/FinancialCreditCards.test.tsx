import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FinancialCreditCardsPage } from './FinancialCreditCards'

afterEach(() => { cleanup(); vi.restoreAllMocks() })

const card = { id: 'card-1', name: 'Platinum', issuer: 'Banco', lastFourDigits: '0012', holderUserId: null, creditLimit: '10000.00', closingDay: 10, dueDay: 20, defaultPaymentAccountId: null, isArchived: false, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', archivedAt: null }
const json = (value: unknown) => Promise.resolve(new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json' } }))

describe('FinancialCreditCardsPage', () => {
  it('abre o cadastro a partir da importação e preserva a query Gmail ao editar', async () => {
    let saved: Record<string, unknown> | null = null
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (url === '/auth/csrf') return json({ token: 'synthetic' })
      if (url.endsWith(`/credit-cards/${card.id}`) && init?.method === 'PUT') { saved = JSON.parse(init.body as string); return json({ ...card, gmailQuery: saved?.gmailQuery }) }
      if (url.includes('/credit-cards?')) return json([card])
      if (url.includes('/summary?')) return json({ cardCount: 1, totalLimit: '10000.00', invoiceTotal: '0.00', availableLimit: '10000.00', cards: [] })
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: '0.00', availableLimit: '10000.00', overLimitAmount: '0.00' })
      return json([])
    }))
    render(<FinancialCreditCardsPage workspaceId="workspace" />)
    await screen.findAllByText('Platinum')
    fireEvent.click(screen.getByRole('button', { name: 'Importações' }))
    fireEvent.click(screen.getByRole('button', { name: 'Configurar no cadastro do cartão' }))
    fireEvent.change(screen.getByLabelText('Query Gmail (opcional)'), { target: { value: 'from:synthetic.invalid filename:pdf' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))
    await waitFor(() => expect(saved).toMatchObject({ gmailQuery: 'from:synthetic.invalid filename:pdf', lastFourDigits: '0012', creditLimit: '10000.00' }))
  })

  it('reutiliza o formulário na fatura e recarrega os detalhes após salvar', async () => {
    let saved = false
    let invoiceReads = 0
    const invoice = () => ({ id: 'invoice', creditCardId: card.id, competence: '2030-11', cycleStartDate: '2030-10-06', closingDate: '2030-11-05', dueDate: '2030-11-10', status: 'Open', totalAmount: saved ? '50.00' : '0.00', paidAmount: '0.00', remainingAmount: saved ? '50.00' : '0.00', paymentStatus: 'Unpaid', installments: [], installmentCount: 0 })
    const changed = vi.fn()
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (url === '/auth/csrf') return json({ token: 'test-token' })
      if (url.endsWith('/purchases/preview')) return json({ totalAmount: '50.00', installmentCount: 1, previouslyPaidInstallments: 0, remainingAmount: '50.00', installments: [{ number: 1, amount: '50.00', competence: '2030-11', dueDate: '2030-11-10' }] })
      if (url.endsWith('/purchases') && init?.method === 'POST') { saved = true; return Promise.resolve(new Response(JSON.stringify({ id: 'created' }), { status: 201 })) }
      if (url.includes('/credit-cards?')) return json([card])
      if (url.includes('/summary?')) return json({ cardCount: 1, totalLimit: '10000.00', invoiceTotal: saved ? '50.00' : '0.00', availableLimit: saved ? '9950.00' : '10000.00', cards: [] })
      if (url.includes('/categories?')) return json([{ id: 'expense', name: 'Compras', type: 'Expense', isActive: true, createdAt: '' }])
      if (url.includes('/invoices?')) { invoiceReads++; return json([invoice()]) }
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: saved ? '50.00' : '0.00', availableLimit: saved ? '9950.00' : '10000.00', overLimitAmount: '0.00' })
      return json([])
    }))
    render(<FinancialCreditCardsPage workspaceId="workspace" onChanged={changed} />)
    await screen.findAllByText('Platinum')
    fireEvent.change(screen.getByLabelText('Competência das faturas'), { target: { value: '2030-11' } })
    fireEvent.click(await screen.findByRole('button', { name: /2030-11.*Aberta/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar lançamento' }))
    expect((screen.getByLabelText('Primeira competência pendente (opcional para compra nova)') as HTMLInputElement).value).toBe('2030-11')
    fireEvent.change(screen.getByLabelText('Descrição'), { target: { value: 'Compra sintética' } })
    fireEvent.change(screen.getByLabelText('Valor total'), { target: { value: '50,00' } })
    fireEvent.change(screen.getByLabelText('Categoria'), { target: { value: 'expense' } })
    fireEvent.click(screen.getByRole('button', { name: 'Revisar resumo' }))
    await screen.findByRole('region', { name: 'Resumo da compra' })
    const readsBefore = invoiceReads
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar' }))
    await waitFor(() => expect(changed).toHaveBeenCalledOnce())
    await waitFor(() => expect(invoiceReads).toBeGreaterThan(readsBefore))
    await waitFor(() => expect(within(screen.getByLabelText('Resumo dos cartões')).getByText(/^R\$\s50,00$/)).toBeTruthy())
    expect(screen.queryByRole('button', { name: 'Revisar resumo' })).toBeNull()
  })

  it('não quebra a página nem inventa zeros quando o resumo falha', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string) => {
      if (url.includes('/credit-cards?')) return json([card])
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: '0.00', availableLimit: '10000.00', overLimitAmount: '0.00' })
      return json([])
    }))
    render(<FinancialCreditCardsPage workspaceId="workspace" />)
    await screen.findAllByText('Platinum')
    expect(await screen.findByText(/Resumo indisponível/)).toBeTruthy()
    expect(screen.queryByLabelText('Resumo dos cartões')).toBeNull()
  })

  it('lista cartões e preserva valores monetários como texto na exibição', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string) => {
      if (url.includes('/summary?')) return json({ cardCount: 1, totalLimit: '10000.00', invoiceTotal: '0.00', availableLimit: '10000.00', cards: [] })
      if (url.includes('/credit-cards?')) return json([card])
      if (url.endsWith('/accounts?includeInactive=true')) return json([])
      if (url.endsWith('/categories?includeInactive=true')) return json([])
      if (url.endsWith('/invoices?from=2026-10&through=2026-10')) return json([])
      if (url.endsWith('/purchases')) return json([])
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: '0.00', availableLimit: '10000.00', overLimitAmount: '0.00' })
      if (url.includes('/invoice-projection?')) return json([])
      return json([])
    }))
    render(<FinancialCreditCardsPage workspaceId="family-1" />)
    expect((await screen.findAllByText('Platinum')).length).toBeGreaterThan(0)
    await waitFor(() => expect(screen.getAllByText(/10\.000,00/).length).toBeGreaterThan(0))
    expect(screen.getAllByText(/•••• 0012/).length).toBeGreaterThan(0)
    expect(screen.getAllByText('Cartões').length).toBeGreaterThan(0)
    expect((await screen.findByLabelText('Resumo dos cartões')).textContent).toContain('Faturas da competência')
    expect(Array.from(screen.getByLabelText('Resumo dos cartões').querySelectorAll('dt')).map(d => d.textContent)).toEqual(['Faturas da competência', 'Pendente conhecido — inclui sem vínculo', 'Pagamentos registrados', 'Limite disponível — ativos'])
  })

  it('notifica a família após arquivar para invalidar Agenda, Contas e Orçamentos', async () => {
    const changed = vi.fn()
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') return Promise.resolve(new Response(null, { status: 204 }))
      if (url.includes('/summary?')) return json({ cardCount: 1, totalLimit: '10000.00', invoiceTotal: '0.00', availableLimit: '10000.00', cards: [] })
      if (url.includes('/credit-cards?')) return json([card])
      if (url.endsWith('/accounts?includeInactive=true')) return json([])
      if (url.endsWith('/categories?includeInactive=true')) return json([])
      if (url.endsWith('/invoices?from=2026-10&through=2026-10')) return json([])
      if (url.endsWith('/purchases')) return json([])
      if (url.endsWith('/limit')) return json({ creditLimit: '10000.00', usedLimit: '0.00', availableLimit: '10000.00', overLimitAmount: '0.00' })
      if (url.includes('/invoice-projection?')) return json([])
      return json([])
    }))
    vi.stubGlobal('confirm', vi.fn(() => true))
    render(<FinancialCreditCardsPage workspaceId="family-1" onChanged={changed} />)
    await screen.findAllByText('Platinum')
    fireEvent.click(screen.getByRole('button', { name: 'Arquivar' }))
    await waitFor(() => expect(changed).toHaveBeenCalled())
  })
})
