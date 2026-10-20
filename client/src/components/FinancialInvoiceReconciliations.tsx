import { useEffect, useMemo, useState } from 'react'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import {
  listCreditCards,
  listInvoices,
  type CreditCardInvoice,
  type FinancialCreditCard,
} from '../api/creditCards'
import {
  createInvoiceReconciliation,
  listInvoiceReconciliations,
  removeInvoiceReconciliation,
  type FinancialInvoiceReconciliation,
} from '../api/invoiceReconciliations'
import { listPlanned, type PlannedTransaction } from '../api/planned'
import { formatBrlDecimal } from '../money'
import { normalizeRequestError } from '../api/apiErrors'
import { OperationalError, OperationalLoading } from './OperationalFeedback'

const firstError = (errors: Record<string, string[]>) => Object.values(errors).flat()[0] ?? 'Verifique os dados informados.'

type ReconciliationView = {
  link: FinancialInvoiceReconciliation
  planned: PlannedTransaction
  invoice: CreditCardInvoice
  card: FinancialCreditCard
  account: FinancialAccount | null
}

type LoadedData = {
  links: FinancialInvoiceReconciliation[]
  planned: PlannedTransaction[]
  invoices: CreditCardInvoice[]
  cards: FinancialCreditCard[]
  accounts: FinancialAccount[]
}

async function loadAllPending(workspaceId: string, signal: AbortSignal): Promise<PlannedTransaction[]> {
  const result: PlannedTransaction[] = []
  let page = 1
  let total = 0
  do {
    const loaded = await listPlanned(workspaceId, {
      from: '', to: '', accountId: '', categoryId: '', type: 'Expense', includeRealized: false, includeCancelled: false,
    }, page, signal, 100)
    result.push(...loaded.items)
    total = loaded.totalCount
    page += 1
  } while (result.length < total)
  return result
}

async function loadData(workspaceId: string, signal: AbortSignal): Promise<LoadedData> {
  const [links, planned, cards, accounts] = await Promise.all([
    listInvoiceReconciliations(workspaceId, signal),
    loadAllPending(workspaceId, signal),
    listCreditCards(workspaceId, true, signal),
    listAccounts(workspaceId, true, signal),
  ])
  const invoiceLists = await Promise.all(cards.map((card) => listInvoices(workspaceId, card.id, '2000-01', '2100-12', signal)))
  return { links, planned, invoices: invoiceLists.flat(), cards, accounts }
}

export function FinancialInvoiceReconciliations({ workspaceId, refreshKey = 0, onChanged }: { workspaceId: string; refreshKey?: number; onChanged?: () => void }) {
  const [data, setData] = useState<LoadedData | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const [selectedPlannedId, setSelectedPlannedId] = useState('')
  const [selectedInvoiceId, setSelectedInvoiceId] = useState('')
  const [review, setReview] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    setData(null)
    setError(null)
    loadData(workspaceId, controller.signal)
      .then(setData)
      .catch((failure: unknown) => { if (!controller.signal.aborted) setError(failure) })
    return () => controller.abort()
  }, [workspaceId, refreshKey])

  const activeLinks = useMemo(() => data?.links.filter((item) => item.removedAt === null) ?? [], [data])
  const removedLinks = useMemo(() => data?.links.filter((item) => item.removedAt !== null) ?? [], [data])
  const linkedPlanned = useMemo(() => new Set(activeLinks.map((item) => item.plannedTransactionId)), [activeLinks])
  const linkedInvoices = useMemo(() => new Set(activeLinks.map((item) => item.creditCardInvoiceId)), [activeLinks])
  const selectedPlanned = data?.planned.find((item) => item.id === selectedPlannedId) ?? null
  const eligibleInvoices = useMemo(() => {
    if (!data) return []
    return data.invoices.filter((invoice) => invoice.remainingAmount !== '0.00' && !linkedInvoices.has(invoice.id))
  }, [data, linkedInvoices])
  const selectedInvoice = data?.invoices.find((item) => item.id === selectedInvoiceId) ?? null
  const selectedCard = selectedInvoice && data ? data.cards.find((item) => item.id === selectedInvoice.creditCardId) ?? null : null
  const compatible = Boolean(selectedPlanned && selectedCard?.defaultPaymentAccountId && selectedPlanned.account.id === selectedCard.defaultPaymentAccountId)

  const views = useMemo<ReconciliationView[]>(() => {
    if (!data) return []
    return activeLinks.flatMap((link) => {
      const planned = data.planned.find((item) => item.id === link.plannedTransactionId)
      const invoice = data.invoices.find((item) => item.id === link.creditCardInvoiceId)
      const card = invoice && data.cards.find((item) => item.id === invoice.creditCardId)
      if (!planned || !invoice || !card) return []
      return [{ link, planned, invoice, card, account: data.accounts.find((item) => item.id === card.defaultPaymentAccountId) ?? null }]
    })
  }, [activeLinks, data])

  const refresh = () => onChanged?.()
  const create = async () => {
    if (!selectedPlanned || !selectedInvoice || !compatible) return
    setBusy(true); setError(null)
    try {
      const result = await createInvoiceReconciliation(workspaceId, { plannedTransactionId: selectedPlanned.id, creditCardInvoiceId: selectedInvoice.id })
      if (!result.ok) { setError(firstError(result.errors)); return }
      setSelectedPlannedId(''); setSelectedInvoiceId(''); setReview(false); refresh()
    } catch (failure) {
      if (normalizeRequestError(failure).resultUncertain) {
        try {
          const links = await listInvoiceReconciliations(workspaceId)
          if (links.some((link) => link.removedAt === null && link.plannedTransactionId === selectedPlanned.id && link.creditCardInvoiceId === selectedInvoice.id)) {
            setSelectedPlannedId(''); setSelectedInvoiceId(''); setReview(false); refresh(); return
          }
        } catch { /* Sem confirmação, mantém a seleção para consulta ou reenvio manual. */ }
      }
      setError(failure)
    } finally { setBusy(false) }
  }

  const remove = async (view: ReconciliationView) => {
    if (!window.confirm(`Desfazer o vínculo entre "${view.planned.description}" e a fatura ${view.invoice.competence}? A previsão voltará a participar separadamente das projeções.`)) return
    setBusy(true); setError(null)
    try {
      const result = await removeInvoiceReconciliation(workspaceId, view.link.id)
      if (!result.ok) { setError(result.message); return }
      refresh()
    } catch (failure) {
      if (normalizeRequestError(failure).resultUncertain) {
        try {
          const links = await listInvoiceReconciliations(workspaceId)
          if (links.some((link) => link.id === view.link.id && link.removedAt !== null)) { refresh(); return }
        } catch { /* A leitura também falhou; o usuário poderá consultar novamente. */ }
      }
      setError(failure)
    } finally { setBusy(false) }
  }

  if (error && !data) return <section className="reconciliations" aria-live="polite"><h3>Conciliações</h3><OperationalError error={error} onRetry={refresh} /></section>
  if (!data) return <section className="reconciliations" aria-live="polite"><h3>Conciliações</h3><OperationalLoading>Carregando conciliações…</OperationalLoading></section>

  return <section className="reconciliations" aria-live="polite">
    <div className="credit-cards__heading">
      <div><h3>Conciliações</h3><p className="meta">Vincule explicitamente uma previsão pendente à obrigação de uma fatura.</p></div>
    </div>
    {error !== null && <OperationalError error={error} />}

    <section className="reconciliation-create" aria-labelledby="reconciliation-create-title">
      <h4 id="reconciliation-create-title">Nova conciliação</h4>
      <p className="meta">A escolha é manual. A fatura passa a ser a referência das projeções; nenhum valor é ajustado automaticamente.</p>
      <div className="reconciliation-form-grid">
        <label>Previsão de despesa
          <select value={selectedPlannedId} onChange={(event) => { setSelectedPlannedId(event.target.value); setReview(false) }} disabled={busy}>
            <option value="">Selecionar previsão pendente</option>
            {data.planned.filter((item) => !linkedPlanned.has(item.id) && item.status === 'Pending').map((item) => <option key={item.id} value={item.id}>{item.dueDate} · {item.description} · {formatBrlDecimal(item.amount)}</option>)}
          </select>
        </label>
        <label>Fatura com saldo devido
          <select value={selectedInvoiceId} onChange={(event) => { setSelectedInvoiceId(event.target.value); setReview(false) }} disabled={busy}>
            <option value="">Selecionar fatura</option>
            {eligibleInvoices.map((invoice) => {
              const card = data.cards.find((item) => item.id === invoice.creditCardId)
              const isCompatible = Boolean(selectedPlanned && card?.defaultPaymentAccountId && selectedPlanned.account.id === card.defaultPaymentAccountId)
              return <option key={invoice.id} value={invoice.id} disabled={!isCompatible}>{card?.name ?? 'Cartão'} · {invoice.competence} · vence {invoice.dueDate} · saldo {formatBrlDecimal(invoice.remainingAmount)}{!isCompatible ? ' · conta incompatível' : ''}</option>
            })}
          </select>
        </label>
      </div>
      {selectedPlanned && <div className="reconciliation-selection"><strong>Previsão selecionada</strong><span>{selectedPlanned.description} · {formatBrlDecimal(selectedPlanned.amount)} · {selectedPlanned.dueDate} · conta {selectedPlanned.account.name}</span></div>}
      {selectedInvoice && selectedCard && <div className="reconciliation-selection"><strong>Fatura selecionada</strong><span>{selectedCard.name} · competência {selectedInvoice.competence} · vencimento {selectedInvoice.dueDate} · total {formatBrlDecimal(selectedInvoice.totalAmount)} · pago {formatBrlDecimal(selectedInvoice.paidAmount)} · devido {formatBrlDecimal(selectedInvoice.remainingAmount)}</span></div>}
      {selectedPlanned && selectedInvoice && !compatible && <p className="field-error" role="alert">A conta da previsão não é a conta preferencial de pagamento do cartão. Altere a conta ou escolha outra fatura; a interface não fará alterações automáticas.</p>}
      {selectedPlanned && selectedInvoice && compatible && !review && <button type="button" className="button--primary" onClick={() => setReview(true)}>Revisar conciliação</button>}
      {review && selectedPlanned && selectedInvoice && selectedCard && <div className="reconciliation-review" role="group" aria-labelledby="reconciliation-review-title"><h5 id="reconciliation-review-title">Confira antes de confirmar</h5><p>Previsão: <strong>{formatBrlDecimal(selectedPlanned.amount)}</strong>. Fatura: <strong>{formatBrlDecimal(selectedInvoice.remainingAmount)} ainda devido</strong>.</p><p>Após a conciliação, a previsão continuará registrada, mas não será contabilizada separadamente nas projeções financeiras. A obrigação da fatura será utilizada como referência.</p><div className="form__actions"><button type="button" className="button--primary" onClick={() => void create()} disabled={busy}>{busy ? 'Confirmando…' : 'Confirmar conciliação'}</button><button type="button" onClick={() => setReview(false)} disabled={busy}>Voltar</button></div></div>}
    </section>

    <section aria-labelledby="active-reconciliations-title"><div className="section-heading"><h4 id="active-reconciliations-title">Vínculos ativos</h4><span className="meta">{views.length}</span></div>{views.length === 0 ? <p className="meta">Nenhuma conciliação ativa.</p> : <div className="reconciliation-list">{views.map((view) => <article className="reconciliation-card" key={view.link.id}><div><h5>{view.planned.description}</h5><p className="meta">Previsão {view.planned.dueDate} · conta {view.account?.name ?? view.planned.account.name}</p><p>Fatura {view.invoice.competence} · {view.card.name} · vence {view.invoice.dueDate}</p></div><dl><div><dt>Valor previsto</dt><dd>{formatBrlDecimal(view.planned.amount)}</dd></div><div><dt>Saldo devido</dt><dd>{formatBrlDecimal(view.invoice.remainingAmount)}</dd></div></dl><button type="button" onClick={() => void remove(view)} disabled={busy}>Desfazer vínculo</button></article>)}</div>}</section>
    {removedLinks.length > 0 && <details className="reconciliation-history"><summary>Histórico de vínculos removidos ({removedLinks.length})</summary><ul>{removedLinks.map((link) => <li key={link.id}>{link.plannedTransactionId} ↔ {link.creditCardInvoiceId} · removido em {link.removedAt}</li>)}</ul></details>}
  </section>
}
