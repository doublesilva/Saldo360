import { useEffect, useState } from 'react'
import type { OverdraftAccount, PlanActualDashboard } from '../../api/planActual'
import { captureOverdraftRecovery, getOverdraftRecovery, type OverdraftRecovery } from '../../api/overdraftRecovery'
import { ApiRequestError, toUserMessage } from '../../api/apiErrors'
import { money, dateTime, isNegative, isPositiveAmount, percentText, barWidth } from './overviewText'
import { formatDate } from '../../money'

/** Texto do estado da recuperação (o servidor decide o estado; aqui só se traduz). */
const recoveryStateText: Record<string, string> = {
  NoUsage: 'Sem utilização do limite',
  FullyRecovered: 'Limite totalmente recuperado',
  Worsening: 'Piora do saldo',
  AboveLimit: 'Utilização acima do limite',
  PartialRecovery: 'Recuperação parcial',
  ProjectionIncomplete: 'Projeção incompleta',
  InProgress: 'Em acompanhamento',
}

type Load =
  | { kind: 'loading' }
  | { kind: 'ok'; recovery: OverdraftRecovery }
  | { kind: 'unregistered' }
  | { kind: 'error'; message: string }

/** Uma conta elegível: busca a meta registrada e mostra o acompanhamento. O dinheiro vem pronto do servidor. */
function RecoveryAccount({ workspaceId, data, account, onAccounts }: { workspaceId: string; data: PlanActualDashboard; account: OverdraftAccount; onAccounts: () => void }) {
  const [load, setLoad] = useState<Load>({ kind: 'loading' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    getOverdraftRecovery(workspaceId, account.accountId, data.competence, controller.signal)
      .then(recovery => setLoad({ kind: 'ok', recovery }))
      .catch((failure: unknown) => {
        if (controller.signal.aborted) return
        const fields = failure instanceof ApiRequestError ? failure.fieldErrors : {}
        if (fields.snapshot) setLoad({ kind: 'unregistered' })
        else if (fields.balance) setLoad({ kind: 'error', message: 'Saldo indisponível para acompanhar a recuperação desta conta.' })
        else setLoad({ kind: 'error', message: `Não foi possível carregar a recuperação: ${toUserMessage(failure)}` })
      })
    return () => controller.abort()
  }, [workspaceId, account.accountId, data.competence])
  const register = async () => {
    setBusy(true); setError(null)
    try { setLoad({ kind: 'ok', recovery: await captureOverdraftRecovery(workspaceId, account.accountId, data.competence) }) }
    catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }
  const noLimit = account.contractedLimit == null
  return <div className="ov-recovery__account">
    <p><strong>{account.name}</strong>{noLimit ? ' · Sem limite informado' : ` · Limite contratado ${money(account.contractedLimit)}`}</p>
    {load.kind === 'loading' && <p className="status status--pending" role="status">Carregando recuperação…</p>}
    {load.kind === 'error' && <p className="field-error" role="alert">{load.message}</p>}
    {load.kind === 'unregistered' && <p className="meta">Meta ainda não fixada e registrada para esta competência: a meta provisória, quando calculada, aparece no resumo da recuperação. O histórico não é inventado nem congelado automaticamente.</p>}
    {load.kind === 'ok' && <RecoveryDetails recovery={load.recovery} />}
    {error && <p className="field-error" role="alert">{error}</p>}
    <div className="form__actions">
      {load.kind === 'unregistered' && <button type="button" className="button--primary" disabled={busy} onClick={() => void register()}>{busy ? 'Registrando…' : 'Registrar meta do mês'}</button>}
      {noLimit && <button type="button" onClick={onAccounts}>Informar limite</button>}
    </div>
  </div>
}

function RecoveryDetails({ recovery }: { recovery: OverdraftRecovery }) {
  const final = recovery.isFinalResult
  const targetLabel = isNegative(recovery.target) ? `Piora prevista de ${money(recovery.target.replace('-', ''))}` : money(recovery.target)
  return <>
    <p>{recoveryStateText[recovery.state] ?? 'Em acompanhamento'}{final ? ' · Mês encerrado (resultado final)' : ''}</p>
    {recovery.positiveBalance && <p className="meta">Saldo positivo de {money(recovery.positiveBalance)}: a recuperação terminou, sem exceder a utilização inicial.</p>}
    <dl className="account__details">
      <div><dt>Uso na abertura</dt><dd>{money(recovery.openingUsed)}</dd></div>
      <div><dt>Uso atual</dt><dd>{money(recovery.currentUsed)}</dd></div>
      <div><dt>Meta original (fixa)</dt><dd>{targetLabel}</dd></div>
      <div><dt>{final ? 'Recuperado (resultado final)' : 'Recuperado'}</dt><dd>{money(recovery.recovered)}</dd></div>
      {!final && <div><dt>Recuperação projetada</dt><dd>{recovery.projectedRecovery === null ? 'Projeção incompleta' : money(recovery.projectedRecovery)}</dd></div>}
      {!final && <div><dt>Desvio em relação à meta</dt><dd>{recovery.deviation === null ? 'Indisponível' : money(recovery.deviation)}</dd></div>}
      {recovery.available !== null && <div><dt>Disponível estimado</dt><dd>{money(recovery.available)}</dd></div>}
      {recovery.aboveLimit !== null && isPositiveAmount(recovery.aboveLimit) && <div><dt>Acima do limite</dt><dd>{money(recovery.aboveLimit)}</dd></div>}
    </dl>
    {recovery.attainmentPercent !== null
      ? <p className="meta">Atingimento da meta: {percentText(recovery.attainmentPercent)}<span className="ov-attain" aria-hidden="true"><span style={{ width: barWidth(recovery.attainmentPercent) }} /></span></p>
      : <p className="meta">Meta sem valor a recuperar: comparação em reais (recuperado × meta).</p>}
    {recovery.lateReference && <p className="meta">Referência tardia: a meta foi registrada em {dateTime(recovery.capturedAt)}, depois do início do mês; a abertura usada é a de {formatDate(recovery.referenceDate)}.</p>}
    <p className="meta">Abertura de referência: {formatDate(recovery.referenceDate)} · saldo de {formatDate(recovery.balanceAsOf)} · calculado em {dateTime(recovery.updatedAt)}{recovery.reasons.length ? ' · projeção parcial' : ''}</p>
  </>
}

export function OverviewRecovery({ workspaceId, data, onAccounts, onProjection }: { workspaceId: string; data: PlanActualDashboard; onAccounts: () => void; onProjection: () => void }) {
  const accounts = data.scope.accountId ? data.overdraft.accounts.filter(item => item.accountId === data.scope.accountId) : data.overdraft.accounts
  if (!data.overdraft.eligible && accounts.length === 0) return null
  return <section className="ov-card ov-recovery">
    <h3>Recuperação do limite</h3>
    {accounts.length === 0 && <p className="meta">Nenhuma conta elegível neste filtro.</p>}
    {accounts.map(account => <RecoveryAccount key={account.accountId} workspaceId={workspaceId} data={data} account={account} onAccounts={onAccounts} />)}
    {!data.scope.accountId && accounts.length > 1 && <p className="meta">Cada conta é acompanhada separadamente: utilizações e saldos positivos não se compensam entre contas.</p>}
    <div className="form__actions"><button type="button" onClick={onProjection}>Ver projeção</button></div>
  </section>
}
