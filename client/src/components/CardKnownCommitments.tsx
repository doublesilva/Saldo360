import { useEffect, useState } from 'react'
import { getCardPortfolio, type CardPortfolioItem } from '../api/creditCards'
import { formatBrlDecimal } from '../money'

export function CardKnownCommitments({ workspaceId, cardId, month }: { workspaceId: string; cardId: string; month: string }) {
  const [rows, setRows] = useState<{ month: string; card: CardPortfolioItem }[]>([])
  const [error, setError] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    const months = Array.from({ length: 6 }, (_, index) => { const [y, m] = month.split('-').map(Number); const date = new Date(Date.UTC(y, m - 1 + index, 1)); return date.toISOString().slice(0, 7) })
    Promise.all(months.map(async value => ({ month: value, card: (await getCardPortfolio(workspaceId, value, controller.signal)).cards.find(c => c.cardId === cardId) })))
      .then(data => { if (!controller.signal.aborted) setRows(data.filter((r): r is { month: string; card: CardPortfolioItem } => !!r.card)) })
      .catch(() => { if (!controller.signal.aborted) setError(true) })
    return () => controller.abort()
  }, [workspaceId, cardId, month])
  return <section aria-label="Compromissos conhecidos"><h5>Compromissos conhecidos — seis meses</h5><p className="meta">Faturas vinculadas + parcelas importadas sem fatura. Este total não é uma fatura oficial fechada nem valor líquido após pagamentos. O limite inclui também outros períodos e desconta pagamentos.</p>
    {error && <p role="alert">Não foi possível carregar a composição.</p>}
    {rows.map(r => <div className="invoice-details" key={r.month}><h6>{r.month}</h6><p>Faturas vinculadas: {formatBrlDecimal(r.card.invoiceTotal)} · Importadas sem vínculo: {formatBrlDecimal(r.card.unlinkedPeriodAmount)} · Total conhecido: {formatBrlDecimal(r.card.knownPeriodTotal)}</p>
      {r.card.unassignedCompetenceCount > 0 && <p role="status">Cobertura parcial: {r.card.unassignedCompetenceCount} obrigações sem competência, não somadas ao mês.</p>}
      <ul>{r.card.unlinkedObligations.map(o => <li key={o.id}>Importada — {o.description} · {o.number ?? '?'}/{o.count ?? '?'} · {formatBrlDecimal(o.amount)} · vencimento {o.dueDate ?? 'não informado'} · sem fatura · origem {o.sourceSystem ?? 'não informada'}{!o.competence && ' · competência desconhecida'}</li>)}</ul>
    </div>)}
  </section>
}
