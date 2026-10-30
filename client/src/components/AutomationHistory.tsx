import { useEffect, useState } from 'react'
import {
  failureLabels, listAutomationRuns, runKindLabels, runStateLabels, runTriggerLabels, type AutomationRun, type RunKind, type RunPage, type RunState,
} from '../api/pluggyAutomation'
import { describeSyncError } from '../api/pluggySyncStatus'
import { aiStopLabels } from '../pluggyFormat'
import { countsText, durationText, whenSp } from '../automationFormat'
import { OperationalEmpty, OperationalError, OperationalLoading } from './OperationalFeedback'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: RunPage }

const PAGE_SIZE = 10

/** Motivo da falha ou da pendência em frase: só códigos curtos, nunca resposta do provedor, payload ou token. */
function reasonOf(run: AutomationRun): string | null {
  if (run.failureCode) return failureLabels[run.failureCode] ?? run.failureCode
  if (run.sync?.errorSummary) return describeSyncError(run.sync.errorSummary)
  if (run.categorization?.aiStoppedBecause) return aiStopLabels[run.categorization.aiStoppedBecause] ?? run.categorization.aiStoppedBecause
  return null
}

/** Histórico real das execuções (sincronizações e categorizações), paginado e filtrável. Ler o histórico nunca inicia uma execução. */
export function AutomationHistory({ workspaceId, refreshKey, initialKind = '' }: { workspaceId: string; refreshKey: string; initialKind?: '' | RunKind }) {
  const [kind, setKind] = useState<'' | RunKind>(initialKind)
  const [state, setState] = useState<'' | RunState>('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [page, setPage] = useState(1)
  const [reload, setReload] = useState(0)
  const invalidPeriod = from !== '' && to !== '' && from > to
  const key = `${workspaceId}|${refreshKey}|${kind}|${state}|${from}|${to}|${page}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }

  useEffect(() => {
    if (invalidPeriod) return
    const controller = new AbortController()
    listAutomationRuns(workspaceId, { kind, state, from, to, page, pageSize: PAGE_SIZE }, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, key, kind, state, from, to, page, invalidPeriod])

  const filter = (change: () => void) => { change(); setPage(1) }
  const data = visible.state === 'loaded' ? visible.data : null
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1
  const filtered = kind !== '' || state !== '' || from !== '' || to !== ''

  return <section className="card" aria-labelledby="automation-history-title">
    <h4 id="automation-history-title">Histórico de execuções</h4>
    <form className="automation-history__filters" onSubmit={event => event.preventDefault()} aria-label="Filtros do histórico">
      <label>Automação<select value={kind} onChange={event => filter(() => setKind(event.target.value as '' | RunKind))}>
        <option value="">Todas</option>{(Object.keys(runKindLabels) as RunKind[]).map(item => <option key={item} value={item}>{runKindLabels[item]}</option>)}</select></label>
      <label>Resultado<select value={state} onChange={event => filter(() => setState(event.target.value as '' | RunState))}>
        <option value="">Todos</option>{(Object.keys(runStateLabels) as RunState[]).map(item => <option key={item} value={item}>{runStateLabels[item]}</option>)}</select></label>
      <label>De<input type="date" value={from} onChange={event => filter(() => setFrom(event.target.value))} /></label>
      <label>Até<input type="date" value={to} onChange={event => filter(() => setTo(event.target.value))} /></label>
      <button type="button" onClick={() => setReload(value => value + 1)}>Atualizar</button>
      {filtered && <button type="button" onClick={() => { setKind(''); setState(''); setFrom(''); setTo(''); setPage(1) }}>Limpar filtros</button>}
    </form>
    {invalidPeriod && <p role="alert" className="field-error">O início do período não pode ser posterior ao fim.</p>}
    {!invalidPeriod && visible.state === 'loading' && <OperationalLoading>Carregando o histórico…</OperationalLoading>}
    {!invalidPeriod && visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {!invalidPeriod && data && data.items.length === 0 && <OperationalEmpty>{filtered ? 'Nenhuma execução encontrada com estes filtros.' : 'Nenhuma execução registrada ainda. As sincronizações e categorizações aparecem aqui quando acontecerem.'}</OperationalEmpty>}
    {!invalidPeriod && data && data.items.length > 0 && <>
      <ul className="automation-history">
        {data.items.map(run => {
          const reason = reasonOf(run)
          return <li key={`${run.kind}-${run.id}`} className="automation-history__item">
            <div className="automation-history__head">
              <strong>{runKindLabels[run.kind]}</strong>
              <span className={`badge ${run.state === 'Completed' ? 'badge--sync-completed' : run.state === 'Failed' ? 'badge--sync-failed' : 'badge--sync-running'}`}>{runStateLabels[run.state]}</span>
              <span className="meta">{runTriggerLabels[run.trigger]}</span>
            </div>
            <dl className="card-overview__facts">
              <div><dt>Início</dt><dd>{whenSp(run.startedAt)}</dd></div>
              <div><dt>Término</dt><dd>{run.finishedAt ? whenSp(run.finishedAt) : run.state === 'Running' ? 'Em andamento' : 'Não registrado'}</dd></div>
              <div><dt>Duração</dt><dd>{durationText(run.durationSeconds)}</dd></div>
              <div><dt>Escopo</dt><dd>{run.scope ?? '—'}</dd></div>
            </dl>
            {reason && <p className="meta" role="status">{run.state === 'Failed' ? 'Motivo: ' : 'Observação: '}{reason}</p>}
            <details><summary>Detalhes</summary>
              <p className="meta">{countsText(run)}</p>
              {run.relatedRunId && <p className="meta">Etapa executada depois de uma sincronização (as duas execuções aparecem separadas no histórico, com contagens próprias).</p>}
              {run.categorization?.model && <p className="meta">Modelo: {run.categorization.model}</p>}
            </details>
          </li>
        })}
      </ul>
      <nav className="automation-history__pager" aria-label="Paginação do histórico">
        <button type="button" disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Anteriores</button>
        <span className="meta">Página {data.page} de {pages} · {data.total} execução(ões)</span>
        <button type="button" disabled={page >= pages} onClick={() => setPage(value => value + 1)}>Próximas</button>
      </nav>
    </>}
  </section>
}
