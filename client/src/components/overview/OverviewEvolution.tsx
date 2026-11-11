import { useCallback } from 'react'
import type { PlanMonth, SixMonthPlan } from '../../api/monthlyPlan'
import type { PlanActualScopeChoice } from '../../api/planActual'
import { getSixMonthPlan } from '../../api/monthlyPlan'
import { addCalendarMonths } from '../../dates'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { isNegative, money, monthTitle, periodLabels, shortMonth } from './overviewText'
import { useLoad } from './useLoad'

const kindLabels = { Past: 'Realizado', Current: 'Mês atual', Future: 'Projeção' } as const

/** Um valor de período desconhecido nunca derruba o gráfico: "Historical" (nome da projeção da Agenda) é o mês passado e o resto cai em projeção. */
const kindOf = (value: string): keyof typeof kindLabels => value === 'Historical' ? 'Past' : value in kindLabels ? value as keyof typeof kindLabels : 'Future'

// Geometria do gráfico (0–100 nos dois eixos). parseFloat só posiciona pontos: os valores exibidos continuam sendo o texto exato do servidor.
const PAD = 5
const amountOf = (value: string) => { const parsed = Number.parseFloat(value); return Number.isFinite(parsed) && parsed > 0 ? parsed : 0 }
const xOf = (index: number, count: number) => count < 2 ? 50 : PAD + index * (100 - 2 * PAD) / (count - 1)
const yOf = (value: string, max: number) => max > 0 ? 100 - amountOf(value) / max * 88 : 100

/**
 * Evolução financeira no padrão Folio: receitas (linha cheia) e despesas (tracejada) por competência, do histórico à projeção, na mesma base dos indicadores (consumo, não
 * caixa). A faixa sombreada marca os meses projetados; o rodapé traz o resultado do mês analisado, calculado pelo servidor. A tabela tem os mesmos valores em texto.
 */
export function OverviewEvolution({ plan, competence }: { plan: SixMonthPlan; competence: string }) {
  const months = plan.months
  const max = months.reduce((largest, month) => Math.max(largest, amountOf(month.projectedIncome), amountOf(month.realizedIncome), amountOf(month.projectedExpense), amountOf(month.realizedExpense)), 0)
  const empty = max === 0
  const line = (pick: (month: PlanMonth) => string) => months.map((month, index) => `${xOf(index, months.length)},${yOf(pick(month), max)}`).join(' ')
  const firstFuture = months.findIndex(month => kindOf(month.periodKind) === 'Future')
  const futureFrom = firstFuture < 0 ? null : Math.max(0, xOf(firstFuture, months.length) - (100 - 2 * PAD) / Math.max(1, months.length - 1) / 2)
  const selected = months.find(month => month.competence === competence)
  const result = selected ? kindOf(selected.periodKind) === 'Past' ? selected.realizedResult : selected.projectedResult : null
  const summary = `Receitas e despesas de ${months[0] ? shortMonth(months[0].competence) : ''} a ${months.at(-1) ? shortMonth(months.at(-1)!.competence) : ''}. ${months.map(month => `${shortMonth(month.competence)}: receitas ${money(month.projectedIncome)}, despesas ${money(month.projectedExpense)} (${kindLabels[kindOf(month.periodKind)].toLowerCase()})`).join('; ')}.`
  return <>
    {empty ? <p className="ov-empty">Sem receitas nem despesas neste período.</p>
      : <div className="ov-chart" role="img" aria-label={summary}>
        <svg className="ov-chart__plot" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
          {futureFrom !== null && <rect className="ov-chart__future" x={futureFrom} y="0" width={100 - futureFrom} height="100" />}
          {[12, 34, 56, 78, 100].map(y => <line key={y} className="ov-chart__grid" x1="0" x2="100" y1={y} y2={y} />)}
          <polyline className="ov-chart__line ov-chart__line--expense" points={line(month => month.projectedExpense)} />
          <polyline className="ov-chart__line ov-chart__line--income" points={line(month => month.projectedIncome)} />
        </svg>
        <div className="ov-chart__points" aria-hidden="true">
          {months.map((month, index) => <span key={month.competence}>
            <i className={`ov-chart__dot ov-chart__dot--expense${month.competence === competence ? ' is-selected' : ''}`} style={{ left: `${xOf(index, months.length)}%`, top: `${yOf(month.projectedExpense, max)}%` }} title={`Despesas ${shortMonth(month.competence)}: ${money(month.projectedExpense)}`} />
            <i className={`ov-chart__dot ov-chart__dot--income${month.competence === competence ? ' is-selected' : ''}`} style={{ left: `${xOf(index, months.length)}%`, top: `${yOf(month.projectedIncome, max)}%` }} title={`Receitas ${shortMonth(month.competence)}: ${money(month.projectedIncome)}`} />
          </span>)}
        </div>
        <div className="ov-chart__axis" aria-hidden="true">
          {months.map((month, index) => <span key={month.competence} className={month.competence === competence ? 'is-selected' : undefined} style={{ left: `${xOf(index, months.length)}%` }}>{shortMonth(month.competence)}</span>)}
        </div>
      </div>}
    <footer className="ov-card__foot">
      <span>{selected ? `Resultado orçamentário de ${monthTitle(competence)}${kindOf(selected.periodKind) === 'Past' ? '' : ' (projetado)'}: receitas menos consumo` : 'Resultado orçamentário: receitas menos consumo, calculado pelo servidor'}</span>
      {result !== null && <strong className={`ov-mono${isNegative(result) ? ' ov-negative' : ' ov-positive'}`}>{isNegative(result) ? '' : '+ '}{money(result)}</strong>}
    </footer>
    <details className="ov-details"><summary>Ver valores em tabela</summary>
      <div className="ov-table-wrap"><table className="ov-table"><caption className="visually-hidden">Receitas e consumo orçamentário por mês, realizados e projetados</caption>
        <thead><tr><th scope="col">Mês</th><th scope="col">Situação</th><th scope="col">Receitas realizadas</th><th scope="col">Receitas projetadas</th><th scope="col">Despesas realizadas</th><th scope="col">Despesas projetadas</th></tr></thead>
        <tbody>{months.map(month => <tr key={month.competence}><th scope="row">{shortMonth(month.competence)}</th><td>{periodLabels[kindOf(month.periodKind)]}</td><td>{money(month.realizedIncome)}</td><td>{money(month.projectedIncome)}</td><td>{money(month.realizedExpense)}</td><td>{money(month.projectedExpense)}</td></tr>)}</tbody>
      </table></div></details>
  </>
}

const EVOLUTION_MONTHS_BEFORE = 3

/** O gráfico principal: três meses antes da competência, ela e os dois seguintes, no mesmo escopo. A moldura não muda entre carregando, erro e pronto. */
export function OverviewEvolutionSection({ workspaceId, competence, scope, refreshKey }: { workspaceId: string; competence: string; scope: PlanActualScopeChoice; refreshKey: number }) {
  const from = addCalendarMonths(`${competence}-01`, -EVOLUTION_MONTHS_BEFORE).slice(0, 7)
  const { load, retry } = useLoad(`${workspaceId}|${from}|${scope.kind}:${scope.accountId ?? ''}|${refreshKey}`, useCallback((signal: AbortSignal) => getSixMonthPlan(workspaceId, from, scope, signal), [workspaceId, from, scope]))
  const months = load.state === 'loaded' ? load.data.months : []
  return <section className="ov-card ov-evolution" aria-labelledby="ov-evolution-title">
    <header className="ov-card__head">
      <h3 id="ov-evolution-title">Evolução financeira <small>{months.length > 0 ? `${shortMonth(months[0].competence)} a ${shortMonth(months.at(-1)!.competence)}` : 'seis meses'}</small></h3>
      <ul className="ov-legend" aria-label="Legenda"><li><i className="ov-legend__income" />Receitas</li><li><i className="ov-legend__expense" />Despesas</li><li><i className="ov-legend__future" />Projeção</li></ul>
    </header>
    {/* Só o dado da competência e do escopo atuais: nada de retrato anterior durante a troca. */}
    {load.state === 'loaded' ? <OverviewEvolution plan={load.data} competence={competence} />
      : load.state === 'error' ? <OperationalError error={load.error} onRetry={retry} retryLabel="Recarregar a evolução" /> : <OperationalLoading>Carregando a evolução…</OperationalLoading>}
  </section>
}
