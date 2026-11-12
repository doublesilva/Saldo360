import { useEffect, useMemo, useRef, useState } from 'react'
import { formatBrlDecimal, formatDate } from '../../money'
import { ClassificationReviewList } from '../ClassificationReviewList'
import { OperationalEmpty, OperationalLoading } from '../OperationalFeedback'
import { PluggyReviewPanel } from '../PluggyIntegration'
import {
  BATCH_SIZE, buildDecisionQueue, fetchClassificationPage, fetchImportPage, loadSource, SOURCE_PAGE_SIZE, sourceLabels, type DecisionItem, type SourceState,
} from './queueModel'
import './DecisionQueue.css'

type Sources = { review: SourceState | null; classification: SourceState | null }
const noSources: Sources = { review: null, classification: null }

/**
 * "Requer sua decisão": lista única, somente leitura, das pendências que os fluxos de Contas e de Automação já encaminham para decisão humana.
 * Abrir a fila, carregar mais ou abrir o detalhe nunca sincroniza, confirma ou altera lançamentos: o detalhe é o próprio fluxo existente (com as mesmas confirmações) filtrado no item escolhido.
 * A fila lê todas as páginas das duas fontes (ver queueModel) e mostra em lotes, na ordem global de prioridade.
 */
export function DecisionQueue({ workspaceId, refreshKey = '', onChanged }: { workspaceId: string; refreshKey?: string; onChanged?: () => void }) {
  const [selected, setSelected] = useState<DecisionItem | null>(null)
  const [version, setVersion] = useState(0)
  const [attempt, setAttempt] = useState(0)
  const [shown, setShown] = useState(BATCH_SIZE)
  const [sources, setSources] = useState<Sources>(noSources)
  const key = `${workspaceId}|${refreshKey}|${version}|${attempt}`
  const [loadedKey, setLoadedKey] = useState<string | null>(null)
  const sourcesRef = useRef<Sources>(noSources)
  const retrying = useRef(false)
  const origin = useRef<{ key: string; index: number } | null>(null)
  const restoreFocus = useRef(false)
  const titleRef = useRef<HTMLHeadingElement>(null)
  const listRef = useRef<HTMLUListElement>(null)
  const loading = loadedKey !== key

  useEffect(() => {
    const controller = new AbortController()
    const previous = sourcesRef.current
    const retry = retrying.current
    const read = async (name: keyof Sources, fetchPage: (page: number) => Promise<Awaited<ReturnType<typeof fetchImportPage>>>) => {
      const before = previous[name]
      // Nova tentativa: continua só o que falhou, de onde parou; o que já foi lido por inteiro é mantido.
      if (retry && before && !before.failed) return before
      const fresh = await loadSource(fetchPage, retry && before?.failed ? before : null, controller.signal)
      // Atualização que falhou sem trazer nada: mantém o que já estava na tela, sinalizando a falha.
      if (!retry && fresh.failed && fresh.items.length === 0 && before) return { ...before, failed: true }
      // Falhou antes de saber o total: a fonte segue desconhecida (cobertura parcial, sem inventar contagem).
      return fresh.failed && fresh.total === 0 && fresh.items.length === 0 ? null : fresh
    }
    Promise.all([
      read('review', page => fetchImportPage(workspaceId, page, SOURCE_PAGE_SIZE, controller.signal)),
      read('classification', page => fetchClassificationPage(workspaceId, page, SOURCE_PAGE_SIZE, controller.signal)),
    ]).then(([review, classification]) => {
      if (controller.signal.aborted) return
      const next = { review, classification }
      sourcesRef.current = next; retrying.current = false
      setSources(next); setLoadedKey(key)
    }).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId, key])

  const queue = useMemo(() => loadedKey === null ? null : buildDecisionQueue(sources), [sources, loadedKey])
  const items = useMemo(() => queue?.items ?? [], [queue])
  const visible = items.slice(0, shown)

  // Voltar à fila: foco no item de origem se continua pendente; senão no próximo disponível (mesma posição); senão no título.
  useEffect(() => {
    if (selected || !restoreFocus.current || loading) return
    restoreFocus.current = false
    const target = origin.current
    const keys = items.map(item => item.key)
    const position = target ? (keys.includes(target.key) ? keys.indexOf(target.key) : Math.min(target.index, keys.length - 1)) : -1
    const button = position >= 0 ? listRef.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(keys[position])}"] button`) : null
    ;(button ?? titleRef.current)?.focus()
  }, [selected, loading, items])

  const changed = () => { setVersion(value => value + 1); onChanged?.() }
  const retry = () => { retrying.current = true; setAttempt(value => value + 1) }
  const open = (item: DecisionItem, index: number) => { origin.current = { key: item.key, index }; restoreFocus.current = false; setSelected(item) }
  const back = () => { restoreFocus.current = true; setSelected(null) }
  const title = queue ? ` (${queue.total}${queue.totalComplete ? '' : ', contagem parcial'})` : ''

  if (selected) return <section className="decision-queue" aria-labelledby="decision-queue-title">
    <h4 id="decision-queue-title">Requer sua decisão</h4>
    <button type="button" className="button--link" onClick={back}>‹ Voltar à fila</button>
    <p className="meta">Revisando: <strong>{selected.description}</strong>. Nada muda até você usar uma das ações abaixo.</p>
    {selected.source === 'import'
      ? <PluggyReviewPanel workspaceId={workspaceId} refreshKey={`${refreshKey}|${version}`} only={selected.recordId} onlyPage={selected.page} onlyPageSize={SOURCE_PAGE_SIZE} onChanged={changed} onMissing={changed} />
      : <ClassificationReviewList workspaceId={workspaceId} refreshKey={`${refreshKey}|${version}`} initial={{ kind: 'category' }} only={selected.recordId} onlyPage={selected.page} pageSize={SOURCE_PAGE_SIZE} onChanged={changed} />}
  </section>

  return <section className="decision-queue" aria-labelledby="decision-queue-title">
    <h4 id="decision-queue-title" ref={titleRef} tabIndex={-1}>Requer sua decisão{title}</h4>
    <p className="meta">Pendências que a automação não resolveu sozinha. Primeiro as que estão fora dos totais; depois as demais, da mais recente para a mais antiga. Consultar a fila não sincroniza com o banco nem altera lançamentos.</p>
    {loading && !queue && <OperationalLoading>Carregando as pendências…</OperationalLoading>}
    {queue && queue.items.length === 0 && queue.failedSources.length === 0 && !loading && <OperationalEmpty>Nenhuma pendência requer sua decisão agora.</OperationalEmpty>}
    {visible.length > 0 && <ul className="decision-queue__list" ref={listRef}>
      {visible.map((item, index) => <li key={item.key} data-key={item.key} className={`decision-item decision-item--${item.impact}`}>
        <p className="decision-item__head"><span className="decision-item__type">{item.typeLabel}</span> <strong>{item.description}</strong></p>
        <p className="meta decision-item__meta">
          {item.account && <span>{item.account}</span>}<span>{item.date ? formatDate(item.date) : 'Data não informada'}</span>
          {item.amount !== null && <span className={item.direction === 'Income' ? 'amount amount--income' : 'amount amount--negative'}>{item.direction === 'Income' ? '+' : '−'}{formatBrlDecimal(item.amount)}</span>}
        </p>
        <p className="decision-item__reason"><span>Motivo:</span> {item.reason}</p>
        <p className="decision-item__impact"><span>Impacto:</span> <strong>{item.impactText}</strong></p>
        <button type="button" className="decision-item__action" onClick={() => open(item, index)} aria-label={`Revisar ${item.description}`}>Revisar</button>
      </li>)}
    </ul>}
    {queue && (items.length > 0 || queue.total > 0) && <p className="meta decision-queue__count" role="status">Mostrando {visible.length} de {queue.total}{queue.totalComplete ? '' : ' (contagem parcial)'}</p>}
    {loading && queue && <p className="meta" role="status">Atualizando a fila…</p>}
    {!loading && visible.length < items.length && <button type="button" className="decision-queue__more" onClick={() => setShown(value => value + BATCH_SIZE)}>Carregar mais pendências</button>}
    {!loading && queue && queue.failedSources.length > 0 && <p className="notice notice--warning" role="alert">
      Cobertura parcial: não foi possível carregar {queue.failedSources.map(source => sourceLabels[source]).join(' e ')}. A lista mostra só o que já foi carregado. {' '}
      <button type="button" className="button--link" onClick={retry}>Tentar novamente</button></p>}
  </section>
}
