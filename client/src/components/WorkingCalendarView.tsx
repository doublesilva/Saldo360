import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { addNonWorkingDay, getWorkingDays, removeNonWorkingDay, type WorkingDays } from '../api/monthlyPlan'
import { toUserMessage } from '../api/apiErrors'
import { endOfMonth, formatMonthYear } from '../dates'
import { formatDate } from '../money'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import '../MonthlyPlan.css'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: WorkingDays }

/**
 * Calendário de dias úteis da receita por hora: o usuário cadastra feriados e dias não trabalhados. O sistema não presume feriados e a contagem de dias
 * úteis (segunda a sexta, sem os dias cadastrados) é feita pelo backend. Nem todo dia útil é faturável: os dias faturáveis são informados em cada receita.
 */
export function WorkingCalendarView({ workspaceId, competence }: { workspaceId: string; competence: string }) {
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${competence}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const [date, setDate] = useState('')
  const [kind, setKind] = useState<'Holiday' | 'NonWorking'>('Holiday')
  const [description, setDescription] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getWorkingDays(workspaceId, competence, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((failure: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error: failure }) })
    return () => controller.abort()
  }, [workspaceId, competence, key])

  const refresh = useCallback(() => setReload(value => value + 1), [])
  const add = async (event: FormEvent) => {
    event.preventDefault()
    if (!date) return setError('Informe a data.')
    if (!description.trim()) return setError('Informe a descrição.')
    setBusy(true); setError(null)
    try { await addNonWorkingDay(workspaceId, date, kind, description.trim()); setDate(''); setDescription(''); refresh() } catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }
  const remove = async (id: string) => {
    setBusy(true); setError(null)
    try { await removeNonWorkingDay(workspaceId, id, competence); refresh() } catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }

  return <div className="monthly-plan">
    <p className="meta">Dias úteis de {formatMonthYear(`${competence}-01`)} para o cálculo da receita por hora. O calendário começa vazio: cadastre os feriados e dias não trabalhados.</p>
    {visible.state === 'loading' && <OperationalLoading>Carregando o calendário…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={refresh} />}
    {visible.state === 'loaded' && <>
      <dl className="plan-card__facts monthly-plan__days" aria-label="Contagem de dias do mês">
        <div><dt>Dias no mês</dt><dd>{visible.data.calendarDays}</dd></div>
        <div><dt>Dias de segunda a sexta</dt><dd>{visible.data.weekdays}</dd></div>
        <div><dt>Feriados e dias não trabalhados em dia de semana</dt><dd>{visible.data.nonWorkingWeekdays}</dd></div>
        <div><dt>Dias úteis</dt><dd><strong>{visible.data.businessDays}</strong></dd></div>
      </dl>
      {visible.data.nonWorkingDays.length === 0 ? <p className="meta">Nenhum feriado ou dia não trabalhado cadastrado neste mês.</p>
        : <ul className="monthly-plan__days-list">{visible.data.nonWorkingDays.map(day => <li key={day.id}>
          <span>{formatDate(day.date)} · {day.kind === 'Holiday' ? 'Feriado' : 'Dia não trabalhado'} · {day.description}{day.weekend ? ' (fim de semana: não altera os dias úteis)' : ''}</span>
          <button type="button" onClick={() => void remove(day.id)} disabled={busy} aria-label={`Remover ${day.description}`}>Remover</button>
        </li>)}</ul>}
    </>}
    <form className="dialog monthly-plan__form" onSubmit={event => void add(event)} aria-label="Cadastrar dia não trabalhado">
      <h5>Cadastrar feriado ou dia não trabalhado</h5>
      {error && <p className="status status--down" role="alert">{error}</p>}
      <label>Data<input type="date" value={date} min={`${competence}-01`} max={endOfMonth(`${competence}-01`)} onChange={event => setDate(event.target.value)} /></label>
      <label>Tipo<select value={kind} onChange={event => setKind(event.target.value as 'Holiday' | 'NonWorking')}><option value="Holiday">Feriado</option><option value="NonWorking">Dia não trabalhado</option></select></label>
      <label>Descrição<input value={description} onChange={event => setDescription(event.target.value)} maxLength={100} /></label>
      <div className="form__actions"><button className="button--primary" type="submit" disabled={busy}>Cadastrar dia</button></div>
    </form>
  </div>
}
