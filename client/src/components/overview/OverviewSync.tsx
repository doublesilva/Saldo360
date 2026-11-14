import { useState } from 'react'
import type { PlanActualDashboard } from '../../api/planActual'
import { syncConnection } from '../../api/pluggyPoc'
import { toUserMessage } from '../../api/apiErrors'
import { delayedImpact, needsIntervention, syncHealthReason, syncHealthSummaryLabel } from '../../api/syncHealth'
import { dateTime } from './overviewText'

/**
 * Saúde da automação bancária, em uma linha: estado em texto, motivo e "Ver automação" levando à conexão afetada. Problemas que exigem intervenção (bloqueada, requer atenção)
 * já têm item em "Próximas ações": aqui ficam só o estado e o nome, sem repetir motivo nem ação. O impacto só aparece com atraso confirmado. Sem `health` (servidor antigo) não há linha.
 */
export function OverviewSyncHealth({ sync, onOpenAutomation }: { sync: PlanActualDashboard['sync']; onOpenAutomation: (connectionIds: string[]) => void }) {
  const health = sync?.health
  if (!sync || !health) return null
  const names = (ids: string[]) => sync.connections.filter(c => ids.includes(c.connectionId)).map(c => c.label ?? 'Conexão').join(', ')
  const duplicated = needsIntervention(health.state)
  const worst = sync.connections.filter(c => health.affectedConnectionIds.includes(c.connectionId))
  // Motivo só quando uma única conexão está no pior estado (com várias, o detalhe fica na Central).
  const reason = !duplicated && worst.length === 1 && worst[0].health ? syncHealthReason(worst[0].health, iso => dateTime(iso) ?? '') : null
  const useful = health.state !== 'UpToDate' && !duplicated
  const target = health.affectedConnectionIds
  return <p className={`ov-health ov-health--${health.state}`} data-state={health.state}>
    <span className="ov-health__state"><span className="ov-health__label">Sincronização bancária:</span> <strong>{syncHealthSummaryLabel(health)}</strong>{target.length > 0 && <span className="ov-health__names"> · {names(target)}</span>}</span>
    {reason && <span className="ov-health__reason">{reason}</span>}
    {health.state === 'Delayed' && <span className="ov-health__impact">{delayedImpact}</span>}
    {useful && <button type="button" className="ov-health__action" onClick={() => onOpenAutomation(target)}>Ver automação</button>}
  </p>
}

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
