import { useEffect, useState } from 'react'
import { getCardPortfolio, type CardPortfolioItem } from '../api/creditCards'
import { formatBrlDecimal } from '../money'

export function CardKnownCommitments({ workspaceId, cardId, month }: { workspaceId: string; cardId: string; month: string }) {
  const [result, setResult] = useState<{ key: string; rows: { month: string; card: CardPortfolioItem }[]; error: boolean } | null>(null)
  const key = `${workspaceId}|${cardId}|${month}`
  const rows = result?.key === key ? result.rows : []
  const error = result?.key === key && result.error
  useEffect(() => {
    const controller = new AbortController()
    const months = Array.from({ length: 6 }, (_, index) => { const [y, m] = month.split('-').map(Number); const date = new Date(Date.UTC(y, m - 1 + index, 1)); return date.toISOString().slice(0, 7) })
    Promise.all(months.map(async value => ({ month: value, card: (await getCardPortfolio(workspaceId, value, controller.signal)).cards.find(c => c.cardId === cardId) })))
      .then(data => { if (data.some(r => !r.card)) throw new Error('Card unavailable'); if (!controller.signal.aborted) setResult({ key, rows: data as { month: string; card: CardPortfolioItem }[], error: false }) })
      .catch(() => { if (!controller.signal.aborted) setResult({ key, rows: [], error: true }) })
    return () => controller.abort()
  }, [workspaceId, cardId, month, key])
  return <section aria-label="Compromissos conhecidos"><h5>Compromissos conhecidos — seis meses</h5><p className="meta">Faturas vinculadas + parcelas importadas sem fatura. Este total não é uma fatura oficial fechada nem valor líquido após pagamentos. O limite inclui também outros períodos e desconta pagamentos.</p>
    {error && <p role="alert">Não foi possível carregar a composição.</p>}
    {result?.key !== key && <p role="status">Carregando composição…</p>}
    {rows.map(r => <details className="invoice-details" key={r.month} open={r.month === month}><summary>{r.month} · Total conhecido {formatBrlDecimal(r.card.knownPeriodTotal)} · {(r.card.invoices?.reduce((n, i) => n + i.installmentCount, 0) ?? 0) + r.card.unlinkedObligations.filter(o => o.competence?.slice(0, 7) === r.month).length} obrigações · {r.card.incompleteDetailsCount || r.card.unassignedCompetenceCount ? "Cobertura parcial" : "Dados conhecidos"}{r.card.unlinkedObligations.length > 0 && " · pendente de conciliação"}</summary><p>Faturas efetivas: {formatBrlDecimal(r.card.invoiceTotal)} · Sem vínculo: {formatBrlDecimal(r.card.unlinkedPeriodAmount)}</p>
      {r.card.unassignedCompetenceCount > 0 && <p role="status">Cobertura parcial: {r.card.unassignedCompetenceCount} obrigações sem competência, não somadas ao mês.</p>}
      {r.card.incompleteDetailsCount > 0 && <p role="status">Cobertura parcial de informações complementares: {r.card.incompleteDetailsCount} registros. Valores conhecidos preservados.</p>}
      <p>Pagamentos registrados: {formatBrlDecimal(r.card.paidPeriodAmount)} · Pendente conhecido: {formatBrlDecimal(r.card.outstandingPeriodAmount)}{r.card.isArchived && ' · Cartão arquivado: histórico preservado'}</p>
      {r.card.invoices?.map(i => <div key={i.id}><p>Fatura efetiva · vence {i.dueDate} · {i.paymentStatus === 'Paid' ? 'Paga' : i.paymentStatus === 'PartiallyPaid' ? 'Parcialmente paga' : i.status === 'Closed' ? 'Fechada sem pagamento' : 'Aberta'}</p><ul>{i.installments.map(p => <li key={p.installmentId}>{p.purchaseId === '00000000-0000-0000-0000-000000000000' ? 'Parcela importada vinculada' : 'Parcela de compra registrada'} — {p.purchaseDescription} · {p.installmentNumber ?? '?'}/{p.installmentCount ?? '?'} · {formatBrlDecimal(p.amount)}</li>)}</ul></div>)}
      <ul>{r.card.unlinkedObligations.map(o => <li key={o.id}>Importada — {o.description} · {o.number ?? '?'}/{o.count ?? '?'} · {formatBrlDecimal(o.amount)} · vencimento {o.dueDate ?? 'não informado'} · sem fatura · origem {o.sourceSystem ?? 'não informada'}{!o.competence && ' · competência desconhecida'}</li>)}</ul>
    </details>)}
  </section>
}
