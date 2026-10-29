import { useCallback, useEffect, useState } from 'react'
import { describeSyncError, getSyncMonitor, syncStatusLabels, triggerLabels, type SyncConnection, type SyncMonitor } from '../api/pluggySyncStatus'
import { syncConnection } from '../api/pluggyPoc'
import { toUserMessage } from '../api/apiErrors'
import { formatDate } from '../money'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import '../CardProjection.css'

const dateTime = (iso: string | null) => iso ? `${formatDate(iso.slice(0, 10))} ${new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: 'America/Sao_Paulo' })}` : '—'
const duration = (seconds: number | null) => seconds === null ? '—' : seconds < 60 ? `${seconds} s` : `${Math.floor(seconds / 60)} min ${seconds % 60} s`

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: SyncMonitor }

/**
 * Sincronização das contas e cartões integrados: última tentativa e sucesso, próxima execução automática, o que mudou, erro resumido, autorização perto do
 * vencimento e as últimas execuções. "Sincronizar agora" usa a mesma sincronização de sempre (a automática nunca roda ao mesmo tempo na mesma conexão).
 */
export function PluggySyncPanel({ workspaceId, refreshKey = '', onChanged }: { workspaceId: string; refreshKey?: string; onChanged?: () => void }) {
  const [reload, setReload] = useState(0)
  // O erro do botão sobrevive ao recarregamento da lista (que desmonta os cartões): fica por conexão.
  const [errors, setErrors] = useState<Record<string, string | null>>({})
  const key = `${workspaceId}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getSyncMonitor(workspaceId, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, key])

  const refresh = useCallback(() => { setReload(value => value + 1); onChanged?.() }, [onChanged])
  if (visible.state === 'loaded' && (visible.data.connections ?? []).length === 0) return null
  return <section className="pluggy-sync" aria-labelledby="pluggy-sync-title">
    <h4 id="pluggy-sync-title">Sincronização</h4>
    {visible.state === 'loading' && <OperationalLoading>Carregando a sincronização…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {visible.state === 'loaded' && <>
      <p className="meta">{visible.data.auto.enabled
        ? `Sincronização automática ligada: a cada ${visible.data.auto.intervalHours} h${visible.data.auto.windowStartHour === 0 && visible.data.auto.windowEndHour === 24 ? '' : `, entre ${visible.data.auto.windowStartHour}h e ${visible.data.auto.windowEndHour}h`}, quando a instituição tem dados novos.`
        : 'Sincronização automática desligada: use "Sincronizar agora".'}</p>
      <ul className="card-installments__list">{visible.data.connections.map(connection => <Connection key={connection.connectionId} workspaceId={workspaceId} connection={connection} autoEnabled={visible.data.auto.enabled} error={errors[connection.connectionId] ?? null}
        onError={message => setErrors(current => ({ ...current, [connection.connectionId]: message }))} onDone={refresh} />)}</ul>
    </>}
  </section>
}

function Connection({ workspaceId, connection, autoEnabled, error, onError, onDone }: { workspaceId: string; connection: SyncConnection; autoEnabled: boolean; error: string | null; onError: (message: string | null) => void; onDone: () => void }) {
  const [busy, setBusy] = useState(false)
  const run = async () => {
    setBusy(true); onError(null)
    try { await syncConnection(workspaceId, connection.connectionId) } catch (failure: unknown) { onError(toUserMessage(failure)) } finally { setBusy(false); onDone() }
  }
  const problem = connection.status === 'Failed' || connection.status === 'CompletedWithErrors' || connection.status === 'ConsentExpired'
  const reason = describeSyncError(connection.lastError)
  return <li className="card-installments__chain" aria-label={`Sincronização de ${connection.label ?? connection.institution ?? 'conexão'}`}>
    <div>
      <strong>{connection.label ?? connection.institution ?? 'Conexão'}</strong>{' '}
      <span className={`badge badge--sync-${connection.status.toLowerCase()}`}>{syncStatusLabels[connection.status]}</span>{' '}
      <span className="meta">{connection.integratedAccounts} conta(s)/cartão(ões) integrado(s)</span>
    </div>
    <dl className="card-overview__facts">
      <div><dt>Última tentativa</dt><dd>{dateTime(connection.lastAttemptAt)}{connection.lastTrigger ? ` · ${triggerLabels[connection.lastTrigger]}` : ''}</dd></div>
      <div><dt>Última sincronização bem-sucedida</dt><dd>{dateTime(connection.lastSuccessAt)}</dd></div>
      <div><dt>Próxima sincronização</dt><dd>{autoEnabled ? dateTime(connection.nextAttemptAt) : 'Automática desligada'}</dd></div>
      <div><dt>Última execução</dt><dd>{duration(connection.lastDurationSeconds)} · {connection.lastCreated} novo(s), {connection.lastUpdated} atualizado(s)</dd></div>
    </dl>
    {connection.status === 'ConsentExpired' && <p className="notice notice--warning" role="alert">A autorização desta conexão expirou: renove-a no Meu Pluggy. Enquanto isso nada é atualizado.</p>}
    {connection.status === 'SourceNotUpdated' && <p className="meta">A instituição ainda não trouxe dados novos desde a última sincronização{autoEnabled ? '; o Saldo360 verifica de novo sozinho.' : '. A sincronização automática está desligada: use "Sincronizar agora" quando quiser verificar.'}</p>}
    {connection.consentExpiringSoon && connection.consentExpiresAt && <p className="notice notice--warning" role="status">A autorização vence em {formatDate(connection.consentExpiresAt.slice(0, 10))}. Renove-a no Meu Pluggy para não interromper a atualização.</p>}
    {problem && reason && connection.status !== 'ConsentExpired' && <p className="meta" role="status">{reason}{connection.consecutiveFailures > 1 ? ` (${connection.consecutiveFailures} falhas seguidas)` : ''}</p>}
    {error && <p className="field-error" role="alert">{error}</p>}
    <button type="button" className="button--primary" disabled={busy || connection.status === 'Running'} onClick={() => void run()}>{busy ? 'Sincronizando…' : 'Sincronizar agora'}</button>
    {connection.recentRuns.length > 0 && <details><summary>Últimas execuções ({connection.recentRuns.length})</summary>
      <table className="card-installments__table">
        <thead><tr><th scope="col">Início</th><th scope="col">Origem</th><th scope="col">Resultado</th><th scope="col">Novos / atualizados</th></tr></thead>
        <tbody>{connection.recentRuns.map(item => <tr key={item.id}>
          <td>{dateTime(item.startedAt)}</td><td>{triggerLabels[item.trigger] ?? item.trigger}</td>
          <td>{syncStatusLabels[item.status]}{describeSyncError(item.errorSummary) ? ` — ${describeSyncError(item.errorSummary)}` : ''}</td>
          <td>{item.created} / {item.updated}</td>
        </tr>)}</tbody>
      </table>
    </details>}
  </li>
}
