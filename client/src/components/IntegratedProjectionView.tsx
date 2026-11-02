import type { IntegratedMonth, IntegratedProjection } from '../api/monthlyPlan'
import { formatDate } from '../money'
import { formatMonthYear } from '../dates'
import { incomeSourceLabels, money, overdraftStateLabels, periodLabels, plannedMoney, qualityText } from './monthlyPlanText'

type Row = { group: string; label: string; cell: (month: IntegratedMonth) => string }

const percent = (value: string | null) => value === null ? 'Sem meta' : `${value.replace('.', ',')}%`

const rows: Row[] = [
  { group: 'Receita PJ', label: 'Situação', cell: m => incomeSourceLabels[m.income.source] ?? m.income.source },
  { group: 'Receita PJ', label: 'Competência trabalhada', cell: m => m.income.workedCompetence ? formatMonthYear(`${m.income.workedCompetence}-01`) : '—' },
  { group: 'Receita PJ', label: 'Dias faturáveis', cell: m => m.income.billableDays === null ? '—' : `${m.income.billableDays} de ${m.income.businessDays} úteis` },
  { group: 'Receita PJ', label: 'Horas × valor/hora', cell: m => m.income.hoursPerDay && m.income.hourlyRate ? `${m.income.hoursPerDay} h × ${money(m.income.hourlyRate)}` : '—' },
  { group: 'Receita PJ', label: 'Receita esperada (bruta)', cell: m => m.income.expectedGross === null ? '—' : money(m.income.expectedGross) },
  { group: 'Receita PJ', label: 'Recebimento previsto', cell: m => m.income.paymentDate ? formatDate(m.income.paymentDate) : '—' },
  { group: 'Receita PJ', label: 'Receita realizada na conta', cell: m => money(m.income.realizedIncome) },
  { group: 'Disponibilidade PJ', label: 'Saldo de abertura', cell: m => money(m.pj.opening) },
  { group: 'Disponibilidade PJ', label: 'Despesas PJ pendentes', cell: m => money(m.pj.pendingExpense) },
  { group: 'Disponibilidade PJ', label: 'Saldo bancário projetado', cell: m => money(m.pj.closing) },
  { group: 'Disponibilidade PJ', label: 'Reserva de tributos', cell: m => m.pj.taxObligationRegistered ? 'Já há tributo cadastrado: reserva não aplicada' : money(m.pj.taxReserve) },
  { group: 'Disponibilidade PJ', label: 'Reserva operacional', cell: m => money(m.pj.operationalReserve) },
  { group: 'Disponibilidade PJ', label: 'Disponível para repasse', cell: m => money(m.pj.available) },
  { group: 'Disponibilidade PJ', label: 'Repasse sugerido (não cadastrado)', cell: m => money(m.pj.suggestedTransfer) },
  { group: 'Transferências PJ → PF', label: 'Prevista', cell: m => money(m.transfers.plannedPjToPf) },
  { group: 'Transferências PJ → PF', label: 'Realizada', cell: m => money(m.transfers.realizedPjToPf) },
  { group: 'Despesas PF', label: 'Fixas pendentes', cell: m => money(m.pf.fixedPending) },
  { group: 'Despesas PF', label: 'Cartões (faturas existentes)', cell: m => money(m.pf.cardsOutstanding) },
  { group: 'Despesas PF', label: 'Demais compromissos', cell: m => money(m.pf.otherPending) },
  { group: 'Fluxo de caixa PF', label: 'Entradas (sem repasses)', cell: m => money(m.pf.entries) },
  { group: 'Fluxo de caixa PF', label: 'Saídas (sem repasses)', cell: m => money(m.pf.exits) },
  { group: 'Fluxo de caixa PF', label: 'Resultado do mês', cell: m => money(m.pf.result) },
  { group: 'Conta PF', label: 'Saldo inicial', cell: m => money(m.pf.opening) },
  { group: 'Conta PF', label: 'Repasses recebidos da PJ', cell: m => money(m.pf.transfersIn) },
  { group: 'Conta PF', label: 'Saldo final projetado', cell: m => money(m.pf.closing) },
  { group: 'Cheque especial', label: 'Utilizado na abertura', cell: m => m.overdraft ? money(m.overdraft.usedStart) : 'Indisponível' },
  { group: 'Cheque especial', label: 'Utilizado no fechamento', cell: m => m.overdraft ? money(m.overdraft.usedEnd) : 'Indisponível' },
  { group: 'Cheque especial', label: 'Limite disponível', cell: m => m.overdraft ? plannedMoney(m.overdraft.available) : 'Indisponível' },
  { group: 'Cheque especial', label: 'Excesso sobre o limite', cell: m => m.overdraft ? money(m.overdraft.excess) : 'Indisponível' },
  { group: 'Cheque especial', label: 'Recuperação no mês', cell: m => m.overdraft ? money(m.overdraft.recovery) : 'Indisponível' },
  { group: 'Cheque especial', label: 'Meta', cell: m => m.overdraft?.target ? `${money(m.overdraft.target)} · ${overdraftStateLabels[m.overdraft.targetState]}` : '—' },
  { group: 'Cheque especial', label: 'Atingimento da meta', cell: m => m.overdraft ? percent(m.overdraft.achievementPercent) : '—' },
  { group: 'Qualidade', label: 'Cobertura da projeção', cell: m => m.complete ? 'Completa' : 'Parcial' },
]

/**
 * Projeção integrada de seis meses (PROJ-01): receita PJ estimada, disponibilidade para repasse, transferências PJ → PF, conta PF e recuperação do limite.
 * Tudo vem do backend. Repasse não é receita nem despesa; estimativa não aprovada não entra; fonte ausente é dita como ausente.
 */
export function IntegratedProjectionView({ projection }: { projection: IntegratedProjection }) {
  const missing = [...new Set(projection.months.flatMap(month => month.quality))]
  const partial = projection.months.some(month => !month.complete)
  return <section className="monthly-plan__integrated" aria-labelledby="integrated-title">
    <h4 id="integrated-title">Projeção integrada · {projection.pjAccountName ?? 'PJ'} e {projection.pfAccountName ?? 'PF'}</h4>
    {projection.recoveryOverPeriod !== null && <p className="meta">Recuperação do limite no período, pela projeção: <strong>{money(projection.recoveryOverPeriod)}</strong></p>}
    {partial && <p className="notice notice--warning" role="note">Projeção parcial: faltam fontes (abaixo). O fechamento mostrado considera apenas o que existe no sistema e não representa o fluxo financeiro integral.</p>}
    <div className="monthly-plan__table-wrap">
      <table className="monthly-plan__table monthly-plan__table--months">
        <caption className="sr-only">Projeção integrada: receita PJ, disponibilidade, repasses, conta PF e cheque especial por competência</caption>
        <thead><tr><th scope="col">Indicador</th>{projection.months.map(month => <th scope="col" key={month.competence}>{formatMonthYear(`${month.competence}-01`)}<small>{periodLabels[month.periodKind]}</small></th>)}</tr></thead>
        <tbody>{rows.map(row => <tr key={`${row.group}-${row.label}`}><th scope="row">{row.group} · {row.label}</th>{projection.months.map(month => <td key={month.competence}>{row.cell(month)}</td>)}</tr>)}</tbody>
      </table>
    </div>
    {missing.length > 0 && <ul className="plan-card__notes" aria-label="Fontes ausentes da projeção">{missing.map(code => <li key={code}>{qualityText[code] ?? code}</li>)}</ul>}
  </section>
}
