import { useEffect, useState } from 'react'
import { coverageText, getObligationProjection, type MonthlyProjection, type ObligationProjection } from '../api/cardProjection'
import { formatBrlDecimal } from '../money'
import { formatMonthYear, todayInSaoPaulo } from '../dates'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import { CoverageNotice } from './CardOverview'
import '../CardProjection.css'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: ObligationProjection }

/**
 * Evolução mensal das obrigações dos cartões (mês atual + próximos). Separa o que é confirmado (total do banco) do que é projetado e do que é
 * estimado, agrupa por cartão e por conta pagadora e nunca esconde valor por falta de parcela: mostra o conhecido e sinaliza a incompletude.
 */
export function CardPlanning({ workspaceId, refreshKey = 0 }: { workspaceId: string; refreshKey?: number }) {
  const [months, setMonths] = useState(6)
  const [reload, setReload] = useState(0)
  const from = todayInSaoPaulo().slice(0, 7)
  const key = `${workspaceId}|${from}|${months}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getObligationProjection(workspaceId, from, months, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, from, months, key])

  return <section className="card-planning" aria-labelledby="card-planning-title">
    <div className="section-heading">
      <div><h3 id="card-planning-title">Planejamento dos cartões</h3><p className="meta">Obrigações por mês: confirmadas pelo banco, projetadas e estimadas. Nada aqui é lançamento realizado.</p></div>
      <label>Horizonte<select value={months} onChange={event => setMonths(Number.parseInt(event.target.value, 10))}><option value={3}>3 meses</option><option value={6}>6 meses</option><option value={12}>12 meses</option></select></label>
    </div>
    {visible.state === 'loading' && <OperationalLoading>Calculando a projeção…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {visible.state === 'loaded' && <Loaded data={visible.data} />}
  </section>
}

function Loaded({ data }: { data: ObligationProjection }) {
  return <>
    {data.carryover !== '0.00' && <p className="notice notice--warning" role="status">Saldo em atraso de meses anteriores: <strong>{formatBrlDecimal(data.carryover)}</strong> (fora da janela, mas ainda a pagar).</p>}
    <CoverageNotice coverage={data.coverage} />
    <div className="card-planning__scroll">
      <table className="card-planning__table">
        <caption className="visually-hidden">Obrigações mensais dos cartões</caption>
        <thead><tr><th scope="col">Mês</th><th scope="col">Confirmadas (banco)</th><th scope="col">Abertas (projetadas)</th><th scope="col">Parcelas futuras</th><th scope="col">Estimadas</th><th scope="col">Pago</th><th scope="col">A pagar</th><th scope="col">Dados</th></tr></thead>
        <tbody>{data.items.map(item => <Month key={item.month} item={item} />)}</tbody>
      </table>
    </div>
    <h4>Por conta pagadora</h4>
    <Grouping data={data} kind="payer" />
    <h4>Por cartão</h4>
    <Grouping data={data} kind="card" />
  </>
}

function Month({ item }: { item: MonthlyProjection }) {
  const known = item.knownCommitments !== '0.00' ? ` + ${formatBrlDecimal(item.knownCommitments)} conhecidas` : ''
  return <tr>
    <th scope="row">{formatMonthYear(`${item.month}-01`)}</th>
    <td className="confirmed">{formatBrlDecimal(item.confirmedInvoices)}</td>
    <td className="projected">{formatBrlDecimal(item.openInvoicesProjected)}</td>
    <td className="projected">{formatBrlDecimal(item.futureInstallments)}{known}</td>
    <td className="estimated">{formatBrlDecimal(item.estimatedCommitments)}</td>
    <td>{formatBrlDecimal(item.paid)}</td>
    <td><strong>{formatBrlDecimal(item.remaining)}</strong></td>
    <td>{item.coverage.complete ? <span className="badge badge--cycle-closed">Completos</span> : <span className="badge badge--estimated" title={coverageText(item.coverage.reasons).join(' ')}>Incompletos</span>}</td>
  </tr>
}

function Grouping({ data, kind }: { data: ObligationProjection; kind: 'payer' | 'card' }) {
  const rows = new Map<string, { label: string; scope: string | null; cells: Map<string, string> }>()
  for (const month of data.items) {
    if (kind === 'payer') for (const payer of month.byPayer) {
      const id = payer.accountId ?? 'none'
      const row = rows.get(id) ?? { label: payer.name ?? 'Sem conta pagadora', scope: payer.scope, cells: new Map() }
      row.cells.set(month.month, payer.remaining); rows.set(id, row)
    } else for (const card of month.byCard) {
      const row = rows.get(card.cardId) ?? { label: card.name, scope: card.scope, cells: new Map() }
      row.cells.set(month.month, card.remaining); rows.set(card.cardId, row)
    }
  }
  if (rows.size === 0) return <p className="meta">Nenhum cartão no período.</p>
  return <div className="card-planning__scroll"><table className="card-planning__table">
    <thead><tr><th scope="col">{kind === 'payer' ? 'Conta pagadora' : 'Cartão'}</th>{data.items.map(item => <th scope="col" key={item.month}>{formatMonthYear(`${item.month}-01`)}</th>)}</tr></thead>
    <tbody>{[...rows.entries()].map(([id, row]) => <tr key={id}>
      <th scope="row">{row.label}{row.scope && <span className={`badge badge--${row.scope.toLowerCase()}`}>{row.scope}</span>}</th>
      {data.items.map(item => <td key={item.month}>{formatBrlDecimal(row.cells.get(item.month) ?? '0.00')}</td>)}
    </tr>)}</tbody>
  </table></div>
}
