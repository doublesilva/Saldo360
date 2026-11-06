import { useState } from 'react'
import type { ExpenseDistribution } from '../../api/overview'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { isNegative, money, monthTitle, percentText } from './overviewText'

// Dashboard Folio: para onde foi o dinheiro do mês. O servidor agrupa as despesas realizadas por categoria principal, calcula total e percentuais e confere o total com
// "Despesas do mês"; aqui só se desenha a barra empilhada (larguras = percentuais do servidor) e a lista.

const VISIBLE = 5
const PALETTE_SIZE = 7

export function OverviewExpenseDistribution({ competence, distribution, loading, error, retry, onOpen }: {
  competence: string; distribution: ExpenseDistribution | null; loading: boolean; error: unknown; retry: () => void; onOpen?: () => void
}) {
  const [all, setAll] = useState(false)
  const categories = distribution?.categories ?? []
  const shown = all ? categories : categories.slice(0, VISIBLE)
  const color = (index: number, uncategorized: boolean) => uncategorized ? 'none' : String(index % PALETTE_SIZE + 1)
  const withPercent = categories.length > 0 && categories.every(category => category.percentage !== null)
  return <section className="ov-card ov-distribution" aria-labelledby="ov-distribution-title">
    <header className="ov-card__head">
      <div><h3 id="ov-distribution-title">Distribuição das despesas</h3><p className="ov-meta">{monthTitle(competence)}</p></div>
      {distribution && distribution.total !== null && <p className="ov-distribution__total"><span className="ov-meta">Total gasto</span>
        <strong className={`ov-mono${isNegative(distribution.total) ? ' ov-negative' : ''}`}>{money(distribution.total)}</strong></p>}
    </header>
    {error ? <OperationalError error={error} onRetry={retry} retryLabel="Recarregar" />
      : !distribution ? loading ? <OperationalLoading>Carregando as despesas…</OperationalLoading> : <p className="ov-empty">A distribuição das despesas não está disponível.</p>
        : distribution.state === 'Unavailable' ? <p className="ov-empty" role="alert">Não foi possível agrupar as despesas deste mês.</p>
          : categories.length === 0 ? <p className="ov-empty">Nenhuma despesa realizada em {monthTitle(competence)}.</p>
            : <>
              {withPercent && <div className="ov-stack" aria-hidden="true">{categories.map((category, index) =>
                <span key={category.categoryId ?? 'none'} className={`ov-cat-${color(index, category.categoryId === null)}`} style={{ width: `${category.percentage}%` }} />)}</div>}
              <ul className="ov-distribution__list">{shown.map((category, index) => <li key={category.categoryId ?? 'none'}>
                <span className={`ov-swatch ov-cat-${color(index, category.categoryId === null)}`} aria-hidden="true" />
                <span className="ov-distribution__name">{category.name}</span>
                <span className={`ov-mono${isNegative(category.amount) ? ' ov-negative' : ''}`}>{money(category.amount)}</span>
                {category.percentage !== null && <span className="ov-distribution__pct">{percentText(category.percentage)}</span>}
              </li>)}</ul>
              {categories.length > VISIBLE && <button type="button" className="ov-link" aria-expanded={all} onClick={() => setAll(value => !value)}>{all ? 'Mostrar menos' : `Ver todas as ${categories.length} categorias`}</button>}
              <Notes distribution={distribution} />
            </>}
    {onOpen && <footer className="ov-card__foot"><span /><button type="button" className="ov-link" onClick={onOpen}>Ver lançamentos ›</button></footer>}
  </section>
}

function Notes({ distribution }: { distribution: ExpenseDistribution }) {
  const notes: string[] = []
  if (distribution.reasons.includes('TotalDiffersFromExpenses')) notes.push(`O total difere de Despesas do mês (${money(distribution.reference)}): parte dos gastos ainda não pôde ser distribuída.`)
  if (distribution.reasons.includes('ReferenceUnavailable')) notes.push('Não foi possível conferir o total com Despesas do mês.')
  if (distribution.reasons.includes('PercentagesNotRepresentative')) notes.push('Devoluções superam as compras de alguma categoria: os percentuais não são exibidos.')
  return notes.length === 0 ? null : <>{notes.map(note => <p key={note} className="ov-note" role="status">{note}</p>)}</>
}
