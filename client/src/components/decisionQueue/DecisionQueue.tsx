import { useCallback, useState } from 'react'
import { formatBrlDecimal, formatDate } from '../../money'
import { useLoad } from '../overview/useLoad'
import { ClassificationReviewList } from '../ClassificationReviewList'
import { OperationalEmpty, OperationalError, OperationalLoading } from '../OperationalFeedback'
import { PluggyReviewPanel } from '../PluggyIntegration'
import { loadDecisionQueue, PAGE_SIZE, sourceLabels, type DecisionItem } from './queueModel'
import './DecisionQueue.css'

/**
 * "Requer sua decisão": lista única, somente leitura, das pendências que os fluxos de Contas e de Automação já encaminham para decisão humana.
 * Abrir a fila ou o detalhe nunca sincroniza, confirma ou altera lançamentos: o detalhe é o próprio fluxo existente (com as mesmas confirmações) filtrado no item escolhido.
 */
export function DecisionQueue({ workspaceId, refreshKey = '', onChanged }: { workspaceId: string; refreshKey?: string; onChanged?: () => void }) {
  const [selected, setSelected] = useState<DecisionItem | null>(null)
  const [version, setVersion] = useState(0)
  const { load, previous, retry } = useLoad(`${workspaceId}|${refreshKey}|${version}`, useCallback((signal: AbortSignal) => loadDecisionQueue(workspaceId, signal), [workspaceId]))
  const queue = load.state === 'loaded' ? load.data : previous
  const changed = () => { setVersion(value => value + 1); onChanged?.() }

  if (selected) return <section className="decision-queue" aria-labelledby="decision-queue-title">
    <h4 id="decision-queue-title">Requer sua decisão</h4>
    <button type="button" className="button--link" onClick={() => setSelected(null)}>‹ Voltar à fila</button>
    <p className="meta">Revisando: <strong>{selected.description}</strong>. Nada muda até você usar uma das ações abaixo.</p>
    {selected.source === 'import'
      ? <PluggyReviewPanel workspaceId={workspaceId} refreshKey={`${refreshKey}|${version}`} only={selected.recordId} onChanged={changed} onMissing={changed} />
      : <ClassificationReviewList workspaceId={workspaceId} refreshKey={`${refreshKey}|${version}`} initial={{ kind: 'category' }} only={selected.recordId} pageSize={PAGE_SIZE} onChanged={changed} />}
  </section>

  return <section className="decision-queue" aria-labelledby="decision-queue-title">
    <h4 id="decision-queue-title">Requer sua decisão{queue ? ` (${queue.total})` : ''}</h4>
    <p className="meta">Pendências que a automação não resolveu sozinha. Primeiro as que afetam os totais. Consultar a fila não sincroniza com o banco nem altera lançamentos.</p>
    {load.state === 'loading' && !queue && <OperationalLoading>Carregando as pendências…</OperationalLoading>}
    {load.state === 'loading' && queue && <p className="meta" role="status">Atualizando a fila…</p>}
    {load.state === 'error' && <OperationalError error={load.error} onRetry={retry} />}
    {queue && queue.failedSources.length > 0 && <p className="notice notice--warning" role="alert">
      Cobertura parcial: não foi possível carregar {queue.failedSources.map(source => sourceLabels[source]).join(' e ')}. {' '}
      <button type="button" className="button--link" onClick={retry}>Tentar novamente</button></p>}
    {queue && queue.items.length === 0 && queue.failedSources.length === 0 && <OperationalEmpty>Nenhuma pendência requer sua decisão agora.</OperationalEmpty>}
    {queue && queue.items.length > 0 && <ul className="decision-queue__list">
      {queue.items.map(item => <li key={item.key} className={`decision-item decision-item--${item.impact}`}>
        <p className="decision-item__head"><span className="decision-item__type">{item.typeLabel}</span> <strong>{item.description}</strong></p>
        <p className="meta decision-item__meta">
          {item.account && <span>{item.account}</span>}<span>{item.date ? formatDate(item.date) : 'Data não informada'}</span>
          {item.amount !== null && <span className={item.direction === 'Income' ? 'amount amount--income' : 'amount amount--negative'}>{item.direction === 'Income' ? '+' : '−'}{formatBrlDecimal(item.amount)}</span>}
        </p>
        <p className="decision-item__reason"><span>Motivo:</span> {item.reason}</p>
        <p className="decision-item__impact"><span>Impacto:</span> <strong>{item.impactText}</strong></p>
        <button type="button" className="decision-item__action" onClick={() => setSelected(item)} aria-label={`Revisar ${item.description}`}>Revisar</button>
      </li>)}
    </ul>}
    {queue && queue.total > queue.items.length && <p className="meta">Mostrando {queue.items.length} de {queue.total}. Resolva estes para ver os próximos.</p>}
  </section>
}
