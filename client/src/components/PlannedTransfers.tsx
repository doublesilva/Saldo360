import { useEffect, useState, type FormEvent } from 'react'
import type { FinancialAccount } from '../api/accounts'
import {
  cancelPlannedTransfer, createPlannedTransfer, linkPlannedTransfer, listPlannedTransfers, plannedTransferStatusLabels, unlinkPlannedTransfer, type PlannedTransfer,
} from '../api/plannedTransfers'
import { todayInSaoPaulo } from '../dates'
import { formatBrlDecimal, formatDate, parseAmount } from '../money'

const errorMessage = (error: unknown) => (error instanceof Error ? error.message : 'Falha inesperada.')
const addDays = (iso: string, days: number) => { const date = new Date(`${iso}T00:00:00Z`); date.setUTCDate(date.getUTCDate() + days); return date.toISOString().slice(0, 10) }

/**
 * Transferências previstas: compromissos futuros entre contas próprias. Não são lançamentos nem transferências realizadas: entram só na projeção (saída na origem, entrada no destino)
 * e na Agenda. Quando a transferência acontece, a associação é automática só se for inequívoca; na dúvida, escolha a transferência realizada correspondente.
 */
export function PlannedTransfers({ workspaceId, accounts, onChanged }: { workspaceId: string; accounts: FinancialAccount[]; onChanged?: () => void }) {
  const today = todayInSaoPaulo()
  const [items, setItems] = useState<PlannedTransfer[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)
  const [busy, setBusy] = useState(false)
  const [form, setForm] = useState({ source: '', destination: '', amount: '', dueDate: addDays(today, 7), description: '' })
  const active = accounts.filter((account) => account.isActive)

  useEffect(() => {
    const controller = new AbortController()
    listPlannedTransfers(workspaceId, addDays(today, -60), addDays(today, 180), true, controller.signal)
      .then((value) => { setItems(value); setError(null) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) setError(errorMessage(cause)) })
    return () => controller.abort()
  }, [workspaceId, reload, today])

  const run = async (action: () => Promise<unknown>) => {
    setBusy(true); setError(null)
    try { await action(); setReload((n) => n + 1); onChanged?.() } catch (cause) { setError(errorMessage(cause)) } finally { setBusy(false) }
  }
  const submit = (event: FormEvent) => {
    event.preventDefault()
    const amount = parseAmount(form.amount)
    if (amount.kind !== 'ok' || !form.source || !form.destination) { setError(amount.kind === 'invalid' ? amount.message : 'Informe origem, destino e um valor válido.'); return }
    void run(async () => {
      await createPlannedTransfer(workspaceId, { sourceAccountId: form.source, destinationAccountId: form.destination, amount: Number(amount.text), dueDate: form.dueDate, description: form.description.trim() || 'Transferência prevista' })
      setForm((current) => ({ ...current, amount: '', description: '' }))
    })
  }

  return (
    <section className="card planned-transfers" aria-labelledby="planned-transfers-title">
      <h3 id="planned-transfers-title">Transferências previstas</h3>
      <p className="meta">
        Planeje uma transferência futura entre contas suas. Ela aparece na Agenda e no saldo projetado (saída na origem e entrada no destino; no consolidado das duas contas não muda nada), mas não é
        receita, despesa nem transferência realizada. Quando a transferência acontecer, a previsão é associada a ela; se houver mais de uma possibilidade, você escolhe.
      </p>
      <form onSubmit={submit} aria-label="Nova transferência prevista" className="form">
        <label>Origem
          <select value={form.source} onChange={(event) => setForm({ ...form, source: event.target.value })}>
            <option value="">Selecione…</option>{active.map((account) => <option key={account.id} value={account.id}>{account.name}</option>)}
          </select></label>
        <label>Destino
          <select value={form.destination} onChange={(event) => setForm({ ...form, destination: event.target.value })}>
            <option value="">Selecione…</option>{active.map((account) => <option key={account.id} value={account.id}>{account.name}</option>)}
          </select></label>
        <label>Valor<input inputMode="decimal" value={form.amount} onChange={(event) => setForm({ ...form, amount: event.target.value })} /></label>
        <label>Data prevista<input type="date" value={form.dueDate} onChange={(event) => setForm({ ...form, dueDate: event.target.value })} /></label>
        <label>Descrição<input value={form.description} maxLength={200} onChange={(event) => setForm({ ...form, description: event.target.value })} /></label>
        <button type="submit" className="button--primary" disabled={busy}>Prever transferência</button>
      </form>
      {error && <p role="alert" className="status status--down">{error}</p>}
      {items && items.length === 0 && <p className="meta">Nenhuma transferência prevista.</p>}
      {items && items.length > 0 && (
        <ul className="transaction-list">
          {items.map((item) => (
            <li key={item.id} className={item.status === 'Cancelled' ? 'transaction transaction--cancelled' : 'transaction'}>
              <div className="transaction__main">
                <span className="transaction__description">{item.description}: {item.sourceAccount.name} → {item.destinationAccount.name}</span>
                <strong className="amount">{formatBrlDecimal(item.amount)}</strong>
              </div>
              <div className="transaction__meta meta">
                <span>{formatDate(item.dueDate)}</span>
                <span className="badge">{plannedTransferStatusLabels[item.status]}</span>
                {item.status === 'Realized' && <span>{item.linkOrigin === 'UserConfirmed' ? 'Associada por você à transferência realizada' : 'Associada automaticamente à transferência realizada'}</span>}
              </div>
              {(item.status === 'Planned' || item.status === 'Overdue') && item.candidates.length > 0 && (
                <ul aria-label="Transferências realizadas possíveis" className="meta">
                  {item.candidates.map((candidate) => (
                    <li key={candidate.transferId}>
                      Realizada em {formatDate(candidate.transferDate)}: {formatBrlDecimal(candidate.amount)} ({candidate.description}){' '}
                      <button type="button" disabled={busy} onClick={() => void run(() => linkPlannedTransfer(workspaceId, item.id, candidate.transferId))}>Associar a esta previsão</button>
                    </li>
                  ))}
                </ul>
              )}
              <div className="form__actions">
                {(item.status === 'Planned' || item.status === 'Overdue') && <button type="button" disabled={busy} onClick={() => void run(() => cancelPlannedTransfer(workspaceId, item.id))}>Cancelar previsão</button>}
                {item.status === 'Realized' && item.linkOrigin && <button type="button" disabled={busy} onClick={() => void run(() => unlinkPlannedTransfer(workspaceId, item.id))}>Desfazer associação</button>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
