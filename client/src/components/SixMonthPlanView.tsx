import { useEffect, useState } from 'react'
import { getSixMonthPlan, type PlanMonth, type PlanScopeChoice, type SixMonthPlan } from '../api/monthlyPlan'
import type { PlanActualScope } from '../api/planActual'
import { formatMonthYear } from '../dates'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import { coverageLabels, money, periodLabels, plannedMoney, riskLabels, riskReasonText } from './monthlyPlanText'
import '../MonthlyPlan.css'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: SixMonthPlan }

type Row = { label: string; group: string; cell: (month: PlanMonth) => string }
const rows: Row[] = [
  { group: 'Receitas', label: 'Planejadas', cell: m => plannedMoney(m.plannedIncome) },
  { group: 'Receitas', label: 'Projetadas', cell: m => money(m.projectedIncome) },
  { group: 'Receitas', label: 'Realizadas', cell: m => money(m.realizedIncome) },
  { group: 'Despesas', label: 'Planejadas', cell: m => plannedMoney(m.plannedExpense) },
  { group: 'Despesas', label: 'Projetadas', cell: m => money(m.projectedExpense) },
  { group: 'Despesas', label: 'Realizadas', cell: m => money(m.realizedExpense) },
  { group: 'Resultado', label: 'Planejado', cell: m => plannedMoney(m.plannedResult) },
  { group: 'Resultado', label: 'Projetado', cell: m => money(m.projectedResult) },
  { group: 'Resultado', label: 'Realizado', cell: m => money(m.realizedResult) },
  { group: 'Obrigações', label: 'Conhecidas no mês', cell: m => money(m.knownObligations) },
  { group: 'Obrigações', label: 'Saldo projetado no fechamento', cell: m => money(m.closingBalance) },
  { group: 'Cobertura', label: 'Das obrigações', cell: m => coverageLabels[m.coverage.obligationsCoverage] },
  { group: 'Cobertura', label: 'Dos dados', cell: m => m.coverage.dataComplete ? 'Completa' : 'Parcial' },
]

/** Visão de seis meses: o mês selecionado e os cinco seguintes, com planejado, projetado, realizado e risco. Tudo vem do backend; o navegador só apresenta. */
export function SixMonthPlanView({ workspaceId, from, scope, refreshKey, onScope }: { workspaceId: string; from: string; scope: PlanScopeChoice; refreshKey: number; onScope: (choices: PlanActualScope) => void }) {
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${from}|${scope.kind}|${scope.accountId ?? ''}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getSixMonthPlan(workspaceId, from, scope, controller.signal)
      .then(data => { if (!controller.signal.aborted) { setLoaded({ key, state: 'loaded', data }); onScope(data.scope) } })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, from, scope, key]) // eslint-disable-line react-hooks/exhaustive-deps

  if (visible.state === 'loading') return <OperationalLoading>Carregando o planejamento de seis meses…</OperationalLoading>
  if (visible.state === 'error') return <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />
  const plan = visible.data
  const { summary } = plan
  const reasons = [...new Set(plan.months.flatMap(month => month.riskReasons))]
  return <div className="monthly-plan">
    <p className="meta">{formatMonthYear(`${plan.from}-01`)} a {formatMonthYear(`${plan.through}-01`)} · {plan.scope.kind === 'all' ? 'Todas as contas' : plan.scope.accountName ?? 'Conta'}</p>
    <section className="monthly-plan__summary" aria-label="Resumo dos seis meses">
      {summary.highestRiskMonths.length > 0
        ? <p role="status"><strong>Meses de maior risco:</strong> {summary.highestRiskMonths.map(month => formatMonthYear(`${month}-01`)).join(', ')}.</p>
        : <p role="status">Nenhum mês com risco alto ou médio identificado.</p>}
      <p className="meta">{summary.monthsWithoutPlan} mês(es) sem planejamento · resultado projetado no período: <strong>{money(summary.projectedResult)}</strong>{summary.plannedResult !== null ? ` · planejado: ${money(summary.plannedResult)}` : ''}</p>
    </section>
    <div className="monthly-plan__table-wrap">
      <table className="monthly-plan__table monthly-plan__table--months">
        <caption className="sr-only">Planejamento de seis meses: receitas, despesas, resultado, obrigações, cobertura e risco por competência</caption>
        <thead><tr><th scope="col">Indicador</th>{plan.months.map(month => <th scope="col" key={month.competence}>{formatMonthYear(`${month.competence}-01`)}<small>{periodLabels[month.periodKind]}</small></th>)}</tr></thead>
        <tbody>
          {rows.map(row => <tr key={`${row.group}-${row.label}`}><th scope="row">{row.group} · {row.label}</th>{plan.months.map(month => <td key={month.competence}>{row.cell(month)}</td>)}</tr>)}
          <tr className="monthly-plan__risk-row"><th scope="row">Risco do mês</th>{plan.months.map(month => <td key={month.competence}><span className={`monthly-plan__risk monthly-plan__risk--${month.risk}`}>{riskLabels[month.risk]}</span></td>)}</tr>
        </tbody>
      </table>
    </div>
    {reasons.length > 0 && <section aria-label="Motivos de risco"><h5>O que sinaliza risco</h5><ul className="plan-card__notes">{plan.months.filter(month => month.riskReasons.length > 0).map(month =>
      <li key={month.competence}><strong>{formatMonthYear(`${month.competence}-01`)}:</strong> {month.riskReasons.map(reason => riskReasonText[reason] ?? reason).join(' ')}</li>)}</ul></section>}
    <p className="meta">Meses futuros mostram apenas os compromissos já conhecidos (faturas, parcelas, previsões e recorrências materializadas): nenhuma receita ou despesa é inventada para preencher o período. O plano é meta e não altera o saldo projetado da Agenda.</p>
  </div>
}
