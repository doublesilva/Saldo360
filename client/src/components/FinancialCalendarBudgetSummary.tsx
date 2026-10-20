import { useEffect, useMemo, useRef, useState } from 'react'
import { getBudgetExecution, listBudgets, type FinancialBudget, type FinancialBudgetAlertStatus, type FinancialBudgetExecution } from '../api/budgets'
import { formatBrlDecimal } from '../money'

type LoadState =
  | { kind: 'loading'; requestKey: string }
  | { kind: 'loaded'; requestKey: string; budget: FinancialBudget | null; execution: FinancialBudgetExecution | null }
  | { kind: 'error'; requestKey: string; message: string }

const severity: Record<FinancialBudgetAlertStatus, number> = {
  Exceeded: 0,
  LimitReached: 1,
  ApproachingLimit: 2,
  WithinBudget: 3,
}

const statusLabels: Record<FinancialBudgetAlertStatus, string> = {
  WithinBudget: 'Dentro do orçamento',
  ApproachingLimit: 'Atenção',
  LimitReached: 'Limite atingido',
  Exceeded: 'Orçamento excedido',
}

const statusIcons: Record<FinancialBudgetAlertStatus, string> = {
  WithinBudget: '✓',
  ApproachingLimit: '!',
  LimitReached: '●',
  Exceeded: '×',
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

export function FinancialCalendarBudgetSummary({
  workspaceId,
  competence,
  refreshKey = 0,
  onNavigateToBudgets,
}: {
  workspaceId: string
  competence: string
  refreshKey?: number
  onNavigateToBudgets: (competence: string) => void
}) {
  const requestKey = `${workspaceId}|${competence.slice(0, 7)}|${refreshKey}`
  const requestVersion = useRef(0)
  const [state, setState] = useState<LoadState>({ kind: 'loading', requestKey })
  const current = state.requestKey === requestKey ? state : { kind: 'loading' as const, requestKey }

  useEffect(() => {
    const controller = new AbortController()
    const version = ++requestVersion.current
    const month = competence.slice(0, 7)
    listBudgets(workspaceId, month, controller.signal)
      .then(async (budgets) => {
        const budget = budgets.find((item) => !item.isArchived) ?? null
        if (!budget) {
          if (version === requestVersion.current) setState({ kind: 'loaded', requestKey, budget: null, execution: null })
          return
        }
        const execution = await getBudgetExecution(workspaceId, budget.id, controller.signal)
        if (version === requestVersion.current) setState({ kind: 'loaded', requestKey, budget, execution })
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted && version === requestVersion.current) {
          setState({ kind: 'error', requestKey, message: errorMessage(error) })
        }
      })
    return () => controller.abort()
  }, [workspaceId, competence, refreshKey, requestKey])

  return (
    <section className="calendar-budget" aria-labelledby="calendar-budget-title" aria-live="polite">
      <div className="calendar-budget__heading">
        <div>
          <h3 id="calendar-budget-title">Situação do orçamento</h3>
          <p className="meta">Orçamento pessoal da competência — independente dos filtros da Agenda.</p>
        </div>
        <button type="button" className="button--link" onClick={() => onNavigateToBudgets(competence.slice(0, 7))}>Ver orçamento completo</button>
      </div>
      {current.kind === 'loading' && <p className="status status--pending">Carregando situação do orçamento…</p>}
      {current.kind === 'error' && <p className="status status--down" role="status">Não foi possível carregar o orçamento: {current.message}</p>}
      {current.kind === 'loaded' && !current.budget && (
        <div className="calendar-budget__empty">
          <p>Nenhum orçamento definido para esta competência.</p>
          <button type="button" onClick={() => onNavigateToBudgets(competence.slice(0, 7))}>Ir para Orçamentos</button>
        </div>
      )}
      {current.kind === 'loaded' && current.budget && current.execution && (
        <>
          <CalendarBudgetTotals execution={current.execution} />
          <CalendarBudgetAlerts budget={current.budget} execution={current.execution} />
        </>
      )}
    </section>
  )
}

function CalendarBudgetTotals({ execution }: { execution: FinancialBudgetExecution }) {
  const total = execution.total
  return (
    <div className="calendar-budget__content">
      <div className={`calendar-budget__status calendar-budget__status--${total.status}`} role="status">
        <span aria-hidden="true">{statusIcons[total.status]}</span>
        <strong>{statusLabels[total.status]}</strong>
        <span>{total.alertMessage}</span>
      </div>
      <dl className="calendar-budget__totals">
        <CalendarBudgetValue label="Planejado" value={total.planned} />
        <CalendarBudgetValue label="Realizado" value={total.realized} />
        <CalendarBudgetValue label="Comprometido" value={total.committed} />
        <CalendarBudgetValue label="Consumo projetado" value={total.projectedConsumption} />
        <CalendarBudgetValue label="Disponível" value={total.available} />
        <CalendarBudgetValue label="Excedente" value={total.exceededAmount} />
      </dl>
      <div className="calendar-budget__counts" aria-label="Contagem de alertas">
        <span>Atenção: <strong>{total.approachingCount}</strong></span>
        <span>No limite: <strong>{total.reachedCount}</strong></span>
        <span>Excedidas: <strong>{total.exceededCount}</strong></span>
      </div>
    </div>
  )
}

function CalendarBudgetValue({ label, value }: { label: string; value: string }) {
  return <div><dt>{label}</dt><dd>{formatBrlDecimal(value)}</dd></div>
}

function CalendarBudgetAlerts({ budget, execution }: { budget: FinancialBudget; execution: FinancialBudgetExecution }) {
  const targets = useMemo(() => execution.targets
    .filter((item) => item.status !== 'WithinBudget')
    .map((item) => ({
      item,
      name: budget.targets.find((target) => target.id === item.targetId)?.category.name ?? 'Categoria',
    }))
    .sort((a, b) => severity[a.item.status] - severity[b.item.status] || a.name.localeCompare(b.name, 'pt-BR')), [budget.targets, execution.targets])

  return (
    <section className="calendar-budget__alerts" aria-labelledby="calendar-budget-alerts-title">
      <h4 id="calendar-budget-alerts-title">Categorias que exigem atenção</h4>
      {targets.length === 0 ? <p className="meta">Nenhuma meta ativa exige atenção.</p> : (
        <ul>
          {targets.map(({ item, name }) => (
            <li key={item.targetId} className={`calendar-budget-alert calendar-budget-alert--${item.status}`}>
              <div><strong>{name}</strong><span><span aria-hidden="true">{statusIcons[item.status]}</span> {statusLabels[item.status]}</span></div>
              <dl>
                <div><dt>Planejado</dt><dd>{formatBrlDecimal(item.planned)}</dd></div>
                <div><dt>Projetado</dt><dd>{formatBrlDecimal(item.projectedConsumption)}</dd></div>
                <div><dt>{item.status === 'Exceeded' ? 'Excedente' : 'Disponível'}</dt><dd>{formatBrlDecimal(item.status === 'Exceeded' ? item.exceededAmount : item.available)}</dd></div>
              </dl>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
