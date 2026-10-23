import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CreditCardPurchaseForm } from './CreditCardPurchaseForm'
import { createPurchase, previewPurchase, updatePurchase, type CreditCardPurchase, type FinancialCreditCard, type PurchasePreview } from '../api/creditCards'

vi.mock('../api/creditCards', async importOriginal => ({ ...await importOriginal<typeof import('../api/creditCards')>(), createPurchase: vi.fn(), previewPurchase: vi.fn(), updatePurchase: vi.fn() }))
afterEach(() => cleanup())
beforeEach(() => vi.resetAllMocks())
const cards: FinancialCreditCard[] = ['card-1', 'card-2'].map(id => ({ id, name: id, issuer: 'Banco', creditLimit: '10000.00', closingDay: 5, dueDay: 10, lastFourDigits: null, holderUserId: null, defaultPaymentAccountId: null, isArchived: false, createdAt: '', updatedAt: '', archivedAt: null }))
const categories = [{ id: 'expense', name: 'Compras', type: 'Expense' as const, isActive: true, createdAt: '' }, { id: 'income', name: 'Salário', type: 'Income' as const, isActive: true, createdAt: '' }]
const preview: PurchasePreview = { totalAmount: '1000.00', installmentCount: 4, previouslyPaidInstallments: 2, remainingAmount: '500.00', installments: [{ number: 3, amount: '250.00', competence: '2026-11', dueDate: '2026-11-10' }, { number: 4, amount: '250.00', competence: '2026-12', dueDate: '2026-12-10' }] }
const purchase: CreditCardPurchase = { id: 'purchase', creditCardId: 'card-1', categoryId: 'expense', description: 'Compra antiga', purchaseDate: '2026-03-26', totalAmount: '1000.00', installmentCount: 4, status: 'Active', installments: [], previouslyPaidInstallments: 2, firstPendingCompetence: '2026-11-01' }
const field = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } })
const show = (p?: CreditCardPurchase, competence?: string) => { const done = vi.fn(); render(<CreditCardPurchaseForm workspaceId="workspace" cards={cards} cardId="card-1" categories={categories} purchase={p} competence={competence} onDone={done} onCancel={vi.fn()} />); return done }
const fill = () => { field('Descrição', 'Compra nova'); field('Data da compra', '2026-03-26'); field('Valor total', '1000,00'); field('Categoria', 'expense'); field('Tipo', 'installments'); field('Número de parcelas', '4'); field('Parcelas já pagas', '2'); field('Primeira competência pendente (opcional para compra nova)', '2026-11') }
const review = async () => { vi.mocked(previewPurchase).mockResolvedValue({ ok: true, value: preview }); fireEvent.click(screen.getByRole('button', { name: 'Revisar resumo' })); await screen.findByRole('region', { name: 'Resumo da compra' }) }

describe('CreditCardPurchaseForm', () => {
  it('exige resumo, envia o valor decimal como texto e cria somente após revisão', async () => {
    const done = show(); fill()
    expect(screen.getByRole('button', { name: 'Adicionar' }).hasAttribute('disabled')).toBe(true)
    await review()
    expect(screen.getByText(/Primeira pendente: 3\/4/)).toBeTruthy()
    expect(screen.getAllByText(/250,00/)).toHaveLength(2)
    expect(screen.getByText(/Restam 2 parcelas/)).toBeTruthy()
    vi.mocked(createPurchase).mockResolvedValue({ ok: true, value: purchase })
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar' }))
    await waitFor(() => expect(done).toHaveBeenCalledOnce())
    expect(createPurchase).toHaveBeenCalledWith('workspace', 'card-1', expect.objectContaining({ totalAmount: '1000.00', installmentCount: 4, previouslyPaidInstallments: 2, firstPendingCompetence: '2026-11-01' }))
  })

  it('deriva seis pagas de 7/10 e troca cartão e modo de valor sem calcular dinheiro no cliente', async () => {
    show(); fill(); field('Cartão', 'card-2'); field('Modo de entrada do valor', 'Installment'); field('Valor por parcela', '320,71'); field('Numeração', 'current'); field('Primeira parcela pendente', '7/10')
    await review()
    expect(previewPurchase).toHaveBeenCalledWith('workspace', 'card-2', expect.objectContaining({ amountMode: 'Installment', totalAmount: '320.71', installmentCount: 10, previouslyPaidInstallments: 6 }))
    expect(screen.queryByLabelText('Parcelas já pagas')).toBeNull()
    expect(screen.queryByLabelText('Número de parcelas')).toBeNull()
  })

  it('invalida o resumo ao alterar campos e não permite salvar um cálculo antigo', async () => {
    show(); fill(); await review(); field('Valor total', '2000,00')
    expect(screen.queryByRole('region', { name: 'Resumo da compra' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Adicionar' }).hasAttribute('disabled')).toBe(true)
    expect(createPurchase).not.toHaveBeenCalled()
  })

  it('edita a mesma compra com os dados preenchidos e cartão imutável', async () => {
    const done = show(purchase)
    expect((screen.getByLabelText('Cartão') as HTMLSelectElement).disabled).toBe(true)
    expect((screen.getByLabelText('Parcelas já pagas') as HTMLInputElement).value).toBe('2')
    field('Descrição', 'Corrigida'); await review()
    vi.mocked(updatePurchase).mockResolvedValue({ ok: true, value: purchase })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar alterações' }))
    await waitFor(() => expect(done).toHaveBeenCalledOnce())
    expect(updatePurchase).toHaveBeenCalledWith('workspace', 'card-1', 'purchase', expect.objectContaining({ description: 'Corrigida' }))
    expect(createPurchase).not.toHaveBeenCalled()
  })

  it('mantém aberto e apresenta conflito sem repetir a operação', async () => {
    const done = show(purchase); await review()
    vi.mocked(updatePurchase).mockResolvedValue({ ok: false, errors: { purchase: ['Histórico protegido.'] } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar alterações' }))
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Histórico protegido.')
    expect(done).not.toHaveBeenCalled(); expect(updatePurchase).toHaveBeenCalledOnce()
  })

  it('rejeita parcelas inválidas e exige competência quando há histórico pago', async () => {
    show(); fill(); field('Primeira competência pendente (opcional para compra nova)', '')
    fireEvent.click(screen.getByRole('button', { name: 'Revisar resumo' }))
    await screen.findByText('Informe a primeira competência pendente.')
    expect(previewPurchase).not.toHaveBeenCalled()
    field('Numeração', 'current'); field('Primeira parcela pendente', '11/10')
    fireEvent.click(screen.getByRole('button', { name: 'Revisar resumo' }))
    await screen.findByText(/Informe parcelas válidas/)
    expect(previewPurchase).not.toHaveBeenCalled()
  })

  it('usa competência preselecionada da fatura e tipo à vista sem campos contraditórios', async () => {
    show(undefined, '2026-11'); field('Descrição', 'À vista'); field('Valor total', '50,00'); field('Categoria', 'expense')
    expect((screen.getByLabelText('Primeira competência pendente (opcional para compra nova)') as HTMLInputElement).value).toBe('2026-11')
    expect(screen.queryByLabelText('Parcelas já pagas')).toBeNull()
    await review()
    expect(previewPurchase).toHaveBeenCalledWith('workspace', 'card-1', expect.objectContaining({ installmentCount: 1, previouslyPaidInstallments: 0, firstPendingCompetence: '2026-11-01' }))
    expect(screen.queryByRole('option', { name: 'Salário' })).toBeNull()
  })

  it('apresenta erro de preview e bloqueia duplo envio enquanto a resposta está pendente', async () => {
    show(); fill()
    let finish!: (value: Awaited<ReturnType<typeof previewPurchase>>) => void
    vi.mocked(previewPurchase).mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const button = screen.getByRole('button', { name: 'Revisar resumo' })
    fireEvent.click(button); fireEvent.click(button)
    expect(previewPurchase).toHaveBeenCalledOnce()
    finish({ ok: false, errors: { purchase: ['Valor inválido.'] } })
    await screen.findByText('Valor inválido.')
    expect(screen.getByRole('button', { name: 'Adicionar' }).hasAttribute('disabled')).toBe(true)
  })
})
