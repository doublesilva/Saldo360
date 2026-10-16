import { useEffect, useState } from 'react'
import { getFinancialDashboard, type DashboardAlert, type FinancialDashboardResponse } from '../api/dashboard'
import { formatBrlDecimal, formatDate } from '../money'
import { formatMonthYear, todayInSaoPaulo } from '../dates'

type DashboardSection = 'calendar' | 'budgets' | 'cards' | 'debts' | 'goals' | 'planning' | 'accounts'
const money = (value: string | null) => value === null ? 'Indisponível' : formatBrlDecimal(value)
const trendLabels = { Improving: 'Melhorando', Stable: 'Estável', Deteriorating: 'Piorando', Unknown: 'Tendência indeterminada' } as const
const severityLabels = { Critical: 'Crítico', Warning: 'Atenção', Info: 'Informativo' } as const

export function FinancialDashboard({ familyId, onNavigate, refreshKey = 0 }: { familyId: string; onNavigate?: (section: DashboardSection) => void; refreshKey?: number }) {
  const [competence, setCompetence] = useState(() => todayInSaoPaulo().slice(0, 7))
  const [months, setMonths] = useState(6)
  const [state, setState] = useState<{ kind: 'loading' } | { kind: 'loaded'; value: FinancialDashboardResponse } | { kind: 'error'; message: string }>({ kind: 'loading' })
  useEffect(() => {
    const controller = new AbortController()
    getFinancialDashboard(familyId, competence, months, controller.signal)
      .then((value) => { if (!controller.signal.aborted) setState({ kind: 'loaded', value }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setState({ kind: 'error', message: error instanceof Error ? error.message : 'Falha ao carregar o painel financeiro.' }) })
    return () => controller.abort()
  }, [familyId, competence, months, refreshKey])

  return <section className="family-section dashboard-page" aria-labelledby="dashboard-title">
    <div className="section-heading"><div><h3 id="dashboard-title">Visão Geral</h3><p className="meta">Resumo financeiro consolidado da família.</p></div><div className="dashboard-filters"><label>Competência<input aria-label="Competência do dashboard" type="month" value={competence} onChange={(event) => { setState({ kind: 'loading' }); setCompetence(event.target.value) }} /></label><label>Horizonte<select aria-label="Horizonte da projeção" value={months} onChange={(event) => { setState({ kind: 'loading' }); setMonths(Number.parseInt(event.target.value, 10)) }}><option value={3}>3 meses</option><option value={6}>6 meses</option><option value={12}>12 meses</option></select></label></div></div>
    {state.kind === 'loading' && <p className="status status--pending" role="status">Carregando painel…</p>}
    {state.kind === 'error' && <p className="status status--down" role="alert">{state.message}</p>}
    {state.kind === 'loaded' && <DashboardContent data={state.value} onNavigate={onNavigate} />}
  </section>
}

function DashboardContent({ data, onNavigate }: { data: FinancialDashboardResponse; onNavigate?: (section: DashboardSection) => void }) {
  return <>
    {!data.coverage.complete && <p className="notice" role="status">Cobertura incompleta: {data.coverage.reason ?? 'alguns compromissos não estão alocados.'} O saldo projetado não representa disponibilidade garantida.</p>}
    <div className="dashboard-summary"><Metric title="Saldo realizado" value={money(data.position.realizedBalance)} /><Metric title="Fechamento projetado" value={money(data.indicators.projectedBalance)} /><Metric title="Comprometimento" value={money(data.indicators.commitment)} /><Metric title="Saldo não alocado" value={money(data.indicators.unallocatedAvailability)} /></div>
    <div className="dashboard-grid"><DashboardProjection data={data} /><DashboardAlerts alerts={data.alerts} onNavigate={onNavigate} /></div>
    <div className="dashboard-grid dashboard-grid--secondary"><DashboardCashFlow data={data} /><DashboardBudgets data={data} onNavigate={onNavigate} /><DashboardCards data={data} onNavigate={onNavigate} /><DashboardDebts data={data} onNavigate={onNavigate} /><DashboardGoals data={data} onNavigate={onNavigate} /></div>
    <section className="card" aria-labelledby="dashboard-next-title"><h4 id="dashboard-next-title">Próximo compromisso</h4>{data.indicators.nextCommitment ? <p><strong>{data.indicators.nextCommitment.description}</strong> · {money(data.indicators.nextCommitment.amount)} · {formatDate(data.indicators.nextCommitment.date)} ({data.indicators.nextCommitment.source})</p> : <p className="meta">Nenhum compromisso pendente conhecido.</p>}<p className="meta">Antes do próximo recebimento: {data.indicators.capacityBeforeNextIncome.status === 'Unknown' ? 'Não há dados suficientes para calcular a disponibilidade antes do próximo recebimento.' : data.indicators.capacityBeforeNextIncome.nextIncomeDate ? `${formatDate(data.indicators.capacityBeforeNextIncome.nextIncomeDate)} · ${money(data.indicators.capacityBeforeNextIncome.nextIncomeAmount)}` : 'Sem recebimento pendente conhecido.'}</p><button type="button" onClick={() => onNavigate?.('planning')}>Abrir Planejamento</button></section>
  </>
}

function Metric({ title, value }: { title: string; value: string }) { return <article className="card dashboard-metric"><h4>{title}</h4><p className="dashboard-metric__value">{value}</p></article> }
function DashboardProjection({ data }: { data: FinancialDashboardResponse }) { return <section className="card" aria-labelledby="dashboard-projection-title"><h4 id="dashboard-projection-title">Evolução projetada</h4><p>Tendência: <strong>{trendLabels[data.indicators.trend]}</strong></p><ul className="dashboard-projection-list">{data.monthlyProjection.map((item) => <li key={item.competence}><strong>{formatMonthYear(`${item.competence}-01`)}</strong><span>{money(item.closingBalance)}</span><span className="meta">Receitas {money(item.realizedIncome)} · Despesas {money(item.realizedExpense)} · {item.projectionComplete ? 'Cobertura completa' : 'Cobertura incompleta'}</span></li>)}</ul></section> }
function DashboardAlerts({ alerts, onNavigate }: { alerts: DashboardAlert[]; onNavigate?: (section: DashboardSection) => void }) { return <section className="card" aria-labelledby="dashboard-alerts-title"><h4 id="dashboard-alerts-title">Alertas</h4>{alerts.length === 0 ? <p className="meta">Nenhum alerta financeiro.</p> : <ul className="dashboard-alerts">{alerts.map((alert) => <li key={`${alert.code}-${alert.sourceId ?? 'none'}-${alert.referenceDate}`} className={`dashboard-alert dashboard-alert--${alert.severity}`}><strong>{severityLabels[alert.severity]} · {alert.title}</strong><p>{alert.description}</p>{alert.amount && <span>{money(alert.amount)}</span>}<small>{formatDate(alert.referenceDate)}</small><button type="button" onClick={() => onNavigate?.(alert.code.includes('budget') ? 'budgets' : alert.code.includes('card') ? 'cards' : alert.code.includes('debt') ? 'debts' : alert.code.includes('goal') ? 'goals' : alert.code.includes('balance') ? 'accounts' : 'calendar')}>Ver módulo</button></li>)}</ul>}</section> }
function DashboardCashFlow({ data }: { data: FinancialDashboardResponse }) { return <section className="card" aria-labelledby="dashboard-flow-title"><h4 id="dashboard-flow-title">Fluxo de caixa da competência</h4><p>Receitas realizadas: {money(data.cashFlow.realizedIncome)}</p><p>Despesas realizadas: {money(data.cashFlow.realizedExpense)}</p><p>Receitas pendentes: {money(data.cashFlow.pendingIncome)}</p><p>Despesas pendentes: {money(data.cashFlow.pendingExpense)}</p><p><strong>Resultado conhecido: {money(data.indicators.knownNet)}</strong></p></section> }
function DashboardBudgets({ data, onNavigate }: { data: FinancialDashboardResponse; onNavigate?: (section: DashboardSection) => void }) { return <section className="card"><h4>Orçamentos</h4><p>{data.budgets.targetCount} metas · Comprometido {money(data.budgets.committed)} · Realizado {money(data.budgets.realized)}</p><p>Acima da meta: {data.budgets.exceededCount} · Atenção: {data.budgets.attentionCount}</p><button type="button" onClick={() => onNavigate?.('budgets')}>Abrir Orçamentos</button></section> }
function DashboardCards({ data, onNavigate }: { data: FinancialDashboardResponse; onNavigate?: (section: DashboardSection) => void }) { return <section className="card"><h4>Cartões</h4><p>{data.creditCards.count} cartões · Devido {money(data.creditCards.outstanding)}</p><p>Vencidas: {data.creditCards.overdueInvoices} · Futuras: {data.creditCards.futureInvoices}</p>{data.creditCards.hasUnallocatedObligations && <p className="notice">Há obrigações sem conta de pagamento.</p>}<button type="button" onClick={() => onNavigate?.('cards')}>Abrir Cartões</button></section> }
function DashboardDebts({ data, onNavigate }: { data: FinancialDashboardResponse; onNavigate?: (section: DashboardSection) => void }) { return <section className="card"><h4>Dívidas e financiamentos</h4><p>{data.debts.activeContracts} contratos ativos · Parcelas no horizonte {money(data.debts.installmentsDueInHorizon)}</p><p>Saldo contratual: {money(data.debts.declaredOutstandingBalance)}</p><button type="button" onClick={() => onNavigate?.('debts')}>Abrir Dívidas</button></section> }
function DashboardGoals({ data, onNavigate }: { data: FinancialDashboardResponse; onNavigate?: (section: DashboardSection) => void }) { return <section className="card"><h4>Objetivos</h4><p>{data.goals.active} ativos · {data.goals.completed} concluídos</p><p>Alocado: {money(data.goals.allocated)} · Não alocado: {money(data.goals.unallocated)}</p><button type="button" onClick={() => onNavigate?.('goals')}>Abrir Objetivos</button></section> }
