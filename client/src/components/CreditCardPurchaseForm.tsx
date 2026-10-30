import { useRef, useState, type FormEvent } from 'react'
import { createPurchase, previewPurchase, updatePurchase, type CreditCardPurchase, type FinancialCreditCard, type PurchaseInput, type PurchasePreview } from '../api/creditCards'
import type { FinancialCategory } from '../api/categories'
import { todayInSaoPaulo } from '../dates'
import { formatBrlDecimal, parseAmount } from '../money'
import { OperationalError } from './OperationalFeedback'
import type { ExpenseNature } from '../api/expenseNature'
import { CategoryOptions } from './CategoryOptions'
import { ExpenseNatureField } from './ExpenseNatureField'

export function CreditCardPurchaseForm({ workspaceId, cards, cardId, categories, purchase, competence, onDone, onCancel }: {
  workspaceId: string; cards: FinancialCreditCard[]; cardId: string; categories: FinancialCategory[]; purchase?: CreditCardPurchase; competence?: string; onDone: (cardId: string) => void; onCancel: () => void
}) {
  const [selected, setSelected] = useState(cardId)
  const [description, setDescription] = useState(purchase?.description ?? '')
  const [date, setDate] = useState(purchase?.purchaseDate ?? todayInSaoPaulo())
  const [amount, setAmount] = useState(purchase?.totalAmount.replace('.', ',') ?? '')
  const [amountMode, setAmountMode] = useState<'Total' | 'Installment'>('Total')
  const [kind, setKind] = useState((purchase?.installmentCount ?? 1) === 1 ? 'single' : 'installments')
  const [count, setCount] = useState(String(purchase?.installmentCount ?? 1))
  const [paid, setPaid] = useState(String(purchase?.previouslyPaidInstallments ?? 0))
  const [numberMode, setNumberMode] = useState('paid')
  const [fraction, setFraction] = useState('')
  const [firstMonth, setFirstMonth] = useState(purchase?.firstPendingCompetence?.slice(0, 7) ?? competence ?? '')
  const [category, setCategory] = useState(purchase?.categoryId ?? '')
  const [expenseNature, setExpenseNature] = useState<ExpenseNature>(purchase?.expenseNature ?? 'Undefined')
  const [preview, setPreview] = useState<{ key: string; data: PurchasePreview } | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const inFlight = useRef(false)
  const key = JSON.stringify([selected, description, date, amount, amountMode, count, paid, numberMode, fraction, firstMonth, category, expenseNature])
  const current = preview?.key === key ? preview.data : null
  const input = (): PurchaseInput => {
    const parsed = parseAmount(amount, 'O valor')
    if (parsed.kind !== 'ok') throw new Error(parsed.kind === 'invalid' ? parsed.message : 'Informe o valor.')
    const match = /^(\d+)\/(\d+)$/.exec(fraction.trim())
    const n = numberMode === 'current' ? Number(match?.[2]) : Number(count)
    const already = numberMode === 'current' ? Number(match?.[1]) - 1 : Number(paid)
    if (!Number.isInteger(n) || n < 1 || n > 120 || !Number.isInteger(already) || already < 0 || already >= n) throw new Error('Informe parcelas válidas, como 7/10, ou quantidade paga entre zero e N-1.')
    if (kind === 'single' && n !== 1 || kind === 'installments' && n < 2) throw new Error('Confira o tipo e a quantidade de parcelas.')
    if (!description.trim() || !date || !category || !selected) throw new Error('Informe cartão, descrição, data e categoria.')
    if (already > 0 && !firstMonth) throw new Error('Informe a primeira competência pendente.')
    return { description, purchaseDate: date, totalAmount: parsed.text, installmentCount: n, categoryId: category, amountMode, previouslyPaidInstallments: already, firstPendingCompetence: firstMonth ? `${firstMonth}-01` : null, expenseNature }
  }
  const run = async (save: boolean) => {
    if (inFlight.current) return
    inFlight.current = true; setBusy(true); setError(null)
    try {
      const data = input()
      if (save && !current) throw new Error('Revise o resumo antes de salvar.')
      if (save) {
        const result = purchase ? await updatePurchase(workspaceId, selected, purchase.id, data) : await createPurchase(workspaceId, selected, data)
        if (result.ok) onDone(selected); else setError(Object.values(result.errors).flat().join(' '))
      } else {
        const result = await previewPurchase(workspaceId, selected, data)
        if (result.ok) setPreview({ key, data: result.value }); else setError(Object.values(result.errors).flat().join(' '))
      }
    } catch (cause) { setError(cause instanceof Error ? cause.message : cause) } finally { inFlight.current = false; setBusy(false) }
  }
  const submit = (event: FormEvent) => { event.preventDefault(); void run(true) }
  return <form className="form credit-card-form" onSubmit={submit}>
    <h5>{purchase ? 'Editar compra' : 'Nova compra'}</h5>
    <label>Cartão<select value={selected} disabled={!!purchase || busy} onChange={e => setSelected(e.target.value)}>{cards.filter(c => !c.isArchived || c.id === selected).map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
    {purchase && <p className="meta">A edição preserva a identidade e o cartão da compra. Histórico quitado ou consolidado é protegido.</p>}
    <label>Descrição<input value={description} maxLength={200} onChange={e => setDescription(e.target.value)} required /></label>
    <label>Data da compra<input type="date" value={date} onChange={e => setDate(e.target.value)} required /></label>
    <label>Tipo<select value={kind} onChange={e => { const value = e.target.value; setKind(value); setNumberMode('paid'); setCount(value === 'single' ? '1' : '2'); setPaid('0') }}><option value="single">À vista</option><option value="installments">Parcelada</option></select></label>
    <label>Modo de entrada do valor<select value={amountMode} onChange={e => setAmountMode(e.target.value as 'Total' | 'Installment')}><option value="Total">Total da compra</option><option value="Installment">Valor de cada parcela</option></select></label>
    <label>{amountMode === 'Total' ? 'Valor total' : 'Valor por parcela'}<input inputMode="decimal" value={amount} onChange={e => setAmount(e.target.value)} required /></label>
    {kind === 'installments' && <><label>Numeração<select value={numberMode} onChange={e => setNumberMode(e.target.value)}><option value="paid">Informar quantidade já paga</option><option value="current">Informar primeira parcela pendente (7/10)</option></select></label>
    {numberMode === 'paid' ? <div className="form-row"><label>Número de parcelas<input type="number" min="2" max="120" value={count} onChange={e => { setCount(e.target.value); setPaid('0') }} /></label><label>Parcelas já pagas<input type="number" min="0" max={Number(count) - 1} value={paid} onChange={e => setPaid(e.target.value)} /></label></div> : <label>Primeira parcela pendente<input placeholder="7/10" value={fraction} onChange={e => setFraction(e.target.value)} /></label>}</>}
    <p className="meta">Uma parcela significa compra à vista. Parcelas anteriores pagas não geram pagamentos nem movimentos bancários.</p>
    <label>Primeira competência pendente (opcional para compra nova)<input type="month" value={firstMonth} onChange={e => setFirstMonth(e.target.value)} /></label>
    <label>Categoria<select value={category} onChange={e => setCategory(e.target.value)} required><option value="">Selecionar categoria</option><CategoryOptions categories={categories} type="Expense" keepId={purchase?.categoryId} /></select></label>
    <ExpenseNatureField id="purchase-expense-nature" value={expenseNature} onChange={setExpenseNature} disabled={busy} />
    <p className="meta">Cartão e parcelas não definem o tipo de despesa: ele só muda quando você escolhe.</p>
    <button type="button" disabled={busy} onClick={() => void run(false)}>Revisar resumo</button>
    {current && <section aria-label="Resumo da compra" className="purchase-preview"><h6>Resumo calculado pelo servidor</h6><p>Total original: <strong>{formatBrlDecimal(current.totalAmount)}</strong></p><p>Já pagas: {current.previouslyPaidInstallments} · Primeira pendente: {current.previouslyPaidInstallments + 1}/{current.installmentCount}</p><p>Restam {current.installments.length} parcelas · Valor restante: <strong>{formatBrlDecimal(current.remainingAmount)}</strong></p><ul>{current.installments.map(i => <li key={i.number}>{i.number}/{current.installmentCount} · {i.competence} · vence {i.dueDate} · {formatBrlDecimal(i.amount)}</li>)}</ul></section>}
    {error != null && <OperationalError error={error} />}
    <div className="form__actions"><button className="button--primary" disabled={busy || !current}>{busy ? 'Processando…' : purchase ? 'Salvar alterações' : 'Adicionar'}</button><button type="button" disabled={busy} onClick={onCancel}>Cancelar</button></div>
  </form>
}
