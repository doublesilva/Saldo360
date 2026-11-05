import type { ReactNode } from 'react'
import type { ProjectionMonth, ProjectionScenario } from '../api/projection'
import { formatMonthYear } from '../dates'
import { formatDate, isNegativeDecimal } from '../money'
import { incomeSourceLabels, money, periodLabels } from './monthlyPlanText'
import { monthOf, scenarioLabels } from './projectionText'

/** Valor da API com tom: negativo, positivo ou indisponível (nulo nunca é R$ 0,00). */
export function Amount({ value, signed = false }: { value: string | null; signed?: boolean }) {
  const tone = value === null ? 'unavailable' : isNegativeDecimal(value) ? 'negative' : signed && /[1-9]/.test(value) ? 'positive' : 'neutral'
  return <span className={`proj-amount proj-amount--${tone}`}>{money(value)}</span>
}

const overdraftValue = (month: ProjectionMonth, pick: (value: NonNullable<ProjectionMonth['overdraft']>) => string | null) => month.overdraft ? pick(month.overdraft) : null

/** Indicadores do mês selecionado, um cartão por cenário. Resultado do mês e saldo final acumulado ficam em rótulos distintos. */
export function ProjectionIndicators({ scenarios, competence }: { scenarios: ProjectionScenario[]; competence: string }) {
  return <div className="proj-indicators">
    {scenarios.map(scenario => {
      const month = monthOf(scenario, competence)
      if (!month) return null
      const unavailable = month.overdraft === null
      return <section key={scenario.name} className={`proj-indicator proj-indicator--${scenario.name}`} aria-label={`Indicadores do cenário ${scenarioLabels[scenario.name]}`}>
        <h5>{scenarioLabels[scenario.name]}<span className={`proj-badge ${month.complete ? '' : 'proj-badge--partial'}`}>{month.complete ? 'Cobertura completa' : 'Projeção parcial'}</span></h5>
        <dl>
          <div><dt>Saldo final PF do mês</dt><dd><Amount value={month.pf.closing} /></dd></div>
          <div><dt>Resultado financeiro do mês</dt><dd><Amount value={month.pf.result} signed /></dd></div>
          <div><dt>Limite do cheque especial utilizado</dt><dd><Amount value={overdraftValue(month, value => value.usedEnd)} /></dd></div>
          <div><dt>Recuperação mensal do limite</dt><dd><Amount value={overdraftValue(month, value => value.monthlyRecovery)} signed /></dd></div>
          <div><dt>Recuperação acumulada</dt><dd><Amount value={overdraftValue(month, value => value.cumulativeRecovery)} signed /></dd></div>
          <div><dt>Saldo positivo</dt><dd><Amount value={overdraftValue(month, value => value.positiveBalance)} signed /></dd></div>
        </dl>
        {unavailable && <p className="meta">Sem saldo ou limite de cheque especial conhecido para este mês: os indicadores do limite estão indisponíveis.</p>}
        {month.overdraft && isNegativeDecimal(month.overdraft.monthlyRecovery) && <p className="proj-worse" role="note">A utilização do limite aumentou neste mês.</p>}
      </section>
    })}
  </div>
}

/** Tabela compacta de todos os meses: os mesmos valores dos gráficos, em texto, com seleção do mês para detalhamento. */
export function ProjectionSummaryTable({ scenarios, selected, onSelect }: { scenarios: ProjectionScenario[]; selected: string; onSelect: (competence: string) => void }) {
  const months = scenarios[0]?.months ?? []
  return <div className="monthly-plan__table-wrap">
    <table className="monthly-plan__table monthly-plan__table--months proj-summary">
      <caption className="sr-only">Saldo PF e cheque especial no fechamento de cada mês, por cenário</caption>
      <thead><tr><th scope="col">Indicador</th>{months.map(month => <th scope="col" key={month.competence} aria-current={month.competence === selected || undefined}>
        <button type="button" className="proj-month-button" aria-pressed={month.competence === selected} onClick={() => onSelect(month.competence)}>{formatMonthYear(`${month.competence}-01`)}</button>
        <small>{periodLabels[month.periodKind]}</small></th>)}</tr></thead>
      <tbody>{scenarios.flatMap(scenario => [
        <tr key={`${scenario.name}-closing`}><th scope="row">{scenarioLabels[scenario.name]} · Saldo final PF</th>{scenario.months.map(month => <td key={month.competence}><Amount value={month.pf.closing} /></td>)}</tr>,
        <tr key={`${scenario.name}-used`}><th scope="row">{scenarioLabels[scenario.name]} · Cheque especial usado</th>{scenario.months.map(month => <td key={month.competence}><Amount value={overdraftValue(month, value => value.usedEnd)} /></td>)}</tr>,
      ])}</tbody>
    </table>
  </div>
}

type Row = { label: string; cell: (month: ProjectionMonth) => ReactNode; note?: string }
type Section = { title: string; rows: Row[]; footnote?: string }

const cash = (pick: (month: ProjectionMonth) => string | null, signed = false) => (month: ProjectionMonth) => <Amount value={pick(month)} signed={signed} />
const text = (pick: (month: ProjectionMonth) => string) => (month: ProjectionMonth) => pick(month)

const sections: Section[] = [
  { title: 'Receita PJ', rows: [
    { label: 'Competência trabalhada', cell: text(m => m.income.workedCompetence ? formatMonthYear(`${m.income.workedCompetence}-01`) : '—') },
    { label: 'Receita esperada (bruta)', cell: cash(m => m.income.expectedGross ?? null) },
    { label: 'Receita recebida na conta', cell: cash(m => m.income.realizedIncome) },
    { label: 'Situação da receita', cell: text(m => (incomeSourceLabels[m.income.source] ?? m.income.source) + (m.income.inferred ? ' · inferida' : '')) },
    { label: 'Recebimento previsto', cell: text(m => m.income.paymentDate ? formatDate(m.income.paymentDate) : '—') },
    { label: 'Despesas PJ pendentes', cell: cash(m => m.pj.pendingExpense) },
    { label: 'Reserva de tributos', cell: m => m.pj.taxObligationRegistered ? 'Tributo já cadastrado: reserva não aplicada' : <Amount value={m.pj.taxReserve} /> },
    { label: 'Reserva operacional', cell: cash(m => m.pj.operationalReserve) },
    { label: 'Repasse PJ → PF projetado', cell: m => <>{money(m.transfers.autoProjectedPjToPf ?? '0.00')}{m.transfers.dependsOnInferredIncome ? ' (depende de receita inferida)' : ''}</> },
    { label: 'Repasse PJ → PF previsto', cell: cash(m => m.transfers.plannedPjToPf) },
    { label: 'Repasse PJ → PF realizado', cell: cash(m => m.transfers.realizedPjToPf) },
  ] },
  { title: 'Orçamento PF', footnote: 'Consumo no cartão é orçamento da competência da fatura; ele só sai da conta no vencimento. A provisão de alimentação (diferença ainda não reconhecida na fatura) está dentro do complemento de consumo dos cartões, que nos Tetos também contém o complemento até o teto.', rows: [
    { label: 'Despesas fora dos cartões', cell: cash(m => m.pf.outsideCardsExpenses) },
    { label: 'Complemento fora dos cartões (Tetos)', cell: cash(m => m.pf.outsideCardsComplement) },
    { label: 'Consumo dos cartões', cell: cash(m => m.pf.cardConsumption) },
    { label: 'Complemento do consumo dos cartões (provisão de alimentação e Tetos)', cell: cash(m => m.pf.cardConsumptionComplement) },
    { label: 'Teto dos cartões', cell: cash(m => m.budget.cardTarget) },
    { label: 'Cartões projetados', cell: cash(m => m.budget.cardProjected) },
    { label: 'Margem dos cartões', cell: cash(m => m.budget.cardMargin) },
    { label: 'Excesso nos cartões', cell: cash(m => m.budget.cardExcess) },
    { label: 'Teto fora dos cartões', cell: cash(m => m.budget.outsideTarget) },
    { label: 'Fora dos cartões projetado', cell: cash(m => m.budget.outsideProjected) },
    { label: 'Margem fora dos cartões', cell: cash(m => m.budget.outsideMargin) },
    { label: 'Excesso fora dos cartões', cell: cash(m => m.budget.outsideExcess) },
  ] },
  { title: 'Fluxo de caixa PF', footnote: 'Só o pagamento da fatura sai do caixa; o consumo do cartão não é desembolso bancário.', rows: [
    { label: 'Saldo inicial', cell: cash(m => m.pf.opening) },
    { label: 'Entradas', cell: cash(m => m.pf.entries) },
    { label: 'Repasses recebidos da PJ', cell: cash(m => m.pf.transfersIn) },
    { label: 'Desembolsos fora dos cartões', cell: cash(m => m.pf.outsideCardsExpenses) },
    { label: 'Pagamentos de faturas', cell: cash(m => m.pf.cardPaymentsInCash) },
    { label: 'Resultado do mês', cell: cash(m => m.pf.result, true) },
    { label: 'Saldo final', cell: cash(m => m.pf.closing) },
  ] },
  { title: 'Cheque especial', rows: [
    { label: 'Limite contratado', cell: m => <Amount value={overdraftValue(m, value => value.limit)} /> },
    { label: 'Utilização na abertura', cell: m => <Amount value={overdraftValue(m, value => value.usedStart)} /> },
    { label: 'Utilização no fechamento', cell: m => <Amount value={overdraftValue(m, value => value.usedEnd)} /> },
    { label: 'Limite disponível', cell: m => <Amount value={overdraftValue(m, value => value.available)} /> },
    { label: 'Recuperação mensal', cell: m => <Amount value={overdraftValue(m, value => value.monthlyRecovery)} signed /> },
    { label: 'Recuperação acumulada', cell: m => <Amount value={overdraftValue(m, value => value.cumulativeRecovery)} signed /> },
    { label: 'Saldo positivo', cell: m => <Amount value={overdraftValue(m, value => value.positiveBalance)} signed /> },
  ] },
]

/** Detalhamento do mês selecionado: um bloco por assunto e uma coluna por cenário visível. Os valores são os da API. */
export function ProjectionDetail({ scenarios, competence }: { scenarios: ProjectionScenario[]; competence: string }) {
  const title = formatMonthYear(`${competence}-01`)
  return <section className="proj-detail" aria-labelledby="proj-detail-title">
    <h4 id="proj-detail-title">Detalhamento · {title}</h4>
    {sections.map(section => <div key={section.title} className="monthly-plan__section">
      <h5>{section.title}</h5>
      <div className="monthly-plan__table-wrap">
        <table className="monthly-plan__table proj-detail__table">
          <caption className="sr-only">{section.title} de {title}</caption>
          <thead><tr><th scope="col">Item</th>{scenarios.map(scenario => <th scope="col" key={scenario.name}>{scenarioLabels[scenario.name]}</th>)}</tr></thead>
          <tbody>{section.rows.map(row => <tr key={row.label}><th scope="row">{row.label}</th>{scenarios.map(scenario => {
            const month = monthOf(scenario, competence)
            return <td key={scenario.name} data-label={scenarioLabels[scenario.name]}>{month ? row.cell(month) : '—'}</td>
          })}</tr>)}</tbody>
        </table>
      </div>
      {section.footnote && <p className="meta">{section.footnote}</p>}
    </div>)}
  </section>
}
