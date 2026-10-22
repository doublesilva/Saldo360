import { formatBrlDecimal } from '../money'

/** Numbers below determine SVG geometry only. All displayed money is the unchanged API decimal string. */
export function MonthlyFlowSummary({ income, expense }: { income: string | null; expense: string | null }) {
  const display = (value: string | null) => value !== null && /^-?\d+(\.\d+)?$/.test(value) ? formatBrlDecimal(value) : 'Indisponível'
  const entries = income === null || !/^\d+(\.\d+)?$/.test(income) ? NaN : Number(income)
  const exits = expense === null || !/^\d+(\.\d+)?$/.test(expense) ? NaN : Number(expense)
  const valid = Number.isFinite(entries) && Number.isFinite(exits) && Number.isFinite(entries + exits)
  const hasMovement = valid && entries + exits > 0
  const share = hasMovement ? entries / (entries + exits) * 100 : 0
  return <section className="card monthly-summary" aria-labelledby="monthly-summary-title"><div className="card-heading"><h4 id="monthly-summary-title">Resumo do mês</h4><span className="summary-tag">Realizado</span></div>
    <p className="meta">Composição de entradas e saídas realizadas. Não representa saldo nem despesas por categoria.</p>
    <figure className="monthly-summary__figure"><svg className="flow-donut" viewBox="0 0 120 120" role="img" aria-label={hasMovement ? 'Composição das receitas e despesas realizadas; valores na legenda' : valid ? 'Sem movimentação realizada' : 'Resumo visual indisponível'}>
      <circle cx="60" cy="60" r="46" fill="none" stroke="var(--border)" strokeWidth="13" />
      {hasMovement && <><circle cx="60" cy="60" r="46" fill="none" stroke="var(--fail)" strokeWidth="13" /><circle cx="60" cy="60" r="46" fill="none" stroke="var(--ok)" strokeWidth="13" pathLength="100" strokeDasharray={`${share} ${100 - share}`} transform="rotate(-90 60 60)" /></>}
      <text x="60" y="58" textAnchor="middle">{hasMovement ? 'Entradas' : valid ? 'Sem' : 'Dados'}</text><text x="60" y="72" textAnchor="middle">{hasMovement ? 'e saídas' : valid ? 'movimento' : 'indisponíveis'}</text>
    </svg><figcaption><dl className="flow-legend"><div><dt><span className="legend-dot legend-dot--income" />Receitas realizadas</dt><dd>{display(income)}</dd></div><div><dt><span className="legend-dot legend-dot--expense" />Despesas realizadas</dt><dd>{display(expense)}</dd></div></dl>{!hasMovement && <p className="meta">{valid ? 'Nenhuma movimentação realizada neste mês.' : 'Não há dados suficientes para representar a composição.'}</p>}</figcaption></figure>
  </section>
}
