import { useEffect, useMemo, useRef, useState } from 'react'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import type { CategoryType } from '../api/categories'
import {
  getFinancialCalendar,
  type FinancialCalendarItem,
  type FinancialCalendarResponse,
  type FinancialCalendarStatus,
} from '../api/financialCalendar'
import { addCalendarMonths, endOfMonth, formatMonthYear, startOfMonth, todayInSaoPaulo } from '../dates'
import { formatBrlDecimal, formatDate } from '../money'

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

/** Primeira versão mensal, somente leitura, da Agenda Financeira. */
export function FinancialCalendarPage({ familyId }: { familyId: string }) {
  const today = todayInSaoPaulo()
  const [month, setMonth] = useState(() => startOfMonth(today))
  const [selectedDate, setSelectedDate] = useState(today)
  const [accountId, setAccountId] = useState('')
  const [type, setType] = useState<CategoryType | ''>('')
  const [accounts, setAccounts] = useState<Load<FinancialAccount[]>>({ kind: 'loading' })
  const requestVersion = useRef(0)
  const from = month
  const through = endOfMonth(month)
  const requestKey = `${familyId}|${from}|${through}|${accountId}|${type}`
  const [calendarState, setCalendar] = useState<RequestLoad<FinancialCalendarResponse>>({
    kind: 'loading',
    requestKey,
  })
  // Ao mudar mês/filtro, o resultado anterior deixa de ser exibido imediatamente, sem setState síncrono no efeito.
  const calendar: RequestLoad<FinancialCalendarResponse> =
    calendarState.requestKey === requestKey ? calendarState : { kind: 'loading', requestKey }

  useEffect(() => {
    const controller = new AbortController()
    listAccounts(familyId, true, controller.signal)
      .then((items) => setAccounts({ kind: 'loaded', data: items }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setAccounts({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId])

  useEffect(() => {
    const controller = new AbortController()
    const version = ++requestVersion.current
    getFinancialCalendar(
      familyId,
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
  }, [familyId, from, through, accountId, type, requestKey])

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
        onAccountChange={setAccountId}
        onTypeChange={setType}
      />

      {calendar.kind === 'loading' && <p className="status status--pending">Carregando agenda…</p>}
      {calendar.kind === 'error' && (
        <p className="status status--down" role="alert">
          {calendar.message}
        </p>
      )}
      {calendar.kind === 'loaded' && (
        <>
          <FinancialCalendarSummary summary={calendar.data.summary} />
          <div className="calendar-layout">
            <FinancialCalendarGrid
              month={month}
              today={today}
              selectedDate={selectedDate}
              items={calendar.data.items}
              onSelect={setSelectedDate}
            />
            <FinancialCalendarEvents date={selectedDate} items={selectedItems} />
          </div>
        </>
      )}
    </section>
  )
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
        <label>
          Tipo
          <select value={type} onChange={(event) => onTypeChange(event.target.value as CategoryType | '')}>
            <option value="">Todas</option>
            <option value="Income">Receitas</option>
            <option value="Expense">Despesas</option>
          </select>
        </label>
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

export function FinancialCalendarEvents({ date, items }: { date: string; items: FinancialCalendarItem[] }) {
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
                <span>{item.account.name}{item.account.isActive ? '' : ' (inativa)'}</span>
                {item.category && <span>{item.category.name}{item.category.isActive ? '' : ' (inativa)'}</span>}
              </div>
              <span className={`badge badge--calendar-${item.status.toLowerCase()}`}>{statusLabels[item.status]}</span>
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
