import { useEffect, useState } from 'react'
import {
  getReconciliationOverview, getReconciliationPeriod, kindLabels, monthReasonLabels, observationStatusLabels, reliabilityLabels, statusExplanations, statusLabels,
  type AccountReconciliation, type ReconciliationOverview, type ReconciliationPeriod,
} from '../api/accountReconciliation'
import { formatBrlDecimal, formatDate } from '../money'
import { formatMonthYear, todayInSaoPaulo } from '../dates'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import '../CardProjection.css'

type Load<T> = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: T }

/**
 * Saldo informado pelo banco × saldo calculado, por conta integrada. Só exibe o que o backend calculou: nenhuma diferença é ajustada e nenhum
 * saldo inicial manual é exigido quando há referência bancária suficiente.
 */
export function AccountBalanceReconciliation({ workspaceId, refreshKey = '' }: { workspaceId: string; refreshKey?: string }) {
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load<ReconciliationOverview>>({ key, state: 'loading' })
  const visible: Load<ReconciliationOverview> = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getReconciliationOverview(workspaceId, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, key])

  if (visible.state === 'loaded' && (visible.data.accounts ?? []).length === 0) return null
  return <section className="balance-reconciliation" aria-labelledby="balance-reconciliation-title">
    <div className="section-heading"><div><h3 id="balance-reconciliation-title">Saldo bancário × calculado</h3><p className="meta">Contas integradas: o saldo informado pelo banco comparado ao calculado pelo Saldo360. Diferenças são mostradas e classificadas, nunca ajustadas.</p></div></div>
    {visible.state === 'loading' && <OperationalLoading>Comparando saldos…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {visible.state === 'loaded' && <Loaded workspaceId={workspaceId} data={visible.data} />}
  </section>
}

function Loaded({ workspaceId, data }: { workspaceId: string; data: ReconciliationOverview }) {
  return <>
    <dl className="credit-card-limit" aria-label="Subtotais por tipo de conta">
      {data.byScope.map(scope => <div key={scope.scope}>
        <dt>Contas {scope.scope} — banco / calculado</dt>
        <dd>{scope.complete && scope.bank && scope.calculated ? `${formatBrlDecimal(scope.bank)} / ${formatBrlDecimal(scope.calculated)}` : 'Indisponível'}</dd>
        {!scope.complete && <small className="meta">{scope.reasons.join('; ') || 'Há conta sem saldo comparável.'}</small>}
      </div>)}
      <div><dt>Consolidado — banco / calculado</dt><dd>{data.consolidated.complete && data.consolidated.bank && data.consolidated.calculated ? `${formatBrlDecimal(data.consolidated.bank)} / ${formatBrlDecimal(data.consolidated.calculated)}` : 'Indisponível: falta saldo em alguma conta'}</dd></div>
    </dl>
    <div className="card-overview__grid">{data.accounts.map(account => <AccountCard key={account.accountId} workspaceId={workspaceId} account={account} />)}</div>
  </>
}

function AccountCard({ workspaceId, account }: { workspaceId: string; account: AccountReconciliation }) {
  const [open, setOpen] = useState(false)
  const bank = account.bank
  return <article className="card-overview__card" aria-label={`Conciliação de ${account.name}`}>
    <header>
      <h4>{account.name}</h4>
      <span className={`badge badge--${account.scope.toLowerCase()}`}>{account.scope}</span>
      <span className={`badge badge--status-${account.status.toLowerCase()}`}>{statusLabels[account.status]}</span>
    </header>
    {account.statusReason && <p className="meta">{statusExplanations[account.statusReason] ?? account.statusReason}</p>}
    <dl className="card-overview__facts">
      <div><dt>Saldo informado pelo banco</dt><dd>{bank ? formatBrlDecimal(bank.amount) : 'Sem observação'}</dd></div>
      <div><dt>Saldo calculado pelo Saldo360</dt><dd>{account.calculated.amount ? formatBrlDecimal(account.calculated.amount) : 'Indisponível'}</dd></div>
      <div><dt>Diferença</dt><dd>{account.difference ? formatBrlDecimal(account.difference) : '—'}</dd></div>
      <div><dt>Natureza do saldo</dt><dd>{bank ? kindLabels[bank.kind] ?? bank.kind : '—'}</dd></div>
      <div><dt>Referência bancária</dt><dd>{bank ? reliabilityLabels[bank.reliability] ?? bank.reliability : 'Sem referência'}</dd></div>
      <div><dt>Última sincronização</dt><dd>{account.lastSyncedAt ? formatDate(account.lastSyncedAt.slice(0, 10)) : 'Nunca'}</dd></div>
      <div><dt>Histórico calculável</dt><dd>{account.calculated.calculableFrom ? `desde ${formatDate(account.calculated.calculableFrom)}` : 'Indisponível'}</dd></div>
      <div><dt>Movimentações em revisão</dt><dd>{account.review.count}{account.review.count > 0 ? ` (líquido ${formatBrlDecimal(account.review.netAmount)})` : ''}</dd></div>
    </dl>
    {bank?.overdraft && <p className="meta">Cheque especial: limite {bank.overdraft.contractedLimit ? formatBrlDecimal(bank.overdraft.contractedLimit) : '—'}, em uso {bank.overdraft.usedLimit ? formatBrlDecimal(bank.overdraft.usedLimit) : '—'}. {bank.overdraft.note}</p>}
    {account.calculated.openingSource === 'BalanceAnchor' && <p className="meta">O saldo parte da referência bancária: não é preciso informar saldo inicial manual.</p>}
    {account.review.pending > 0 && <p className="meta">{account.review.pending} movimentação(ões) pendente(s) ou futura(s) não entram no saldo realizado.</p>}
    {account.unexplained && account.status === 'DivergenceFound' && <p className="meta" role="status">Sem explicação: {formatBrlDecimal(account.unexplained)}.</p>}
    {account.consistency.checked > 0 && <p className="meta">{account.consistency.matching} de {account.consistency.checked} observação(ões) anterior(es) conferem com o histórico importado.</p>}
    <button type="button" aria-expanded={open} onClick={() => setOpen(value => !value)}>{open ? 'Ocultar divergências por período' : 'Divergências por período'}</button>
    {open && <Period workspaceId={workspaceId} account={account} />}
  </article>
}

function Period({ workspaceId, account }: { workspaceId: string; account: AccountReconciliation }) {
  const current = todayInSaoPaulo().slice(0, 7)
  const [from, setFrom] = useState(() => { const [y, m] = current.split('-').map(Number); const date = new Date(Date.UTC(y, m - 6, 1)); return `${date.getUTCFullYear()}-${String(date.getUTCMonth() + 1).padStart(2, '0')}` })
  const [through, setThrough] = useState(current)
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${account.accountId}|${from}|${through}|${reload}`
  const [loaded, setLoaded] = useState<Load<ReconciliationPeriod>>({ key, state: 'loading' })
  const visible: Load<ReconciliationPeriod> = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getReconciliationPeriod(workspaceId, account.accountId, from, through, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, account.accountId, from, through, key])

  return <div className="balance-reconciliation__period">
    <div className="form-row">
      <label>De<input type="month" value={from} onChange={event => { if (event.target.value) setFrom(event.target.value) }} /></label>
      <label>Até<input type="month" value={through} onChange={event => { if (event.target.value) setThrough(event.target.value) }} /></label>
    </div>
    {visible.state === 'loading' && <OperationalLoading>Carregando período…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {visible.state === 'loaded' && <>
      <div className="card-planning__scroll"><table className="card-planning__table">
        <caption className="visually-hidden">Abertura de cada mês: calculada pelo Saldo360 e reconstruída pelo histórico do banco</caption>
        <thead><tr><th scope="col">Mês</th><th scope="col">Abertura calculada</th><th scope="col">Abertura pelo banco</th><th scope="col">Diferença</th><th scope="col">Em revisão no mês</th></tr></thead>
        <tbody>{visible.data.months.map(month => <tr key={month.month}>
          <th scope="row">{formatMonthYear(`${month.month}-01`)}</th>
          <td>{month.ledgerOpening ? formatBrlDecimal(month.ledgerOpening) : <span className="meta">{monthReasonLabels[month.ledgerReason ?? ''] ?? 'Indisponível'}{(month.blockingReview ?? 0) > 0 ? ` — resolva ${month.blockingReview} movimentação(ões) em revisão deste mês em diante` : ''}</span>}</td>
          <td>{month.bankOpening ? formatBrlDecimal(month.bankOpening) : <span className="meta">{monthReasonLabels[month.bankReason ?? ''] ?? 'Indisponível'}</span>}</td>
          <td>{month.difference ? formatBrlDecimal(month.difference) : '—'}</td>
          <td>{month.reviewInMonth > 0 ? `${month.reviewInMonth} (${formatBrlDecimal(month.reviewNetInMonth)})` : '0'}</td>
        </tr>)}</tbody>
      </table></div>
      {visible.data.observations.length > 0 && <ul className="card-installments__list" aria-label="Observações do banco no período">
        {visible.data.observations.map(item => <li key={item.observedAt} className="card-installments__chain">
          <strong>{formatDate(item.asOf)}</strong>: banco {formatBrlDecimal(item.observed)}{item.ledger ? ` · calculado ${formatBrlDecimal(item.ledger)}` : ''}{item.bankSide ? ` · histórico importado ${formatBrlDecimal(item.bankSide)}` : ''} — {observationStatusLabels[item.status] ?? item.status}
        </li>)}
      </ul>}
    </>}
  </div>
}
