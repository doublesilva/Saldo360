import { useEffect, useMemo, useRef, useState } from 'react'
import { listAccounts, primaryAccountOf, type FinancialAccount } from '../api/accounts'
import type { CategoryType } from '../api/categories'
import {
  getFinancialCalendar,
  getFinancialCalendarBalances,
  getSixMonthPlanning,
  type FinancialCalendarBalanceDay,
  type FinancialCalendarBalanceResponse,
  type FinancialCalendarItem,
  type FinancialCalendarMonthlyProjectionResponse,
  type FinancialCalendarResponse,
  type FinancialCalendarStatus,
} from '../api/financialCalendar'
import { addCalendarMonths, endOfMonth, formatMonthYear, startOfMonth, todayInSaoPaulo } from '../dates'
import { formatBrlDecimal, formatDate } from '../money'
import { FinancialCalendarDetails } from './FinancialCalendarDetails'
import { FinancialCalendarCardInvoices } from './FinancialCalendarCardInvoices'
import { FinancialCalendarBudgetSummary } from './FinancialCalendarBudgetSummary'
import { OperationalError, OperationalLoading } from './OperationalFeedback'

type Load<T> = { kind: 'loading' } | { kind: 'loaded'; data: T } | { kind: 'error'; message: string }
type RequestLoad<T> =
  | { kind: 'loading'; requestKey: string }
  | { kind: 'loaded'; requestKey: string; data: T }
  | { kind: 'error'; requestKey: string; message: string }

const weekDays = ['Dom', 'Seg', 'Ter', 'Qua', 'Qui', 'Sex', 'Sáb']
const typeLabels: Record<CategoryType, string> = { Income: 'Receita', Expense: 'Despesa' }
const statusLabels: Record<FinancialCalendarStatus, string> = {
  Realized: 'Realizado',
  Pending: 'Pendente',
  Overdue: 'Vencido',
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

/** Visão mensal da Agenda Financeira, com detalhes e operações financeiras existentes. */
type FinancialCalendarPageProps = { workspaceId: string; onChanged?: () => void; initialMonth?: string; onNavigateToBudgets?: (competence: string) => void; onNavigateToCards?: () => void }

export function FinancialCalendarPage(props: FinancialCalendarPageProps) {
  return <FinancialCalendarPageContent key={props.workspaceId} {...props} />
}

function FinancialCalendarPageContent({ workspaceId, onChanged, initialMonth, onNavigateToBudgets, onNavigateToCards }: FinancialCalendarPageProps) {
  const today = todayInSaoPaulo()
  const [month, setMonth] = useState(() => initialMonth ? startOfMonth(initialMonth) : startOfMonth(today))
  const [selectedDate, setSelectedDate] = useState(today)
  const [accountId, setAccountId] = useState('')
  // A conta principal é só a seleção inicial; o consolidado continua a uma escolha de distância e a escolha do usuário nunca é sobrescrita.
  const accountChosen = useRef(false)
  const [type, setType] = useState<CategoryType | ''>('')
  const [view, setView] = useState<'month' | 'planning'>('month')
  const [refreshKey, setRefreshKey] = useState(0)
  const [opened, setOpened] = useState<{ item: FinancialCalendarItem; trigger: HTMLElement } | null>(null)
  const [accounts, setAccounts] = useState<Load<FinancialAccount[]>>({ kind: 'loading' })
  const requestVersion = useRef(0)
  const from = month
  const through = endOfMonth(month)
  const requestKey = `${workspaceId}|${from}|${through}|${accountId}|${type}|${refreshKey}`
  const [calendarState, setCalendar] = useState<RequestLoad<FinancialCalendarResponse>>({
    kind: 'loading',
    requestKey,
  })
  // Ao mudar mês/filtro, o resultado anterior deixa de ser exibido imediatamente, sem setState síncrono no efeito.
  const calendar: RequestLoad<FinancialCalendarResponse> =
    calendarState.requestKey === requestKey ? calendarState : { kind: 'loading', requestKey }

  useEffect(() => {
    const controller = new AbortController()
    listAccounts(workspaceId, true, controller.signal)
      .then((items) => {
        if (controller.signal.aborted) return
        setAccounts({ kind: 'loaded', data: items })
        const primary = primaryAccountOf(items)
        if (primary && !accountChosen.current) setAccountId(primary.id)
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setAccounts({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId])

  useEffect(() => {
    // Espera as contas: a conta principal define a seleção inicial e não deve haver uma consulta consolidada à toa.
    if (accounts.kind === 'loading') return
    const controller = new AbortController()
    const version = ++requestVersion.current
    getFinancialCalendar(
      workspaceId,
      from,
      through,
      { accountId: accountId || undefined, type: type || undefined },
      controller.signal,
    )
      .then((data) => {
        if (version === requestVersion.current) setCalendar({ kind: 'loaded', requestKey, data })
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted && version === requestVersion.current) {
          setCalendar({ kind: 'error', requestKey, message: errorMessage(error) })
        }
      })
    return () => controller.abort()
  }, [workspaceId, from, through, accountId, type, requestKey, refreshKey, accounts.kind])

  const handleFinancialChange = () => {
    setRefreshKey((current) => current + 1)
    onChanged?.()
  }

  const selectMonth = (nextMonth: string, preferredDate?: string) => {
    setMonth(nextMonth)
    setSelectedDate(preferredDate ?? nextMonth)
  }

  const selectedItems =
    calendar.kind === 'loaded' ? calendar.data.items.filter((item) => item.date === selectedDate) : []

  return (
    <section className="financial-calendar" aria-labelledby="financial-calendar-title">
      <FinancialCalendarHeader
        month={month}
        today={today}
        accountId={accountId}
        type={type}
        accounts={accounts}
        onPrevious={() => selectMonth(addCalendarMonths(month, -1))}
        onNext={() => selectMonth(addCalendarMonths(month, 1))}
        onToday={() => selectMonth(startOfMonth(today), today)}
        onAccountChange={(value) => { accountChosen.current = true; setAccountId(value) }}
        onTypeChange={setType}
        showTypeFilter={view === 'month'}
      />

      <div className="calendar-view-switch" role="group" aria-label="Visualização da agenda">
        <button type="button" className={view === 'month' ? 'family-navigation__item--active' : ''} onClick={() => setView('month')}>
          Mês
        </button>
        <button type="button" className={view === 'planning' ? 'family-navigation__item--active' : ''} onClick={() => setView('planning')}>
          Próximos meses
        </button>
      </div>

      {view === 'month' && calendar.kind === 'loading' && <OperationalLoading>Carregando agenda…</OperationalLoading>}
      {view === 'month' && calendar.kind === 'error' && (
        <OperationalError error={calendar.message} onRetry={() => setRefreshKey((current) => current + 1)} />
      )}
      {view === 'month' && calendar.kind === 'loaded' && (
        <>
          <FinancialCalendarSummary summary={calendar.data.summary} />
          <FinancialCalendarBudgetSummary
            workspaceId={workspaceId}
            competence={month}
            refreshKey={refreshKey}
            onNavigateToBudgets={(competence) => onNavigateToBudgets?.(competence)}
          />
          <div className="calendar-layout">
            <FinancialCalendarGrid
              month={month}
              today={today}
              selectedDate={selectedDate}
              items={calendar.data.items}
              onSelect={setSelectedDate}
            />
            <FinancialCalendarEvents
              date={selectedDate}
              items={selectedItems}
              onOpen={(item, trigger) => setOpened({ item, trigger })}
            />
          </div>
          <FinancialCalendarCardInvoices workspaceId={workspaceId} month={month} accountId={accountId} selectedDate={selectedDate} refreshKey={refreshKey} onOpenCards={onNavigateToCards} />
        </>
      )}
      {accounts.kind === 'loading' ? null : view === 'month' ? (
        <FinancialCalendarBalanceEvolution
          workspaceId={workspaceId}
          from={from}
          through={through}
          accountId={accountId}
          selectedDate={selectedDate}
          onSelectDate={setSelectedDate}
          refreshKey={refreshKey}
        />
      ) : (
        <FinancialCalendarMonthlyPlanning
          workspaceId={workspaceId}
          from={startOfMonth(today).slice(0, 7)}
          accountId={accountId}
          refreshKey={refreshKey}
          onSelectMonth={(selectedMonth) => {
            selectMonth(`${selectedMonth}-01`)
            setView('month')
          }}
        />
      )}
      {view === 'month' && opened && (
        <FinancialCalendarDetails
          key={`${workspaceId}:${opened.item.source}:${opened.item.id}`}
          workspaceId={workspaceId}
          item={opened.item}
          returnFocus={opened.trigger}
          onClose={() => setOpened(null)}
          onChanged={handleFinancialChange}
        />
      )}
    </section>
  )
}

export function FinancialCalendarMonthlyPlanning({
  workspaceId,
  from,
  accountId,
  refreshKey = 0,
  onSelectMonth,
}: {
  workspaceId: string
  from: string
  accountId: string
  refreshKey?: number
  onSelectMonth: (month: string) => void
}) {
  const requestVersion = useRef(0)
  const requestKey = `${workspaceId}|${from}|${accountId}|${refreshKey}`
  const [projectionState, setProjectionState] = useState<RequestLoad<FinancialCalendarMonthlyProjectionResponse>>({
    kind: 'loading',
    requestKey,
  })
  const projection = projectionState.requestKey === requestKey
    ? projectionState
    : { kind: 'loading' as const, requestKey }

  useEffect(() => {
    const controller = new AbortController()
    const version = ++requestVersion.current
    getSixMonthPlanning(workspaceId, from, accountId || undefined, controller.signal)
      .then((data) => {
        if (version === requestVersion.current) setProjectionState({ kind: 'loaded', requestKey, data })
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted && version === requestVersion.current) {
          setProjectionState({ kind: 'error', requestKey, message: errorMessage(error) })
        }
      })
    return () => controller.abort()
  }, [workspaceId, from, accountId, refreshKey, requestKey])

  return (
    <section className="monthly-planning" aria-labelledby="monthly-planning-title">
      <div className="monthly-planning__heading">
        <div>
          <h3 id="monthly-planning-title">Planejamento dos próximos seis meses</h3>
          <p className="meta">{accountId ? 'Conta selecionada' : 'Consolidado do orçamento'}</p>
        </div>
      </div>
      {projection.kind === 'loading' && <p className="status status--pending">Calculando planejamento financeiro…</p>}
      {projection.kind === 'error' && <p className="status status--down" role="alert">{projection.message}</p>}
      {projection.kind === 'loaded' && projection.data.summary && (
        <section aria-label="Resumo dos seis meses">
          <dl className="monthly-planning-card__values">
            <MonthlyValue label="Receitas no horizonte" value={projection.data.summary.totalIncome} />
            <MonthlyValue label="Despesas no horizonte (incluem cartões)" value={projection.data.summary.totalExpense} />
            <MonthlyValue label="Pior saldo final" value={projection.data.summary.lowestClosingBalance} />
          </dl>
          <p>Competência com menor fechamento: {projection.data.summary.lowestBalanceMonth ?? 'Não disponível'} · Meses acima da meta: {projection.data.summary.monthsOverBudget} · Meses com saldo negativo: {projection.data.summary.monthsWithNegativeBalance}</p>
        </section>
      )}
      {projection.kind === 'loaded' && (
        <div className="monthly-planning__grid">
          {projection.data.months.map((item) => {
            const negative = item.closingBalance?.startsWith('-') ?? false
            const atRisk = item.firstNegativeProjectedDate !== null
            return (
              <article key={item.month} className={`monthly-planning-card${negative || atRisk ? ' monthly-planning-card--risk' : ''}`}>
                <button type="button" className="monthly-planning-card__month" onClick={() => onSelectMonth(item.month)}>
                  {formatMonthYear(`${item.month}-01`)}
                  <span>Abrir calendário</span>
                </button>
                <dl className="monthly-planning-card__values">
                  <MonthlyValue label="Receitas realizadas" value={item.realizedIncome} />
                  <MonthlyValue label="Despesas realizadas" value={item.realizedExpense} />
                  <MonthlyValue label="Receitas pendentes" value={item.pendingIncome} />
                  <MonthlyValue label="Despesas pendentes" value={item.pendingExpense} />
                  <MonthlyValue label="Cartões pagos (incluídos nas despesas)" value={item.cardPayments ?? null} />
                  <MonthlyValue label="Cartões ainda devidos (incluídos nas pendências)" value={item.creditCardOutstanding ?? null} />
                  <MonthlyValue label="Despesas fixas — classificação não disponível" value={item.fixedExpenses ?? null} />
                  <MonthlyValue label="Meta por categorias" value={item.budgetTarget ?? null} />
                  <MonthlyValue label="Consumo nas categorias com meta" value={item.budgetConsumption ?? null} />
                  <MonthlyValue label="Saldo de fechamento" value={item.closingBalance} negative={negative} />
                  <MonthlyValue label="Menor saldo projetado" value={item.minimumProjectedBalance} negative={item.minimumProjectedBalance?.startsWith('-')} />
                </dl>
                {item.periodKind && <p>{item.periodKind === 'Current' ? 'Mês atual · realizado + pendências restantes' : item.periodKind === 'Future' ? 'Mês futuro · compromissos conhecidos' : 'Mês histórico · realizado'}</p>}
                <p>{item.budgetStatus === 'Exceeded' ? 'Meta ultrapassada' : item.budgetStatus === 'LimitReached' ? 'Meta atingida' : item.budgetStatus === 'ApproachingLimit' ? 'Próximo da meta' : item.budgetStatus === 'WithinBudget' ? 'Dentro da meta' : 'Sem meta definida'}</p>
                {(item.creditCardProjectionComplete === false || item.debtProjectionComplete === false) && <p role="status">Cobertura parcial: há obrigações sem conta atribuída.</p>}
                {item.unavailableReason ? (
                  <p className="monthly-planning-card__status monthly-planning-card__status--unavailable">
                    Saldo indisponível: {balanceUnavailableMessage(item.unavailableReason).toLowerCase()}
                  </p>
                ) : item.minimumProjectedBalance === null ? (
                  <p className="monthly-planning-card__status">Competência histórica · saldo realizado</p>
                ) : atRisk ? (
                  <p className="monthly-planning-card__status monthly-planning-card__status--risk">
                    Risco: primeiro saldo negativo em {formatDate(item.firstNegativeProjectedDate!)}
                  </p>
                ) : (
                  <p className="monthly-planning-card__status">Sem saldo negativo projetado</p>
                )}
                {(item.overdueIncome !== '0.00' || item.overdueExpense !== '0.00') && (
                  <p className="monthly-planning-card__overdue">
                    Pendências vencidas: receitas {formatBrlDecimal(item.overdueIncome)} · despesas {formatBrlDecimal(item.overdueExpense)}
                  </p>
                )}
              </article>
            )
          })}
        </div>
      )}
    </section>
  )
}

function MonthlyValue({ label, value, negative = false }: { label: string; value: string | null; negative?: boolean }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd className={negative ? 'amount amount--negative' : 'amount'}>
        {value === null ? 'Não disponível' : formatBrlDecimal(value)}
      </dd>
    </div>
  )
}

export function FinancialCalendarBalanceEvolution({
  workspaceId,
  from,
  through,
  accountId,
  selectedDate,
  onSelectDate,
  refreshKey = 0,
}: {
  workspaceId: string
  from: string
  through: string
  accountId: string
  selectedDate: string
  onSelectDate?: (date: string) => void
  refreshKey?: number
}) {
  const requestVersion = useRef(0)
  const requestKey = `${workspaceId}|${from}|${through}|${accountId}|${refreshKey}`
  const [balanceState, setBalanceState] = useState<RequestLoad<FinancialCalendarBalanceResponse>>({
    kind: 'loading',
    requestKey,
  })
  const balance: RequestLoad<FinancialCalendarBalanceResponse> =
    balanceState.requestKey === requestKey ? balanceState : { kind: 'loading', requestKey }

  useEffect(() => {
    const controller = new AbortController()
    const version = ++requestVersion.current
    getFinancialCalendarBalances(workspaceId, from, through, accountId || undefined, controller.signal)
      .then((data) => {
        if (version === requestVersion.current) setBalanceState({ kind: 'loaded', requestKey, data })
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted && version === requestVersion.current) {
          setBalanceState({ kind: 'error', requestKey, message: errorMessage(error) })
        }
      })
    return () => controller.abort()
  }, [workspaceId, from, through, accountId, requestKey, refreshKey])

  return (
    <section className="balance-evolution" aria-labelledby="balance-evolution-title">
      <div className="balance-evolution__heading">
        <div>
          <h3 id="balance-evolution-title">Evolução do saldo</h3>
          <p className="meta">{accountId ? 'Conta selecionada' : 'Consolidado do orçamento'}</p>
        </div>
        <div className="balance-evolution__legend" aria-label="Legenda da evolução">
          <span><i className="balance-line-sample" /> Histórico realizado</span>
          <span><i className="balance-line-sample balance-line-sample--projected" /> Projeção</span>
          <span><i className="balance-point-sample balance-point-sample--negative" /> Saldo negativo</span>
          <span><i className="balance-point-sample balance-point-sample--overdue">!</i> Pendências vencidas</span>
        </div>
      </div>

      {balance.kind === 'loading' && <p className="status status--pending">Calculando evolução do saldo…</p>}
      {balance.kind === 'error' && <p className="status status--down" role="alert">{balance.message}</p>}
      {balance.kind === 'loaded' && <FinancialCalendarBalanceContent balance={balance.data} selectedDate={selectedDate} onSelectDate={onSelectDate} />}
    </section>
  )
}

function FinancialCalendarBalanceContent({
  balance,
  selectedDate,
  onSelectDate,
}: {
  balance: FinancialCalendarBalanceResponse
  selectedDate: string
  onSelectDate?: (date: string) => void
}) {
  if (balance.unavailableReason) {
    return (
      <p className="notice balance-evolution__unavailable" role="status">
        {balanceUnavailableMessage(balance.unavailableReason)} Nenhum total parcial é apresentado.
      </p>
    )
  }

  const points = balance.days.flatMap((day, index) => {
    if (day.closingBalance === null || day.chartPosition === null) return []
    const x = balance.days.length === 1 ? 500 : 20 + (index * 960) / (balance.days.length - 1)
    const y = 20 + day.chartPosition * 0.2
    return [{ day, x, y }]
  })
  const realized = points.filter((point) => point.day.kind === 'Realized')
  const projectedOnly = points.filter((point) => point.day.kind === 'Projected')
  const projected = projectedOnly.length > 0 && realized.length > 0
    ? [realized[realized.length - 1], ...projectedOnly]
    : projectedOnly
  const pointText = (values: typeof points) => values.map((point) => `${point.x},${point.y}`).join(' ')

  return (
    <>
      <div className="balance-risk" aria-label="Indicadores de risco">
        <BalanceIndicator label="Saldo no início do período" value={balance.openingBalance} />
        <BalanceIndicator label="Menor saldo projetado" value={balance.risk.minimumProjectedBalance} />
        <div className="summary-value">
          <span>Primeiro saldo projetado negativo</span>
          <strong>{balance.risk.firstNegativeProjectedDate ? formatDate(balance.risk.firstNegativeProjectedDate) : 'Não identificado'}</strong>
        </div>
        <BalanceIndicator label="Saldo projetado no encerramento" value={balance.risk.endingProjectedBalance} />
      </div>

      <div className="balance-chart-wrap">
        <svg
          className="balance-chart"
          viewBox="0 0 1000 240"
          preserveAspectRatio="none"
          role={onSelectDate ? 'group' : 'img'}
          aria-label="Gráfico da evolução diária do saldo; os valores completos estão na tabela seguinte"
        >
          {realized.length > 1 && <polyline className="balance-chart__line" points={pointText(realized)} />}
          {projected.length > 1 && <polyline className="balance-chart__line balance-chart__line--projected" points={pointText(projected)} />}
          {points.map(({ day, x, y }) => (
            <circle
              key={day.date}
              cx={x}
              cy={y}
              r={day.date === selectedDate ? 10 : 6}
              role={onSelectDate ? 'button' : undefined}
              tabIndex={onSelectDate ? 0 : undefined}
              aria-label={onSelectDate ? `Selecionar ${formatDate(day.date)}: ${day.kind === 'Realized' ? 'Realizado' : 'Projetado'}, ${formatBrlDecimal(day.closingBalance!)}${day.hasOverduePending ? ', com pendências vencidas' : ''}` : undefined}
              aria-pressed={onSelectDate ? day.date === selectedDate : undefined}
              onClick={() => onSelectDate?.(day.date)}
              onKeyDown={(event) => {
                if (onSelectDate && (event.key === 'Enter' || event.key === ' ')) {
                  event.preventDefault()
                  onSelectDate(day.date)
                }
              }}
              className={`balance-chart__point balance-chart__point--${day.kind.toLowerCase()}${day.isNegative ? ' balance-chart__point--negative' : ''}${day.hasOverduePending ? ' balance-chart__point--overdue' : ''}${day.date === selectedDate ? ' balance-chart__point--selected' : ''}`}
            >
              <title>{`${formatDate(day.date)}: ${formatBrlDecimal(day.closingBalance!)}`}</title>
            </circle>
          ))}
        </svg>
      </div>

      <div className="balance-table-wrap">
        <table className="balance-table">
          <caption className="sr-only">Valores diários da evolução do saldo</caption>
          <thead>
            <tr><th>Data</th><th>Base</th><th>Receitas</th><th>Despesas</th><th>Transferências</th><th>Fechamento</th></tr>
          </thead>
          <tbody>
            {balance.days.map((day) => <BalanceDayRow key={day.date} day={day} selected={day.date === selectedDate} />)}
          </tbody>
        </table>
      </div>
    </>
  )
}

function BalanceIndicator({ label, value }: { label: string; value: string | null }) {
  return (
    <div className={`summary-value${value?.startsWith('-') ? ' summary-value--overdue' : ''}`}>
      <span>{label}</span>
      <strong>{value === null ? 'Não disponível' : formatBrlDecimal(value)}</strong>
    </div>
  )
}

function BalanceDayRow({ day, selected }: { day: FinancialCalendarBalanceDay; selected: boolean }) {
  const transferText = day.transferIn === null || day.transferOut === null
    ? '—'
    : `Entrada ${formatBrlDecimal(day.transferIn)} · saída ${formatBrlDecimal(day.transferOut)}`
  return (
    <tr className={`${selected ? 'balance-table__selected ' : ''}${day.isNegative ? 'balance-table__negative' : ''}`}>
      <th scope="row">{formatDate(day.date)}{selected ? ' (selecionado)' : ''}</th>
      <td>{day.kind === 'Realized' ? 'Realizado' : 'Projetado'}{day.hasOverduePending
        ? day.kind === 'Realized' ? ' · pendências vencidas não incluídas no saldo' : ' · inclui pendências vencidas'
        : ''}</td>
      <td>{day.income === null ? '—' : formatBrlDecimal(day.income)}</td>
      <td>{day.expense === null ? '—' : formatBrlDecimal(day.expense)}</td>
      <td>{transferText}</td>
      <td className={day.isNegative ? 'amount amount--negative' : 'amount'}>
        {day.closingBalance === null ? '—' : formatBrlDecimal(day.closingBalance)}
      </td>
    </tr>
  )
}

function balanceUnavailableMessage(reason: FinancialCalendarBalanceResponse['unavailableReason']): string {
  return reason === 'BeforeOpeningBalanceDate'
    ? 'Evolução indisponível: o período começa antes da referência do saldo inicial.'
    : 'Evolução indisponível: existe conta sem saldo inicial configurado.'
}

export function FinancialCalendarHeader({
  month,
  today,
  accountId,
  type,
  accounts,
  onPrevious,
  onNext,
  onToday,
  onAccountChange,
  onTypeChange,
  showTypeFilter = true,
}: {
  month: string
  today: string
  accountId: string
  type: CategoryType | ''
  accounts: Load<FinancialAccount[]>
  onPrevious: () => void
  onNext: () => void
  onToday: () => void
  onAccountChange: (value: string) => void
  onTypeChange: (value: CategoryType | '') => void
  showTypeFilter?: boolean
}) {
  return (
    <header className="calendar-header">
      <div>
        <h2 id="financial-calendar-title">Agenda Financeira</h2>
        <p className="calendar-header__month" aria-live="polite">
          {formatMonthYear(month)}
        </p>
      </div>
      <div className="calendar-navigation" aria-label="Navegação mensal">
        <button type="button" onClick={onPrevious} aria-label="Mês anterior">
          ←
        </button>
        <button type="button" onClick={onToday} disabled={month === startOfMonth(today)}>
          Mês atual
        </button>
        <button type="button" onClick={onNext} aria-label="Próximo mês">
          →
        </button>
      </div>
      <div className="calendar-filters">
        <label>
          Conta
          <select value={accountId} onChange={(event) => onAccountChange(event.target.value)} disabled={accounts.kind === 'loading'}>
            <option value="">Todas as contas</option>
            {accounts.kind === 'loaded' &&
              accounts.data.map((account) => (
                <option key={account.id} value={account.id}>
                  {account.name}{account.isActive ? '' : ' (inativa)'}
                </option>
              ))}
          </select>
        </label>
        {showTypeFilter && (
          <label>
            Tipo
            <select value={type} onChange={(event) => onTypeChange(event.target.value as CategoryType | '')}>
              <option value="">Todas</option>
              <option value="Income">Receitas</option>
              <option value="Expense">Despesas</option>
            </select>
          </label>
        )}
      </div>
      {accounts.kind === 'error' && <p className="field-error">{accounts.message}</p>}
    </header>
  )
}

export function FinancialCalendarSummary({ summary }: { summary: FinancialCalendarResponse['summary'] }) {
  return (
    <section className="calendar-summary" aria-label="Resumo financeiro do mês">
      <SummaryValue label="Receitas realizadas" value={summary.realizedIncome} kind="income" />
      <SummaryValue label="Despesas realizadas" value={summary.realizedExpense} kind="expense" />
      <SummaryValue label="Receitas pendentes" value={summary.pendingIncome} kind="income" planned />
      <SummaryValue label="Despesas pendentes" value={summary.pendingExpense} kind="expense" planned />
      <SummaryValue label="Receitas vencidas" value={summary.overdueIncome} kind="income" overdue />
      <SummaryValue label="Despesas vencidas" value={summary.overdueExpense} kind="expense" overdue />
    </section>
  )
}

function SummaryValue({
  label,
  value,
  kind,
  planned = false,
  overdue = false,
}: {
  label: string
  value: string
  kind: 'income' | 'expense'
  planned?: boolean
  overdue?: boolean
}) {
  return (
    <div className={`summary-value summary-value--${kind}${planned ? ' summary-value--planned' : ''}${overdue ? ' summary-value--overdue' : ''}`}>
      <span>{label}</span>
      <strong>{kind === 'income' ? '↑' : '↓'} {formatBrlDecimal(value)}</strong>
    </div>
  )
}

export function FinancialCalendarGrid({
  month,
  today,
  selectedDate,
  items,
  onSelect,
}: {
  month: string
  today: string
  selectedDate: string
  items: FinancialCalendarItem[]
  onSelect: (date: string) => void
}) {
  const days = useMemo(() => buildMonthDays(month), [month])
  const byDate = useMemo(() => {
    const result = new Map<string, FinancialCalendarItem[]>()
    for (const item of items) result.set(item.date, [...(result.get(item.date) ?? []), item])
    return result
  }, [items])

  return (
    <section className="calendar-grid-section" aria-label={`Calendário de ${formatMonthYear(month)}`}>
      <div className="calendar-weekdays" aria-hidden="true">
        {weekDays.map((day) => <span key={day}>{day}</span>)}
      </div>
      <div className="calendar-grid" role="grid">
        {days.map((date, index) =>
          date ? (
            <FinancialCalendarDay
              key={date}
              date={date}
              today={today}
              selected={date === selectedDate}
              items={byDate.get(date) ?? []}
              onSelect={onSelect}
            />
          ) : (
            <span key={`empty-${index}`} className="calendar-day calendar-day--empty" aria-hidden="true" />
          ),
        )}
      </div>
      <div className="calendar-legend" aria-label="Legenda dos indicadores">
        <span><i className="calendar-marker calendar-marker--realized">✓</i> Realizado</span>
        <span><i className="calendar-marker calendar-marker--pending">○</i> Pendente</span>
        <span><i className="calendar-marker calendar-marker--overdue">!</i> Vencido</span>
        <span>↑ Receita</span>
        <span>↓ Despesa</span>
      </div>
    </section>
  )
}

export function FinancialCalendarDay({
  date,
  today,
  selected,
  items,
  onSelect,
}: {
  date: string
  today: string
  selected: boolean
  items: FinancialCalendarItem[]
  onSelect: (date: string) => void
}) {
  const label = `${formatDate(date)}: ${items.length === 0 ? 'sem acontecimentos' : `${items.length} acontecimento${items.length === 1 ? '' : 's'}`}`
  return (
    <button
      type="button"
      role="gridcell"
      className={`calendar-day${date === today ? ' calendar-day--today' : ''}${selected ? ' calendar-day--selected' : ''}`}
      aria-label={label}
      aria-current={date === today ? 'date' : undefined}
      aria-pressed={selected}
      onClick={() => onSelect(date)}
    >
      <span className="calendar-day__number">{Number(date.slice(-2))}</span>
      <span className="calendar-day__markers" aria-hidden="true">
        {items.slice(0, 4).map((item) => (
          <i key={`${item.source}-${item.id}`} className={`calendar-marker calendar-marker--${item.status.toLowerCase()}`}>
            {item.status === 'Realized' ? '✓' : item.status === 'Overdue' ? '!' : '○'}
            {item.type === 'Income' ? '↑' : '↓'}
          </i>
        ))}
        {items.length > 4 && <small>+{items.length - 4}</small>}
      </span>
    </button>
  )
}

export function FinancialCalendarEvents({
  date,
  items,
  onOpen,
}: {
  date: string
  items: FinancialCalendarItem[]
  onOpen: (item: FinancialCalendarItem, trigger: HTMLButtonElement) => void
}) {
  return (
    <section className="calendar-events" aria-live="polite" aria-labelledby="calendar-events-title">
      <h3 id="calendar-events-title">Compromissos de {formatDate(date)}</h3>
      {items.length === 0 ? (
        <p className="meta">Nenhum acontecimento nesta data.</p>
      ) : (
        <ul className="calendar-event-list">
          {items.map((item) => (
            <li key={`${item.source}-${item.id}`} className={`calendar-event calendar-event--${item.status.toLowerCase()}`}>
              <div className="calendar-event__main">
                <span className="calendar-event__description">{item.description}</span>
                <strong className={item.type === 'Income' ? 'amount amount--income' : 'amount amount--negative'}>
                  {item.type === 'Income' ? '+' : '−'}{formatBrlDecimal(item.amount)}
                </strong>
              </div>
              <div className="calendar-event__meta">
                <span>{typeLabels[item.type]}</span>
                <span>{item.account?.name ?? 'Sem conta atribuída'}{item.account && !item.account.isActive ? ' (inativa)' : ''}</span>
                {item.category && <span>{item.category.name}{item.category.isActive ? '' : ' (inativa)'}</span>}
              </div>
              <span className={`badge badge--calendar-${item.status.toLowerCase()}`}>{statusLabels[item.status]}</span>
              <button
                type="button"
                className="button--link calendar-event__details"
                onClick={(event) => onOpen(item, event.currentTarget)}
                aria-label={`Ver detalhes de ${item.description}`}
              >
                Ver detalhes
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

function buildMonthDays(month: string): Array<string | null> {
  const [year, monthNumber] = month.split('-').map(Number)
  const count = Number(endOfMonth(month).slice(-2))
  const firstWeekDay = new Date(Date.UTC(year, monthNumber - 1, 1)).getUTCDay()
  const result: Array<string | null> = Array(firstWeekDay).fill(null)
  for (let day = 1; day <= count; day += 1) {
    result.push(`${month.slice(0, 7)}-${String(day).padStart(2, '0')}`)
  }
  while (result.length % 7 !== 0) result.push(null)
  return result
}
