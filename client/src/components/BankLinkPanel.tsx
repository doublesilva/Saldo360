import { useEffect, useState } from 'react'
import { linkBankTransaction, listBankCandidates, type BankCandidate } from '../api/recurrences'
import { formatBrlDecimal, formatDate } from '../money'

type Load = { kind: 'loading' } | { kind: 'loaded'; items: BankCandidate[] } | { kind: 'error'; message: string }

/**
 * Exceção ao fluxo automático: o motor não achou par seguro para esta competência. Lista os débitos do banco compatíveis e, ao vincular, o valor do banco
 * passa a valer só nesta competência (sem segunda confirmação). Um débito pendente informa o valor, mas não comprova pagamento.
 */
export function BankLinkPanel({ workspaceId, recurrenceId, competence, onLinked, onCancel }: {
  workspaceId: string
  recurrenceId: string
  /** yyyy-MM */
  competence: string
  onLinked: () => void
  onCancel: () => void
}) {
  const [state, setState] = useState<Load>({ kind: 'loading' })
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    listBankCandidates(workspaceId, recurrenceId, competence, controller.signal)
      .then((items) => { if (!controller.signal.aborted) setState({ kind: 'loaded', items }) })
      .catch((e: unknown) => { if (!controller.signal.aborted) setState({ kind: 'error', message: e instanceof Error ? e.message : 'Falha ao carregar.' }) })
    return () => controller.abort()
  }, [workspaceId, recurrenceId, competence])

  const link = async (transactionId: string) => {
    setBusy(transactionId); setError(null)
    try { await linkBankTransaction(workspaceId, recurrenceId, competence, transactionId); onLinked() }
    catch (e: unknown) { setError(e instanceof Error ? e.message : 'Não foi possível vincular.'); setBusy(null) }
  }

  return (
    <section aria-label="Vincular agendamento do banco">
      <p className="notice">Débitos do banco compatíveis com {competence.slice(5)}/{competence.slice(0, 4)}. O valor do banco passa a valer só nesta competência; o valor-base e as outras competências não mudam.</p>
      {state.kind === 'loading' && <p className="status status--pending" role="status">Carregando débitos do banco…</p>}
      {state.kind === 'error' && <p className="status status--down" role="alert">{state.message}</p>}
      {state.kind === 'loaded' && state.items.length === 0 && <p className="notice">Nenhum débito do banco compatível encontrado.</p>}
      {state.kind === 'loaded' && state.items.length > 0 && (
        <ul className="calendar-detail-candidates">
          {state.items.map((item) => (
            <li key={item.transactionId}>
              <span>{formatDate(item.date)} — {item.description} — {formatBrlDecimal(item.amount)} ({item.status === 'Pending' ? 'agendado' : 'efetivado'})</span>
              <button type="button" onClick={() => void link(item.transactionId)} disabled={busy !== null}>{busy === item.transactionId ? 'Vinculando…' : 'Vincular'}</button>
            </li>
          ))}
        </ul>
      )}
      {error && <p className="field-error" role="alert">{error}</p>}
      <div className="form__actions"><button type="button" onClick={onCancel} disabled={busy !== null}>Voltar</button></div>
    </section>
  )
}
