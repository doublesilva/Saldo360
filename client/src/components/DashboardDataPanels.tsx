import { useCallback, useEffect, useState } from 'react'
import { getFinancialCalendar, getFinancialCalendarBalances, type FinancialCalendarBalanceResponse } from '../api/financialCalendar'
import { getCreditCardLimit, listCreditCards, listInvoices } from '../api/creditCards'
import type { FinancialDashboardResponse } from '../api/dashboard'
import { addCalendarMonths, endOfMonth, todayInSaoPaulo } from '../dates'
import { formatBrlDecimal, formatDate } from '../money'
import { OperationalError, OperationalLoading } from './OperationalFeedback'

function usePanel<T>(key: string, load: (signal: AbortSignal) => Promise<T>) {
  const [attempt, setAttempt] = useState(0)
  const requestKey = `${key}|${attempt}`
  const [state, setState] = useState<{ key: string; kind: 'loaded'; data: T } | { key: string; kind: 'error'; error: unknown } | { key: string; kind: 'loading' }>({ key: requestKey, kind: 'loading' })
  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal).then(data => { if (!controller.signal.aborted) setState({ key: requestKey, kind: 'loaded', data }) }).catch(error => { if (!controller.signal.aborted) setState({ key: requestKey, kind: 'error', error }) })
    return () => controller.abort()
  }, [requestKey, load])
  return { state: state.key === requestKey ? state : { kind: 'loading' as const }, retry: () => setAttempt(value => value + 1) }
}

export function DashboardCommitments({ workspaceId, competence, months, data, onAgenda }: { workspaceId: string; competence: string; months: number; data: FinancialDashboardResponse; onAgenda?: () => void }) {
  const from = `${competence}-01`, through = endOfMonth(addCalendarMonths(from, months - 1))
  const load = useCallback(async (signal: AbortSignal) => {
    const result = await getFinancialCalendar(workspaceId, from, through, { type: 'Expense' }, signal)
    if (!Array.isArray(result.items)) throw new Error('Resposta da Agenda indisponível.')
    return result.items.filter(item => item.type === 'Expense' && (item.status === 'Pending' || item.status === 'Overdue')).sort((a, b) => a.date.localeCompare(b.date) || a.id.localeCompare(b.id))
  }, [workspaceId, from, through])
  const { state, retry } = usePanel(`${workspaceId}|${from}|${through}`, load)
  const today = todayInSaoPaulo()
  return <section className="card dashboard-next" aria-labelledby="dashboard-next-title"><h5 id="dashboard-next-title">Próximos compromissos</h5><p className="meta">Despesas pendentes da Agenda · {formatDate(from)} a {formatDate(through)}. Faturas aparecem no card de cartões.</p>
    {state.kind === 'loading' && <OperationalLoading>Carregando compromissos…</OperationalLoading>}
    {state.kind === 'error' && <OperationalError error={state.error} onRetry={retry} retryLabel="Recarregar compromissos" />}
    {state.kind === 'loaded' && (state.data.length ? <ol className="dashboard-commitments">{state.data.slice(0, 5).map(item => {
      const status = item.status === 'Overdue' || item.date < today ? 'Atrasado' : item.date === today ? 'Vence hoje' : 'Futuro'
      return <li key={`${item.source}:${item.id}`} className={status === 'Atrasado' ? 'dashboard-next__overdue' : status === 'Vence hoje' ? 'dashboard-commitment--today' : undefined}><strong>{item.description}</strong><span>{formatDate(item.date)} · {formatBrlDecimal(item.amount)}</span><small>{status}</small><button type="button" onClick={onAgenda}>Ver na Agenda</button></li>
    })}</ol> : <p className="empty-state">Nenhuma despesa pendente na Agenda para este período.</p>)}
    <p className="meta">{data.indicators.capacityBeforeNextIncome.status === 'Unknown' ? 'Disponibilidade antes do próximo recebimento indeterminada.' : 'Consulte os recebimentos e sua cobertura no Planejamento.'}</p>
    <button type="button" onClick={onAgenda}>Abrir Agenda</button>
  </section>
}

export function DashboardCardDetails({ workspaceId, competence }: { workspaceId: string; competence: string }) {
  const load = useCallback(async (signal: AbortSignal) => {
    const cards = await listCreditCards(workspaceId, false, signal)
    if (!Array.isArray(cards)) throw new Error('Lista de cartões indisponível.')
    return Promise.all(cards.map(async card => {
      const [limit, invoices] = await Promise.allSettled([getCreditCardLimit(workspaceId, card.id, signal), listInvoices(workspaceId, card.id, competence, competence, signal)])
      const moneyText = (value: unknown) => typeof value === 'string' && /^-?\d+\.\d{2}$/.test(value)
      const validLimit = limit.status === 'fulfilled' && [limit.value.creditLimit, limit.value.usedLimit, limit.value.availableLimit].every(moneyText)
      const validInvoices = invoices.status === 'fulfilled' && Array.isArray(invoices.value) && invoices.value.every(invoice => typeof invoice.id === 'string' && typeof invoice.dueDate === 'string' && moneyText(invoice.totalAmount) && moneyText(invoice.remainingAmount))
      return { card, limit: validLimit && limit.status === 'fulfilled' ? limit.value : null, invoices: validInvoices && invoices.status === 'fulfilled' ? invoices.value : null }
    }))
  }, [workspaceId, competence])
  const { state, retry } = usePanel(`${workspaceId}|${competence}`, load)
  return <div className="dashboard-card-details"><h6>Cartões e faturas da competência</h6><p className="meta">Limites consultados no estado atual. Crédito não é saldo bancário nem patrimônio.</p>
    {state.kind === 'loading' && <OperationalLoading>Carregando cartões e faturas…</OperationalLoading>}
    {state.kind === 'error' && <OperationalError error={state.error} onRetry={retry} retryLabel="Recarregar cartões" />}
    {state.kind === 'loaded' && (state.data.length ? <ul>{state.data.map(({ card, limit, invoices }) => <li key={card.id}><strong>{card.name}</strong>
      {limit ? <p>Limite utilizado {formatBrlDecimal(limit.usedLimit)} · disponível {formatBrlDecimal(limit.availableLimit)} · total {formatBrlDecimal(limit.creditLimit)}</p> : <p>Utilização do limite indisponível.</p>}
      {invoices === null ? <p>Faturas indisponíveis; não significa fatura zerada.</p> : invoices.length ? invoices.map(invoice => <p key={invoice.id}>Fatura {invoice.competence} · vence {formatDate(invoice.dueDate)} · total {formatBrlDecimal(invoice.totalAmount)} · restante {formatBrlDecimal(invoice.remainingAmount)} · {invoice.paymentStatus === 'Paid' ? 'Paga' : invoice.paymentStatus === 'PartiallyPaid' ? 'Parcialmente paga' : 'Não paga'}</p>) : <p>Nenhuma fatura emitida nesta competência. Obrigações ainda não vinculadas podem existir.</p>}
    </li>)}</ul> : <p className="empty-state">Nenhum cartão cadastrado.</p>)}
    {state.kind === 'loaded' && state.data.some(item => item.limit === null || item.invoices === null) && <button type="button" onClick={retry}>Recarregar detalhes dos cartões</button>}
  </div>
}

export function DashboardDailyEvolution({ workspaceId, competence, months }: { workspaceId: string; competence: string; months: number }) {
  const from = `${competence}-01`, through = endOfMonth(addCalendarMonths(from, months - 1))
  const load = useCallback(async (signal: AbortSignal) => {
    const result = await getFinancialCalendarBalances(workspaceId, from, through, undefined, signal)
    if (!Array.isArray(result.days)) throw new Error('Evolução diária indisponível.')
    return result
  }, [workspaceId, from, through])
  const { state, retry } = usePanel(`${workspaceId}|${from}|${through}`, load)
  return <div className="dashboard-daily"><p className="meta">Evolução diária · {formatDate(from)} a {formatDate(through)}. Linha contínua: realizado; tracejada: projetado.</p>
    {state.kind === 'loading' && <OperationalLoading>Carregando evolução diária…</OperationalLoading>}
    {state.kind === 'error' && <OperationalError error={state.error} onRetry={retry} retryLabel="Recarregar evolução" />}
    {state.kind === 'loaded' && <DailyChart balance={state.data} />}
  </div>
}

function DailyChart({ balance }: { balance: FinancialCalendarBalanceResponse }) {
  if (balance.unavailableReason) return <p role="status">Evolução indisponível: {balance.unavailableReason === 'BeforeOpeningBalanceDate' ? 'o período antecede a data do saldo inicial.' : 'verifique o saldo inicial das contas.'}</p>
  // Only backend ChartPosition determines vertical coordinates. Do not bridge missing days.
  const point = (day: FinancialCalendarBalanceResponse['days'][number], index: number) => ({ day, x: balance.days.length === 1 ? 500 : 20 + index * 960 / (balance.days.length - 1), y: 20 + (day.chartPosition ?? 0) * .2 })
  const points = balance.days.flatMap((day, index) => day.closingBalance === null || day.chartPosition === null ? [] : [point(day, index)])
  if (!points.length) return <p className="empty-state">Nenhum saldo diário disponível para o gráfico.</p>
  const segments = (kind: 'Realized' | 'Projected') => balance.days.reduce<ReturnType<typeof point>[][]>((groups, day, index) => {
    if (day.kind !== kind || day.closingBalance === null || day.chartPosition === null) { if (groups[groups.length - 1]?.length) groups.push([]); return groups }
    if (!groups.length) groups.push([])
    groups[groups.length - 1].push(point(day, index)); return groups
  }, []).filter(group => group.length > 1)
  return <>{(balance.creditCardProjectionComplete === false || balance.debtProjectionComplete === false) && <p className="notice">Projeção diária parcial: existem obrigações sem conta de pagamento/responsável.</p>}<svg className="balance-chart" viewBox="0 0 1000 240" role="img" aria-label="Evolução diária do saldo realizado e projetado; valores no detalhamento diário">
    {(['Realized', 'Projected'] as const).flatMap(kind => segments(kind).map((segment, index) => <polyline key={`${kind}:${index}`} className={`balance-chart__line${kind === 'Projected' ? ' balance-chart__line--projected' : ''}`} points={segment.map(p => `${p.x},${p.y}`).join(' ')} />))}
    {points.map(({ day, x, y }) => <circle key={day.date} cx={x} cy={y} r="5" className={`balance-chart__point balance-chart__point--${day.kind.toLowerCase()}${day.isNegative ? ' balance-chart__point--negative' : ''}`}><title>{formatDate(day.date)} · {day.kind === 'Realized' ? 'Realizado' : 'Projetado'} · {formatBrlDecimal(day.closingBalance!)}</title></circle>)}
  </svg><details><summary>Detalhamento diário</summary><ul className="dashboard-daily-values">{balance.days.map(day => <li key={day.date}>{formatDate(day.date)} · {day.kind === 'Realized' ? 'Realizado' : 'Projetado'} · {day.closingBalance === null ? 'Indisponível' : formatBrlDecimal(day.closingBalance)}</li>)}</ul></details></>
}
