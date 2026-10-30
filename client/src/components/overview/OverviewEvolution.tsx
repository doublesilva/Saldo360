import type { PlanMonth, SixMonthPlan } from '../../api/monthlyPlan'
import { money, periodLabels, scopeLabel, shortMonth } from './overviewText'

const kindLabels = { Past: 'Realizado', Current: 'Mês atual', Future: 'Projeção' } as const

/** Um valor de período desconhecido nunca derruba o painel: "Historical" (nome da projeção da Agenda) é o mês passado e o resto cai em projeção. */
const kindOf = (value: string): keyof typeof kindLabels => value === 'Historical' ? 'Past' : value in kindLabels ? value as keyof typeof kindLabels : 'Future'

/** Altura relativa (0–100) de um valor: só geometria da barra; o valor exibido continua sendo o texto exato do servidor. */
function heightPercent(value: string, max: number): number {
  const parsed = Number.parseFloat(value)
  return max > 0 && Number.isFinite(parsed) && parsed > 0 ? Math.max(1.5, (parsed / max) * 100) : 0
}

function segments(realized: string, projected: string, max: number) {
  const total = heightPercent(projected, max)
  const done = Math.min(total, heightPercent(realized, max))
  return { done, rest: Math.max(0, total - done) }
}

/**
 * Evolução de receitas e despesas: histórico, mês corrente e projeção, lado a lado e na mesma base de cálculo dos indicadores (consumo, não caixa). O trecho
 * hachurado é o que ainda é projetado; a tabela logo abaixo traz os mesmos valores em texto.
 */
export function OverviewEvolution({ plan }: { plan: SixMonthPlan }) {
  const months = plan.months
  const max = months.reduce((largest, month) => Math.max(largest, Number.parseFloat(month.projectedIncome), Number.parseFloat(month.realizedIncome), Number.parseFloat(month.projectedExpense), Number.parseFloat(month.realizedExpense)), 0)
  const first = months[0]?.competence, last = months[months.length - 1]?.competence
  const empty = months.every(month => ['projectedIncome', 'realizedIncome', 'projectedExpense', 'realizedExpense'].every(key => Number.parseFloat(month[key as keyof PlanMonth] as string) === 0))
  const summary = `Receitas e despesas de ${first ? shortMonth(first) : ''} a ${last ? shortMonth(last) : ''}. ${months.map(month => `${shortMonth(month.competence)}: receitas ${money(month.projectedIncome)}, despesas ${money(month.projectedExpense)} (${kindLabels[kindOf(month.periodKind)].toLowerCase()})`).join('; ')}.`
  return <section className="ov-card ov-evolution" aria-labelledby="ov-evolution-title">
    <header><div><h3 id="ov-evolution-title">Evolução financeira</h3>
      <p className="ov-meta">{first && last ? `${shortMonth(first)} a ${shortMonth(last)}` : ''} · {scopeLabel(plan.scope)} · receitas e despesas por competência</p></div>
      <ul className="ov-legend" aria-label="Legenda"><li><i className="ov-swatch ov-swatch--income" />Receitas</li><li><i className="ov-swatch ov-swatch--expense" />Despesas</li><li><i className="ov-swatch ov-swatch--projected" />Projetado</li></ul></header>
    {empty ? <p className="ov-empty">Sem receitas nem despesas neste período.</p>
      : <div className="ov-bars" role="img" aria-label={summary}>
        {months.map(month => {
          const income = segments(month.realizedIncome, month.projectedIncome, max), expense = segments(month.realizedExpense, month.projectedExpense, max)
          return <div key={month.competence} className={`ov-bars__month ov-bars__month--${kindOf(month.periodKind).toLowerCase()}`}>
            <div className="ov-bars__plot" aria-hidden="true">
              <div className="ov-bar ov-bar--income" title={`Receitas ${shortMonth(month.competence)}: ${money(month.projectedIncome)}`}>{income.rest > 0 && <span className="ov-bar__rest" style={{ height: `${income.rest}%` }} />}{income.done > 0 && <span className="ov-bar__done" style={{ height: `${income.done}%` }} />}</div>
              <div className="ov-bar ov-bar--expense" title={`Despesas ${shortMonth(month.competence)}: ${money(month.projectedExpense)}`}>{expense.rest > 0 && <span className="ov-bar__rest" style={{ height: `${expense.rest}%` }} />}{expense.done > 0 && <span className="ov-bar__done" style={{ height: `${expense.done}%` }} />}</div>
            </div>
            <p className="ov-bars__label"><strong>{shortMonth(month.competence)}</strong><small>{kindLabels[kindOf(month.periodKind)]}</small></p>
          </div>
        })}
      </div>}
    <details className="ov-details"><summary>Ver valores em tabela</summary>
      <div className="ov-table-wrap"><table className="ov-table"><caption className="visually-hidden">Receitas e despesas por mês, realizadas e projetadas</caption>
        <thead><tr><th scope="col">Mês</th><th scope="col">Situação</th><th scope="col">Receitas realizadas</th><th scope="col">Receitas projetadas</th><th scope="col">Despesas realizadas</th><th scope="col">Despesas projetadas</th></tr></thead>
        <tbody>{months.map(month => <tr key={month.competence}><th scope="row">{shortMonth(month.competence)}</th><td>{periodLabels[kindOf(month.periodKind)]}</td><td>{money(month.realizedIncome)}</td><td>{money(month.projectedIncome)}</td><td>{money(month.realizedExpense)}</td><td>{money(month.projectedExpense)}</td></tr>)}</tbody>
      </table></div></details>
  </section>
}
