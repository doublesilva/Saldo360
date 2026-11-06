import type { PlanActualDashboard } from '../../api/planActual'
import { isNegative, money, monthTitle } from './overviewText'

/**
 * Receitas e despesas da competência (plan-actual). Ficam nas análises detalhadas: o resumo principal mostra a situação, não os totais do mês.
 * Quando o fluxo de caixa está disponível, os totais são por competência, sem transferências.
 */
export function OverviewIncomeExpense({ data, byCompetence = false }: { data: PlanActualDashboard; byCompetence?: boolean }) {
  const consumption = data.expenses.consumption
  return <section className="ov-indicators ov-indicators--pair" aria-label="Receitas e despesas do mês">
    <Indicator title={byCompetence ? 'Receitas por competência (sem transferências)' : 'Receitas do mês'} value={data.income.realized} tone="income"
      note={data.income.pending !== '0.00' ? `Previsto a receber: ${money(data.income.pending)}` : null} reference={`Realizadas em ${monthTitle(data.competence)}`} />
    <Indicator title={byCompetence ? 'Despesas por competência (sem transferências)' : 'Despesas do mês'} value={consumption.realized} tone="expense"
      note={consumption.pending !== '0.00' ? `Previsto a pagar: ${money(consumption.pending)}` : null} reference={`Incorridas em ${monthTitle(data.competence)} · cartão pelo mês da fatura`} />
  </section>
}

function Indicator({ title, value, tone, note, reference }: { title: string; value: string | null; tone: 'income' | 'expense'; note?: string | null; reference: string }) {
  return <article className={`ov-card ov-indicator ov-indicator--${tone}${isNegative(value) ? ' ov-indicator--negative' : ''}`} aria-label={title}>
    <h4>{title}</h4>
    <p className="ov-indicator__value" data-unavailable={value === null || undefined}>{money(value)}</p>
    {note && <p className="ov-indicator__note">{note}</p>}
    <p className="ov-indicator__reference">{reference}</p>
  </article>
}
