import { useCallback, useEffect, useState, type FormEvent } from 'react'
import {
  getStability, listImported, syncConnection, listSyncRuns,
  type ImportedPage, type PluggyAccount, type PluggyConnection, type Stability, type SyncRun,
} from '../api/pluggyPoc'
import { classificationLabels, dateTime, day, importStatusLabels, money, NOT_INFORMED, syncStatusLabels, text } from '../pluggyFormat'

const PAGE_SIZE = 25
const errorText = (error: unknown) => (error instanceof Error ? error.message : 'Falha inesperada.')

/** Transações da Pluggy guardadas à parte (T09.2). Informativo: não gera lançamentos, previsões nem altera saldos. */
export function PluggyImported({ workspaceId, connection, accounts }: { workspaceId: string; connection: PluggyConnection; accounts: PluggyAccount[] }) {
  const [filter, setFilter] = useState({ accountId: '', from: '', to: '', status: '', classification: '' })
  const [applied, setApplied] = useState(filter)
  const [page, setPage] = useState(1)
  const [data, setData] = useState<ImportedPage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [lastRun, setLastRun] = useState<SyncRun | null>(null)
  const [syncing, setSyncing] = useState(false)
  const [version, setVersion] = useState(0)
  const [stability, setStability] = useState<Stability | null>(null)
  const [stabilityError, setStabilityError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    listImported(workspaceId, { connectionId: connection.id, ...applied, page, pageSize: PAGE_SIZE }, controller.signal)
      .then(result => { setData(result); setError(null) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) setError(errorText(cause)) })
    return () => controller.abort()
  }, [workspaceId, connection.id, applied, page, version])

  useEffect(() => {
    const controller = new AbortController()
    listSyncRuns(workspaceId, connection.id, controller.signal).then(runs => setLastRun(runs[0] ?? null)).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId, connection.id, version])

  const sync = useCallback(async () => {
    setSyncing(true); setError(null)
    try { setLastRun(await syncConnection(workspaceId, connection.id)); setPage(1); setVersion(value => value + 1) }
    catch (cause) { setError(errorText(cause)) }
    finally { setSyncing(false) }
  }, [workspaceId, connection.id])

  const compare = async () => {
    setStabilityError(null)
    try { setStability(await getStability(workspaceId, connection.id)) } catch (cause) { setStabilityError(errorText(cause)) }
  }
  const submit = (event: FormEvent) => { event.preventDefault(); setPage(1); setApplied(filter) }
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1
  const set = (key: keyof typeof filter) => (value: string) => setFilter(previous => ({ ...previous, [key]: value }))

  return <section className="card" aria-labelledby="pluggy-imported-title">
    <h4 id="pluggy-imported-title">6. Transações importadas</h4>
    <p className="meta">Cópia local das transações de {text(connection.institution)}{connection.label ? ` (${connection.label})` : ''}. Contas ainda não integradas ao cadastro ficam só como informação: não criam lançamentos, não atualizam saldos nem geram previsões. Contas integradas (Contas → Nova conta → Pluggy) alimentam o realizado com as transações efetivadas.</p>
    <p><button type="button" onClick={() => void sync()} disabled={syncing}>{syncing ? 'Sincronizando…' : 'Sincronizar agora'}</button></p>
    {lastRun && <p className="meta" role="status">Última sincronização: {syncStatusLabels[lastRun.status] ?? lastRun.status} em {dateTime(lastRun.startedAt)} — {lastRun.created} novas, {lastRun.updated} atualizadas, {lastRun.unchanged} inalteradas, {lastRun.missing} ausentes, {lastRun.errors} erros ({lastRun.accountsSynced}/{lastRun.accountsTotal} contas){lastRun.errorSummary ? ` · ${lastRun.errorSummary}` : ''}</p>}
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {data && <ul aria-label="Contadores" className="meta">
      <li>Total: {data.counters.total}</li><li>Novas na última sincronização: {data.counters.new}</li><li>Atualizadas na última sincronização: {data.counters.updated}</li>
      <li>Pendentes: {data.counters.pending}</li><li>Ausentes da última coleta: {data.counters.missing}</li><li>Última coleta: {dateTime(data.counters.lastSyncAt)}</li>
    </ul>}
    <form onSubmit={submit} aria-label="Filtros das transações importadas">
      <label>Conta <select value={filter.accountId} onChange={event => set('accountId')(event.target.value)}>
        <option value="">Todas</option>{accounts.map(a => <option key={a.id} value={a.id}>{text(a.marketingName ?? a.name)} {a.maskedNumber ?? ''}</option>)}
      </select></label>{' '}
      <label>De <input type="date" value={filter.from} onChange={event => set('from')(event.target.value)} /></label>{' '}
      <label>Até <input type="date" value={filter.to} onChange={event => set('to')(event.target.value)} /></label>{' '}
      <label>Status <select value={filter.status} onChange={event => set('status')(event.target.value)}>
        <option value="">Todos</option><option value="Pending">Pendente</option><option value="Posted">Efetivada</option>
      </select></label>{' '}
      <label>Classificação <select value={filter.classification} onChange={event => set('classification')(event.target.value)}>
        <option value="">Todas</option>{Object.entries(classificationLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
      </select></label>{' '}
      <button type="submit">Filtrar</button>
    </form>
    {data && data.items.length === 0 && <p className="meta">{data.counters.total === 0 ? 'Nenhuma transação importada. Use "Sincronizar agora".' : 'Nenhuma transação para os filtros informados.'}</p>}
    {data && data.items.length > 0 && <div className="planning-table-wrap"><table>
      <caption className="meta">{data.total} transações · página {data.page} de {pages}</caption>
      <thead><tr>{['Banco e conta', 'Data', 'Descrição', 'Valor', 'Status', 'Classificação sugerida', 'Duplicidade', 'Observada em', 'Sinais'].map(h => <th key={h} scope="col">{h}</th>)}</tr></thead>
      <tbody>{data.items.map(t => <tr key={t.id}>
        <td>{text(connection.institution)} · {text(t.accountLabel)}</td>
        <td>{day(t.date)}</td>
        <td>{text(t.description ?? t.descriptionRaw)}{t.totalInstallments && t.totalInstallments > 1 ? <small className="meta"> · parcela {text(t.installmentNumber)}/{t.totalInstallments}</small> : null}</td>
        <td>{money(t.amount, t.currency)}</td>
        <td>{importStatusLabels[t.status] ?? t.status}</td>
        <td title={t.classificationReason}>{classificationLabels[t.classification] ?? t.classification}</td>
        <td>{t.possibleDuplicateKey && t.possibleDuplicateCount > 1 ? <span title="Mesma conta, dia, valor e descrição que outra transação">Possível duplicidade <code>{t.possibleDuplicateKey}</code> ({t.possibleDuplicateCount})</span> : NOT_INFORMED}</td>
        <td>{dateTime(t.lastObservedAt)}</td>
        <td>{[t.isNewInLastSync && 'Nova', t.changedInLastSync && 'Atualizada', t.missingSyncCount > 0 && `Ausente há ${t.missingSyncCount} coleta(s)`].filter(Boolean).join(' · ') || '—'}</td>
      </tr>)}</tbody>
    </table></div>}
    {data && data.total > data.pageSize && <p>
      <button type="button" disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Anterior</button>{' '}
      <button type="button" disabled={page >= pages} onClick={() => setPage(value => value + 1)}>Próxima</button>
    </p>}

    <h5>Estabilidade entre sincronizações</h5>
    <p className="meta">Compara as duas últimas sincronizações concluídas. Exige ao menos duas.</p>
    <button type="button" onClick={() => void compare()}>Comparar sincronizações</button>
    {stabilityError && <p role="alert" className="notice notice--error">{stabilityError}</p>}
    {stability && <ul aria-label="Estabilidade">
      <li>Sincronizações concluídas: {stability.completedRuns}</li>
      <li>IDs preservados na última: {stability.preservedInLatest} de {stability.previouslyImported} já importadas (não vistas: {stability.notSeenInLatest})</li>
      <li>Novas na última: {stability.newInLatest} (com data anterior à coleta anterior: {stability.newWithDateBeforePreviousRun})</li>
      <li>Transições PENDING → POSTED: {stability.pendingToPostedInLatest} na última, {stability.pendingToPostedTotal} no total; outras mudanças de status: {stability.otherStatusChangesInLatest}</li>
      <li>Alterações de valor (total): {stability.amountChangesTotal} · de descrição (total): {stability.descriptionChangesTotal}</li>
      <li>Campos alterados na última: {Object.entries(stability.changedFieldsInLatest).map(([field, count]) => `${field} (${count})`).join(', ') || NOT_INFORMED}</li>
      <li>Com data futura: {stability.futureDated} (pendentes: {stability.futureDatedPending}) · ausentes agora: {stability.missingNow} · pendentes agora: {stability.pendingNow} · grupos de possível duplicidade: {stability.possibleDuplicateGroups}</li>
      {stability.notes.map(note => <li key={note} className="meta">{note}</li>)}
    </ul>}
  </section>
}
