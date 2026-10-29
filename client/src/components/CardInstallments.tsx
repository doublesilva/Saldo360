import { useEffect, useState } from 'react'
import { chainStatusLabels, coverageText, getProjectedInstallments, originLabels, type CardInstallments as CardInstallmentsData, type InstallmentChain } from '../api/cardProjection'
import { formatBrlDecimal, formatDate } from '../money'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import '../CardProjection.css'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: CardInstallmentsData }

/**
 * Compras parceladas do cartão: o que já está em fatura, o que o banco devolveu para as próximas e o que foi estimado. Estimativas são identificadas e
 * nunca gravadas como lançamento; série ambígua ou sem evidência mostra o que o banco devolveu e avisa que há parcelas sem projeção.
 */
export function CardInstallments({ workspaceId, cardId, refreshKey = 0 }: { workspaceId: string; cardId: string; refreshKey?: number }) {
  const [reload, setReload] = useState(0)
  // Só consulta quando o usuário abre a seção: o detalhe do cartão não paga uma projeção que ninguém pediu.
  const [opened, setOpened] = useState(false)
  const key = `${workspaceId}|${cardId}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    if (!opened) return
    const controller = new AbortController()
    getProjectedInstallments(workspaceId, cardId, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, cardId, key, opened])

  return <details className="card-installments"><summary onClick={() => setOpened(true)}>Parcelas projetadas (informadas e estimadas)</summary>
    {opened && visible.state === 'loading' && <OperationalLoading>Carregando parcelas…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {visible.state === 'loaded' && ((visible.data.chains ?? []).length === 0
      ? <p className="meta">Nenhuma compra parcelada com parcelas restantes.</p>
      : <>
        {!visible.data.coverage.complete && <p className="meta" role="status">{coverageText(visible.data.coverage.reasons).join(' ')}</p>}
        <ul className="card-installments__list">{visible.data.chains.map(chain => <Chain key={chain.key} chain={chain} />)}</ul>
      </>)}
  </details>
}

function Chain({ chain }: { chain: InstallmentChain }) {
  return <li className="card-installments__chain">
    <div><strong>{chain.description}</strong> <span className="badge">{chainStatusLabels[chain.status]}</span> {chain.estimated > 0 && <span className="badge badge--estimated">{chain.estimated} estimada(s)</span>}</div>
    <p className="meta">{chain.total} parcelas de {formatBrlDecimal(chain.amount)}</p>
    {chain.unprojected > 0 && <p className="meta" role="status">{chain.unprojected} parcela(s) restante(s) sem evidência para projetar: não entram no total.</p>}
    <table className="card-installments__table">
      <thead><tr><th scope="col">Parcela</th><th scope="col">Vence</th><th scope="col">Valor</th><th scope="col">Origem</th></tr></thead>
      <tbody>{chain.installments.map(installment => <tr key={`${installment.number}-${installment.closing}`} className={installment.origin === 'Estimated' ? 'estimated' : undefined}>
        <th scope="row">{installment.number}/{installment.total}</th>
        <td>{formatDate(installment.due)}</td>
        <td>{formatBrlDecimal(installment.amount)}</td>
        <td>{originLabels[installment.origin]}{installment.absorbed ? ' · já absorvida pelo total do banco' : ''}</td>
      </tr>)}</tbody>
    </table>
  </li>
}
