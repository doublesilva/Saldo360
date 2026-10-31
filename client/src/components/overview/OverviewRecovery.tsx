import { useEffect, useState } from 'react'
import type { PlanActualDashboard } from '../../api/planActual'
import { captureOverdraftRecovery, getOverdraftRecovery, type OverdraftRecovery } from '../../api/overdraftRecovery'
import { money, dateTime } from './overviewText'
import { toUserMessage } from '../../api/apiErrors'

export function OverviewRecovery({ workspaceId, data, onAccounts, onProjection }: { workspaceId: string; data: PlanActualDashboard; onAccounts: () => void; onProjection: () => void }) {
  const account = data.scope.accountId ? data.overdraft.accounts.find(item => item.accountId === data.scope.accountId) : null
  const accountId = account?.accountId
  const [recovery, setRecovery] = useState<OverdraftRecovery | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    if (!accountId) return
    const controller = new AbortController()
    getOverdraftRecovery(workspaceId, accountId, data.competence, controller.signal).then(setRecovery).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId, accountId, data.competence])
  if (!data.overdraft.eligible && !recovery) return null
  const register = async () => {
    if (!account) return
    setBusy(true); setError(null)
    try { setRecovery(await captureOverdraftRecovery(workspaceId, account.accountId, data.competence)) }
    catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }
  return <section className="ov-card ov-recovery">
    <h3>Recuperação do limite</h3>
    {recovery ? <>
      <p><strong>{recovery.accountName}</strong> · {recovery.state}</p>
      <dl className="account__details">
        <div><dt>Uso na abertura</dt><dd>{money(recovery.openingUsed)}</dd></div><div><dt>Uso atual</dt><dd>{money(recovery.currentUsed)}</dd></div>
        <div><dt>Meta original</dt><dd>{money(recovery.target)}</dd></div><div><dt>Recuperado</dt><dd>{money(recovery.recovered)}</dd></div>
        <div><dt>Recuperação projetada</dt><dd>{money(recovery.projectedRecovery)}</dd></div><div><dt>Desvio</dt><dd>{money(recovery.deviation)}</dd></div>
      </dl>
      <p className="meta">Referência de abertura: {recovery.referenceDate} · atualizado {dateTime(recovery.updatedAt)}{recovery.reasons.length ? ' · projeção parcial' : ''}</p>
    </> : <p className="meta">A meta original desta competência ainda não foi registrada. O histórico não é inventado nem congelado automaticamente.</p>}
    {error && <p className="field-error" role="alert">{error}</p>}
    <div className="form__actions"><button type="button" onClick={onProjection}>Ver projeção</button>
      {!recovery && account && <button type="button" className="button--primary" disabled={busy} onClick={() => void register()}>{busy ? 'Registrando…' : 'Registrar meta do mês'}</button>}
      {(account?.contractedLimit == null || account.contractedLimit === '0.00') && <button type="button" onClick={onAccounts}>Informar limite</button>}
    </div>
  </section>
}
