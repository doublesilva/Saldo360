import { useState } from 'react'
import type { PlanActualDashboard } from '../../api/planActual'
import { syncConnection } from '../../api/pluggyPoc'
import { toUserMessage } from '../../api/apiErrors'
import { dateTime } from './overviewText'

/** Última sincronização com o banco e a ação de sincronizar, junto da posição bancária que ela atualiza. */
export function OverviewSyncLine({ workspaceId, sync, onSynced }: { workspaceId: string; sync: PlanActualDashboard['sync']; onSynced: () => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  if (!sync) return null
  const run = async (connectionId: string) => {
    setBusy(connectionId); setError(null)
    try { await syncConnection(workspaceId, connectionId) } catch (failure: unknown) { setError(toUserMessage(failure)) } finally { setBusy(null); onSynced() }
  }
  return <div className="ov-sync">
    <p className="ov-bank">{sync.latestSuccessAt ? `Última sincronização com o banco: ${dateTime(sync.latestSuccessAt)}.` : 'Ainda sem sincronização bem-sucedida com o banco.'}{' '}
      {sync.connections.map(connection => <button key={connection.connectionId} type="button" disabled={busy !== null || connection.status === 'Running'} onClick={() => void run(connection.connectionId)}>
        {busy === connection.connectionId ? 'Sincronizando…' : sync.connections.length > 1 ? `Sincronizar · ${connection.label ?? 'conexão'}` : 'Sincronizar agora'}</button>)}</p>
    {error && <p className="field-error" role="alert">{error}</p>}
  </div>
}
