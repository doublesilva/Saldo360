import { useState } from 'react'
import { getIncomeForecast, removeForecastMonth, saveForecastMonth, saveIncomeForecast, type IncomeForecastMonth, type IncomeForecastSettings } from '../api/monthlyPlan'
import { formatDate } from '../money'
import { formatMonthYear } from '../dates'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import { money } from './monthlyPlanText'

const statusLabels = { Forecast: 'Estimativa', Realized: 'Realizada (substitui)', Dismissed: 'Descartada' } as const

/**
 * Configuração da previsão de receita PJ por horas (PROJ-01). Só a configuração APROVADA entra na projeção; salvar nunca cria lançamento. Carrega ao abrir, para que a visão
 * de seis meses não dependa dela. O cálculo (dias úteis de São Paulo, valor bruto, reserva) é do backend: o navegador envia parâmetros e exibe o resultado.
 */
export function IncomeForecastEditor({ workspaceId, from, onChanged }: { workspaceId: string; from: string; onChanged: () => void }) {
  const [settings, setSettings] = useState<IncomeForecastSettings | null>(null)
  const [state, setState] = useState<'closed' | 'loading' | 'ready' | 'error'>('closed')
  const [error, setError] = useState<unknown>(null)
  const [saving, setSaving] = useState(false)

  const apply = (next: IncomeForecastSettings) => { setSettings(next); setState('ready'); setError(null) }
  const run = async (action: () => Promise<IncomeForecastSettings>) => {
    setSaving(true)
    try { apply(await action()); onChanged() } catch (failure) { setError(failure) } finally { setSaving(false) }
  }
  const open = () => {
    if (state !== 'closed') return
    setState('loading')
    getIncomeForecast(workspaceId, from).then(apply).catch((failure: unknown) => { setError(failure); setState('error') })
  }

  return <details className="monthly-plan__forecast" onToggle={event => { if ((event.currentTarget as HTMLDetailsElement).open) open() }}>
    <summary>Previsão de receita PJ por horas</summary>
    {state === 'loading' && <OperationalLoading>Carregando a previsão de receita…</OperationalLoading>}
    {state === 'error' && <OperationalError error={error} />}
    {state === 'ready' && settings && <>
      {error !== null && <OperationalError error={error} />}
      <ForecastForm key={JSON.stringify([settings.exists, settings.isActive, settings.accountId, settings.hourlyRate, settings.hoursPerDay, settings.paymentDay, settings.paymentOffsetMonths, settings.taxReservePercent, settings.operationalReserve])}
        settings={settings} saving={saving} onSave={input => run(() => saveIncomeForecast(workspaceId, input))} />
      {settings.exists && <ForecastMonths months={settings.months} saving={saving}
        onAdjust={(worked, input) => run(() => saveForecastMonth(workspaceId, worked, input))} onRestore={worked => run(() => removeForecastMonth(workspaceId, worked))} />}
    </>}
  </details>
}

function ForecastForm({ settings, saving, onSave }: { settings: IncomeForecastSettings; saving: boolean; onSave: (input: Parameters<typeof saveIncomeForecast>[1]) => void }) {
  const [form, setForm] = useState({
    accountId: settings.accountId ?? '', hourlyRate: settings.hourlyRate, hoursPerDay: settings.hoursPerDay, paymentDay: String(settings.paymentDay),
    paymentOffsetMonths: String(settings.paymentOffsetMonths), taxReservePercent: settings.taxReservePercent, operationalReserve: settings.operationalReserve, isActive: settings.isActive,
  })
  const set = (key: keyof typeof form) => (event: { target: { value: string; type?: string; checked?: boolean } }) =>
    setForm(current => ({ ...current, [key]: event.target.type === 'checkbox' ? Boolean(event.target.checked) : event.target.value }))
  return <form className="monthly-plan__form" onSubmit={event => {
    event.preventDefault()
    onSave({ accountId: form.accountId || null, hourlyRate: form.hourlyRate, hoursPerDay: form.hoursPerDay, paymentDay: Number(form.paymentDay), paymentOffsetMonths: Number(form.paymentOffsetMonths),
      taxReservePercent: form.taxReservePercent, operationalReserve: form.operationalReserve, isActive: form.isActive })
  }}>
    <p className="meta">{settings.exists ? 'Parâmetros salvos.' : 'Valores iniciais sugeridos: nada foi salvo e nada entra na projeção.'} Dias úteis seguem o calendário de São Paulo/SP; ajuste os dias faturáveis de cada competência abaixo.</p>
    <label>Conta de recebimento
      <select value={form.accountId} onChange={set('accountId')} required><option value="">Escolha a conta</option>{settings.accounts.map(account => <option key={account.id} value={account.id}>{account.name} · {account.scope}</option>)}</select></label>
    <label>Valor por hora (R$)<input inputMode="decimal" value={form.hourlyRate} onChange={set('hourlyRate')} required /></label>
    <label>Horas por dia<input inputMode="decimal" value={form.hoursPerDay} onChange={set('hoursPerDay')} required /></label>
    <label>Dia esperado de pagamento<input type="number" min={1} max={31} value={form.paymentDay} onChange={set('paymentDay')} required /></label>
    <label>Recebimento em quantos meses após o mês trabalhado
      <select value={form.paymentOffsetMonths} onChange={set('paymentOffsetMonths')}><option value="0">No mesmo mês</option><option value="1">No mês seguinte</option><option value="2">Dois meses depois</option><option value="3">Três meses depois</option></select></label>
    <label>Reserva de tributos (% do faturamento bruto)<input inputMode="decimal" value={form.taxReservePercent} onChange={set('taxReservePercent')} required /></label>
    <label>Reserva operacional (R$)<input inputMode="decimal" value={form.operationalReserve} onChange={set('operationalReserve')} /></label>
    <label className="monthly-plan__check"><input type="checkbox" checked={form.isActive} onChange={set('isActive')} /> Aprovar esta previsão para a projeção</label>
    <p className="meta">A reserva de tributos reduz o valor disponível para repasse, mas não é debitada do saldo bancário. Nenhum lançamento é criado.</p>
    <button type="submit" className="button--primary" disabled={saving}>{saving ? 'Salvando…' : 'Salvar previsão'}</button>
  </form>
}

function ForecastMonths({ months, saving, onAdjust, onRestore }: {
  months: IncomeForecastMonth[]; saving: boolean
  onAdjust: (workedCompetence: string, input: { billableDays?: number | null; expectedAmount?: string | null; paymentDate?: string | null; isDismissed: boolean }) => void
  onRestore: (workedCompetence: string) => void
}) {
  if (months.length === 0) return <p className="meta">Nenhum recebimento previsto nos próximos seis meses.</p>
  return <div className="monthly-plan__table-wrap"><table className="monthly-plan__table">
    <caption className="sr-only">Recebimentos previstos por competência trabalhada</caption>
    <thead><tr><th scope="col">Competência trabalhada</th><th scope="col">Recebimento</th><th scope="col">Dias faturáveis</th><th scope="col">Valor bruto</th><th scope="col">Reserva de tributos</th><th scope="col">Situação</th><th scope="col">Ajuste</th></tr></thead>
    <tbody>{months.map(month => <MonthRow key={month.workedCompetence} month={month} saving={saving} onAdjust={onAdjust} onRestore={onRestore} />)}</tbody>
  </table></div>
}

function MonthRow({ month, saving, onAdjust, onRestore }: {
  month: IncomeForecastMonth; saving: boolean
  onAdjust: (workedCompetence: string, input: { billableDays?: number | null; expectedAmount?: string | null; paymentDate?: string | null; isDismissed: boolean }) => void
  onRestore: (workedCompetence: string) => void
}) {
  const [days, setDays] = useState(String(month.billableDays))
  const adjusted = month.daysAdjusted || month.amountAdjusted || month.dateAdjusted
  // O ajuste grava a competência inteira: reenviar os ajustes já existentes evita perdê-los ao mexer em apenas um deles.
  const kept = {
    billableDays: month.daysAdjusted ? month.billableDays : null, expectedAmount: month.amountAdjusted ? month.grossAmount : null, paymentDate: month.dateAdjusted ? month.paymentDate : null,
  }
  const worked = formatMonthYear(`${month.workedCompetence}-01`)
  return <tr>
    <th scope="row">{worked}</th>
    <td>{formatDate(month.paymentDate)}{month.dateAdjusted && <small>data ajustada</small>}</td>
    <td><input aria-label={`Dias faturáveis de ${worked}`} type="number" min={0} max={31} value={days} onChange={event => setDays(event.target.value)} />
      <small>{month.businessDays} dias úteis{month.daysAdjusted ? ' · ajustado' : ''}</small></td>
    <td>{money(month.grossAmount)}{month.amountAdjusted && <small>valor ajustado</small>}</td>
    <td>{money(month.taxReserve)}</td>
    <td>{statusLabels[month.status]}{month.status === 'Realized' && <small>{money(month.realizedAmount)} recebidos</small>}</td>
    <td>
      <button type="button" disabled={saving} onClick={() => onAdjust(month.workedCompetence, { ...kept, billableDays: Number(days), isDismissed: month.status === 'Dismissed' })}>Aplicar dias</button>
      <button type="button" disabled={saving} onClick={() => onAdjust(month.workedCompetence, { ...kept, isDismissed: month.status !== 'Dismissed' })}>{month.status === 'Dismissed' ? 'Reativar' : 'Descartar'}</button>
      {adjusted && <button type="button" disabled={saving} onClick={() => onRestore(month.workedCompetence)}>Restaurar cálculo</button>}
    </td>
  </tr>
}
