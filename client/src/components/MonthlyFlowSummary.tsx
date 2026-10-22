import { formatBrlDecimal } from '../money'

/** Ratios are chart geometry only; displayed money remains the original API decimal string. */
export function MonthlyFlowSummary({ expense, pendingExpense }: { expense: string | null; pendingExpense: string | null }) {
  const numeric = (value: string | null) => value !== null && /^\d+\.\d{2}$/.test(value) ? Number(value) : NaN
  const realized = numeric(expense), pending = numeric(pendingExpense)
  const valid = Number.isFinite(realized + pending)
  const movement = valid && realized + pending > 0
  const share = movement ? realized / (realized + pending) * 100 : 0
  const percentage = (value: number) => value.toLocaleString('pt-BR', { maximumFractionDigits: 1 }) + '%'
  const display = (value: string | null) => value !== null && /^\d+\.\d{2}$/.test(value) ? formatBrlDecimal(value) : 'Indisponível'
  return <section className="card monthly-summary" aria-labelledby="monthly-summary-title">
    <div className="card-heading"><h4 id="monthly-summary-title">Resumo do mês</h4><span className="summary-tag">Despesas</span></div>
    <p className="meta">Fluxo bancário de despesas: realizadas e pendentes da competência. Receitas não compõem este total. Percentuais visuais aproximados; valores monetários exatos da API.</p>
    <figure className="monthly-summary__figure"><svg className="flow-donut" viewBox="0 0 120 120" role="img" aria-label={movement ? 'Composição das despesas realizadas e pendentes; valores e percentuais na legenda' : valid ? 'Sem despesas conhecidas' : 'Resumo visual indisponível'}>
      <circle cx="60" cy="60" r="46" fill="none" stroke="var(--border)" strokeWidth="13" />
      {movement && <><circle cx="60" cy="60" r="46" fill="none" stroke="var(--warning)" strokeWidth="13" /><circle cx="60" cy="60" r="46" fill="none" stroke="var(--fail)" strokeWidth="13" pathLength="100" strokeDasharray={`${share} ${100 - share}`} transform="rotate(-90 60 60)" /></>}
      <text x="60" y="58" textAnchor="middle">{movement ? 'Despesas' : valid ? 'Sem' : 'Dados'}</text><text x="60" y="72" textAnchor="middle">{movement ? 'do mês' : valid ? 'despesas' : 'indisponíveis'}</text>
    </svg><figcaption><dl className="flow-legend"><div><dt><span className="legend-dot legend-dot--expense" />Despesas realizadas</dt><dd>{display(expense)} · {movement ? percentage(share) : '—'}</dd></div><div><dt><span className="legend-dot legend-dot--pending" />Despesas pendentes</dt><dd>{display(pendingExpense)} · {movement ? percentage(100 - share) : '—'}</dd></div></dl>
      {!movement && <p className="meta">{valid ? 'Nenhuma despesa conhecida nesta competência.' : 'Não há dados suficientes para representar a composição.'}</p>}
    </figcaption></figure>
    <p className="meta">Sem agrupamento por categoria neste contrato. Compras e parcelas de cartão não são somadas novamente aos pagamentos bancários de fatura. Não representa saldo disponível.</p>
  </section>
}
