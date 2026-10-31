import { useEffect, useRef, useState, type KeyboardEvent } from 'react'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import { listCategories, type FinancialCategory } from '../api/categories'
import type { FinancialCalendarItem } from '../api/financialCalendar'
import { cancelPlanned, getPlanned, type PlannedTransaction } from '../api/planned'
import { cancelTransaction, getTransaction, type FinancialTransaction } from '../api/transactions'
import { formatBrlDecimal, formatDate } from '../money'
import { PlannedForm, RealizeForm } from './Planned'
import { TransactionForm } from './Transactions'

type Detail =
  | { source: 'PlannedTransaction'; value: PlannedTransaction }
  | { source: 'Transaction'; value: FinancialTransaction }

type Load<T> = { kind: 'loading' } | { kind: 'loaded'; data: T } | { kind: 'error'; message: string }
type References = { accounts: FinancialAccount[]; categories: FinancialCategory[] }
type Mode = 'details' | 'realize' | 'edit'

const typeLabels = { Income: 'Receita', Expense: 'Despesa' } as const
const statusLabels = { Realized: 'Realizado', Pending: 'Pendente', Overdue: 'Vencido' } as const

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

export function FinancialCalendarDetails({
  workspaceId,
  item,
  returnFocus,
  onClose,
  onChanged,
}: {
  workspaceId: string
  item: FinancialCalendarItem
  returnFocus: HTMLElement | null
  onClose: () => void
  onChanged: () => void
}) {
  const [detail, setDetail] = useState<Load<Detail>>({ kind: 'loading' })
  const [references, setReferences] = useState<Load<References> | null>(null)
  const [mode, setMode] = useState<Mode>('details')
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const dialogRef = useRef<HTMLElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const cancelling = useRef(false)
  const [origin, setOrigin] = useState<PlannedTransaction | null>(null)

  useEffect(() => {
    closeRef.current?.focus()
    return () => returnFocus?.focus()
  }, [returnFocus])

  useEffect(() => {
    if (item.source === 'DebtInstallment') return
    const controller = new AbortController()
    const request = item.source === 'PlannedTransaction'
      ? getPlanned(workspaceId, item.id, controller.signal).then<Detail>((value) => ({ source: 'PlannedTransaction', value }))
      : getTransaction(workspaceId, item.id, controller.signal).then<Detail>((value) => ({ source: 'Transaction', value }))
    request
      .then((data) => { if (!controller.signal.aborted) setDetail({ kind: 'loaded', data }) })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setDetail({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, item.id, item.source])

  useEffect(() => {
    if (mode !== 'edit') return
    const controller = new AbortController()
    Promise.all([listAccounts(workspaceId, true, controller.signal), listCategories(workspaceId, true, controller.signal)])
      .then(([accounts, categories]) => { if (!controller.signal.aborted) setReferences({ kind: 'loaded', data: { accounts, categories } }) })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setReferences({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, mode])

  const finish = () => {
    onChanged()
    onClose()
  }

  const startEditing = () => {
    setReferences({ kind: 'loading' })
    setMode('edit')
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key === 'Escape' && !busy) {
      event.preventDefault()
      onClose()
      return
    }
    if (event.key !== 'Tab') return
    const focusable = Array.from(
      dialogRef.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])',
      ) ?? [],
    )
    if (focusable.length === 0) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault()
      last.focus()
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault()
      first.focus()
    }
  }

  const handleCancel = async (data: Detail) => {
    if (cancelling.current) return
    const isPlanned = data.source === 'PlannedTransaction'
    const date = data.source === 'PlannedTransaction' ? data.value.dueDate : data.value.transactionDate
    const consequence = data.source === 'PlannedTransaction'
      ? 'A previsão será preservada como cancelada, não poderá ser editada e deixará de participar da projeção.'
      : data.value.plannedTransactionId
        ? 'O lançamento será preservado como cancelado, deixará de contar no saldo e a previsão vinculada voltará a ficar pendente.'
        : 'O lançamento será preservado como cancelado, não poderá ser editado e deixará de contar no saldo.'
    const label = isPlanned ? 'previsão' : 'lançamento'
    if (!window.confirm(
      `Cancelar ${label} "${data.value.description}" de ${formatBrlDecimal(data.value.amount)} em ${formatDate(date)}? ${consequence}`,
    )) return

    cancelling.current = true
    setBusy(true)
    setActionError(null)
    try {
      const result = isPlanned
        ? await cancelPlanned(workspaceId, data.value.id)
        : await cancelTransaction(workspaceId, data.value.id)
      if (result.ok) finish()
      else setActionError(Object.values(result.errors).flat()[0] ?? `Não foi possível cancelar o ${label}.`)
    } catch (error: unknown) {
      setActionError(errorMessage(error))
    } finally {
      cancelling.current = false
      setBusy(false)
    }
  }

  const loaded = detail.kind === 'loaded' ? detail.data : null
  const displayedItem = loaded ? itemFromDetail(item, loaded) : item
  const canAct = loaded && (
    loaded.source === 'PlannedTransaction' ? loaded.value.status === 'Pending' : !loaded.value.isCancelled
  )
  const actions = canAct ? item.allowedActions ?? [] : []

  const viewOrigin = async () => {
    if (!loaded || loaded.source !== 'Transaction' || !loaded.value.plannedTransactionId) return
    setBusy(true)
    try { setOrigin(await getPlanned(workspaceId, loaded.value.plannedTransactionId)) }
    catch (error: unknown) { setActionError(errorMessage(error)) }
    finally { setBusy(false) }
  }

  return (
    <div className="calendar-detail-backdrop">
      <section
        ref={dialogRef}
        className="calendar-detail-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="calendar-detail-title"
        onKeyDown={handleKeyDown}
      >
        <header className="calendar-detail-panel__header">
          <div>
            <p className="meta">{item.source === 'Transaction' ? 'Lançamento' : 'Previsão'}</p>
            <h2 id="calendar-detail-title">Detalhes do compromisso</h2>
          </div>
          <button ref={closeRef} type="button" onClick={onClose} disabled={busy} aria-label="Fechar detalhes">
            ×
          </button>
        </header>

        {mode === 'details' && (
          <>
            <DetailFields item={displayedItem} status={loaded?.value.isCancelled ? 'Cancelado' : loaded?.source === 'PlannedTransaction' && loaded.value.status === 'Realized' ? 'Realizado' : undefined} />
            {detail.kind === 'loading' && item.source !== 'DebtInstallment' && <p className="status status--pending" role="status">Carregando detalhes…</p>}
            {item.source === 'DebtInstallment' && <p className="notice">Parcela de dívida: consulte e altere pelo fluxo de Dívidas.</p>}
            {item.origin === 'CreditCardInvoicePayment' && <p className="notice">Pagamento de fatura: correções pertencem ao fluxo de Cartões, não ao cancelamento de lançamento avulso.</p>}
            {item.origin === 'PluggyRefund' && item.source === 'Transaction' && <p className="notice">Devolução bancária: entrada de caixa que não é receita e que abate a despesa devolvida. Para desfazer o vínculo, use Lançamentos.</p>}
            {item.origin === 'DebtInstallmentPayment' && item.source === 'Transaction' && <p className="notice">Pagamento de dívida: correções pertencem ao fluxo de Dívidas.</p>}
            {detail.kind === 'error' && <p className="status status--down" role="alert">{detail.message}</p>}
            {loaded?.source === 'Transaction' && loaded.value.plannedTransactionId && (
              <p className="notice">Este lançamento realiza uma previsão. Cancelá-lo fará a previsão voltar a ficar pendente.</p>
            )}
            {actionError && <p className="field-error" role="alert">{actionError}</p>}
            {item.recurrenceId && <p className="notice">Origem: recorrência {item.recurrenceId}, ocorrência {item.occurrenceNumber}. Alterações afetam somente esta ocorrência, nunca a definição recorrente.</p>}
            {origin && <section aria-label="Previsão de origem"><h3>Previsão de origem</h3><p>{origin.description} — {formatBrlDecimal(origin.amount)} — {formatDate(origin.dueDate)} — {origin.status}</p></section>}
            {canAct && (
              <div className="form__actions calendar-detail-panel__actions">
                {actions.includes('Realize') && loaded.source === 'PlannedTransaction' && (
                  <button type="button" className="button--primary" onClick={() => setMode('realize')} disabled={busy}>
                    Realizar
                  </button>
                )}
                {actions.includes('Edit') && (
                  <button type="button" onClick={startEditing} disabled={busy}>Editar</button>
                )}
                {actions.includes('ViewOrigin') && <button type="button" onClick={() => void viewOrigin()} disabled={busy}>Ver previsão de origem</button>}
                {(actions.includes('Cancel') || actions.includes('UndoRealization')) && <button type="button" onClick={() => void handleCancel(loaded)} disabled={busy}>
                  {busy ? 'Cancelando…' : actions.includes('UndoRealization') ? 'Desfazer realização' : loaded.source === 'PlannedTransaction' ? 'Cancelar previsão' : 'Cancelar lançamento'}
                </button>}
              </div>
            )}
          </>
        )}

        {mode === 'realize' && loaded?.source === 'PlannedTransaction' && (
          <RealizeForm
            workspaceId={workspaceId}
            planned={loaded.value}
            onRealized={finish}
            onCancel={() => setMode('details')}
          />
        )}

        {mode === 'edit' && references?.kind === 'loading' && (
          <p className="status status--pending" role="status">Carregando contas e categorias…</p>
        )}
        {mode === 'edit' && references?.kind === 'error' && (
          <>
            <p className="status status--down" role="alert">{references.message}</p>
            <button type="button" onClick={() => { setReferences(null); setMode('details') }}>Voltar</button>
          </>
        )}
        {mode === 'edit' && references?.kind === 'loaded' && loaded?.source === 'PlannedTransaction' && (
          <PlannedForm
            workspaceId={workspaceId}
            references={references.data}
            planned={loaded.value}
            onSaved={finish}
            onCancel={() => setMode('details')}
          />
        )}
        {mode === 'edit' && references?.kind === 'loaded' && loaded?.source === 'Transaction' && (
          <TransactionForm
            workspaceId={workspaceId}
            references={references.data}
            transaction={loaded.value}
            onSaved={finish}
            onCancel={() => setMode('details')}
          />
        )}
      </section>
    </div>
  )
}

function itemFromDetail(original: FinancialCalendarItem, detail: Detail): FinancialCalendarItem {
  if (detail.source === 'PlannedTransaction') {
    return {
      ...original,
      type: detail.value.type,
      amount: detail.value.amount,
      date: detail.value.dueDate,
      description: detail.value.description,
      account: detail.value.account,
      category: detail.value.category,
      currency: detail.value.currency,
    }
  }
  return {
    ...original,
    type: detail.value.type,
    amount: detail.value.amount,
    date: detail.value.transactionDate,
    description: detail.value.description,
    account: detail.value.account,
    category: detail.value.category,
    currency: detail.value.currency,
    plannedTransactionId: detail.value.plannedTransactionId,
  }
}

function DetailFields({ item, status }: { item: FinancialCalendarItem; status?: string }) {
  return (
    <dl className="calendar-detail-list">
      <div><dt>Descrição</dt><dd>{item.description}</dd></div>
      <div><dt>Valor</dt><dd className={item.type === 'Income' ? 'amount amount--income' : 'amount amount--negative'}>{formatBrlDecimal(item.amount)}</dd></div>
      <div><dt>Tipo</dt><dd>{typeLabels[item.type]}</dd></div>
      <div><dt>{item.source === 'Transaction' ? 'Data efetiva' : 'Data prevista'}</dt><dd>{formatDate(item.date)}</dd></div>
      <div><dt>Conta financeira</dt><dd>{item.account?.name ?? 'Sem conta atribuída'}{item.account && !item.account.isActive ? ' (inativa)' : ''}</dd></div>
      {item.category && <div><dt>Categoria</dt><dd>{item.category.name}{item.category.isActive ? '' : ' (inativa)'}</dd></div>}
      <div><dt>Origem</dt><dd>{item.source === 'DebtInstallment' ? 'Parcela de dívida' : item.source === 'Transaction' ? 'Lançamento' : 'Previsão'}</dd></div>
      <div><dt>Situação</dt><dd>{status ?? statusLabels[item.status]}</dd></div>
    </dl>
  )
}
