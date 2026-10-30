import { useEffect, useState, type FormEvent } from 'react'
import { copyMonthlyPlan, createPlanLine, getPlanHistory, previewHourlyIncome, revisePlanLine, setCategoryLimit, type CategoryLimit, type CopyResult, type HourlyIncomePreview, type PlanHistoryEntry, type PlanLine, type PlanLineInput, type PlanLineKind, type PlanLineNature } from '../api/monthlyPlan'
import type { ScopeAccount } from '../api/planActual'
import type { FinancialCategory } from '../api/categories'
import { toUserMessage } from '../api/apiErrors'
import { decimalToInput, parseAmount } from '../money'
import { addCalendarMonths, endOfMonth, formatMonthYear } from '../dates'
import { actionLabels, dateTime, money, natureLabels } from './monthlyPlanText'

const natures = (kind: PlanLineKind): PlanLineNature[] => kind === 'Expense' ? ['Fixed', 'Variable', 'Recurring', 'Extraordinary', 'Reserve'] : ['Recurring', 'Fixed', 'Variable', 'Extraordinary']

/** Normaliza horas por dia digitadas em pt-BR ("7,5") para o texto decimal que a API espera. */
const hoursText = (value: string) => value.trim().replace(',', '.')

/**
 * Formulário de uma linha do planejamento: receita, despesa ou transferência prevista. Na receita por hora, o valor esperado é calculado pelo backend
 * (pré-visualização) a partir do valor/hora, das horas por dia e dos dias faturáveis informados: o navegador não calcula dinheiro.
 */
export function PlanLineForm({ workspaceId, competence, kind, line, accounts, categories, onSaved, onCancel }: {
  workspaceId: string; competence: string; kind: PlanLineKind; line?: PlanLine; accounts: ScopeAccount[]; categories: FinancialCategory[]; onSaved: () => void; onCancel: () => void
}) {
  const [description, setDescription] = useState(line?.description ?? '')
  const [nature, setNature] = useState<PlanLineNature>(line?.nature ?? (kind === 'Income' ? 'Recurring' : 'Fixed'))
  const [accountId, setAccountId] = useState(line?.accountId ?? '')
  const [destinationId, setDestinationId] = useState(line?.destinationAccountId ?? '')
  const [categoryId, setCategoryId] = useState(line?.categoryId ?? '')
  const [mode, setMode] = useState<'fixed' | 'hourly'>(line?.hourly ? 'hourly' : 'fixed')
  const [amount, setAmount] = useState(line && !line.hourly ? decimalToInput(line.amount) : '')
  const [rate, setRate] = useState(line?.hourly ? decimalToInput(line.hourly.hourlyRate) : '')
  const [hours, setHours] = useState(line?.hourly?.hoursPerDay.replace('.', ',') ?? '8')
  const [days, setDays] = useState(line?.hourly ? String(line.hourly.billableDays) : '')
  const [expectedDate, setExpectedDate] = useState(line?.expectedDate ?? '')
  const [note, setNote] = useState('')
  const [preview, setPreview] = useState<{ key: string; data: HourlyIncomePreview } | { key: string; error: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const rateParsed = parseAmount(rate, 'O valor por hora')
  const daysNumber = /^\d+$/.test(days.trim()) ? Number.parseInt(days, 10) : null
  const hoursValid = /^\d+([.,]\d{1,2})?$/.test(hours.trim())

  // Pré-visualização do valor por hora: sempre calculada pelo backend (centavos exatos e dias úteis do calendário configurado).
  const previewKey = mode === 'hourly' && rateParsed.kind === 'ok' && hoursValid && daysNumber !== null && daysNumber >= 1 ? `${rateParsed.text}|${hoursText(hours)}|${daysNumber}` : null
  useEffect(() => {
    if (previewKey === null) return
    const [hourlyRate, hoursPerDay, billableDays] = previewKey.split('|')
    const controller = new AbortController()
    previewHourlyIncome(workspaceId, competence, hourlyRate, hoursPerDay, Number.parseInt(billableDays, 10), controller.signal)
      .then(data => { if (!controller.signal.aborted) setPreview({ key: previewKey, data }) })
      .catch((failure: unknown) => { if (!controller.signal.aborted) setPreview({ key: previewKey, error: toUserMessage(failure) }) })
    return () => controller.abort()
  }, [workspaceId, competence, previewKey])
  const current = previewKey !== null && preview?.key === previewKey ? preview : null

  const accountOptions = accounts.filter(account => account.isActive || account.accountId === line?.accountId || account.accountId === line?.destinationAccountId)
  const categoryOptions = categories.filter(category => category.type === (kind === 'Income' ? 'Income' : 'Expense') && (category.isActive || category.id === line?.categoryId))

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (!description.trim()) return setError('Informe a descrição.')
    if (kind === 'Transfer' && (!accountId || !destinationId)) return setError('Informe as contas de origem e de destino.')
    if (kind === 'Transfer' && accountId === destinationId) return setError('As contas de origem e de destino devem ser diferentes.')
    const input: PlanLineInput = {
      competence, kind, nature: kind === 'Transfer' ? null : nature, description: description.trim(), accountId: accountId || null,
      destinationAccountId: kind === 'Transfer' ? destinationId : null, categoryId: kind === 'Transfer' ? null : categoryId || null, expectedDate: expectedDate || null, note: note.trim() || null,
    }
    if (mode === 'hourly') {
      if (rateParsed.kind !== 'ok') return setError(rateParsed.kind === 'invalid' ? rateParsed.message : 'Informe o valor por hora.')
      if (!hoursValid) return setError('Informe as horas por dia (até duas casas decimais).')
      if (daysNumber === null || daysNumber < 1) return setError('Informe a quantidade prevista de dias faturáveis.')
      Object.assign(input, { hourlyRate: rateParsed.text, hoursPerDay: hoursText(hours), billableDays: daysNumber })
    } else {
      const parsed = parseAmount(amount, 'O valor')
      if (parsed.kind !== 'ok') return setError(parsed.kind === 'invalid' ? parsed.message : 'Informe o valor.')
      input.amount = parsed.text
    }
    setBusy(true); setError(null)
    try { if (line) await revisePlanLine(workspaceId, line.id, input); else await createPlanLine(workspaceId, input); onSaved() } catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }

  const title = `${line ? 'Revisar' : 'Adicionar'} ${kind === 'Income' ? 'receita planejada' : kind === 'Expense' ? 'despesa planejada' : 'transferência prevista'}`
  return <form className="dialog monthly-plan__form" role="dialog" aria-labelledby="plan-line-title" onSubmit={event => void submit(event)}>
    <h5 id="plan-line-title">{title} · {formatMonthYear(`${competence}-01`)}</h5>
    {kind === 'Transfer' && <p className="meta">A transferência planejada só prevê o caixa de cada conta: não é receita nem despesa consolidada e não cria nenhuma transferência realizada.</p>}
    {error && <p className="status status--down" role="alert">{error}</p>}
    <label>Descrição<input value={description} onChange={event => setDescription(event.target.value)} maxLength={200} required /></label>
    {kind !== 'Transfer' && <label>Natureza<select value={nature} onChange={event => setNature(event.target.value as PlanLineNature)}>{natures(kind).map(item => <option key={item} value={item}>{natureLabels[item]}</option>)}</select></label>}
    {kind === 'Expense' && nature === 'Reserve' && <p className="meta">Reservas e provisões entram no plano, mas não são saída bancária: ficam fora da comparação com as despesas realizadas.</p>}
    <label>{kind === 'Transfer' ? 'Conta de origem' : 'Conta'}
      <select value={accountId} onChange={event => setAccountId(event.target.value)} required={kind === 'Transfer'}>
        <option value="">{kind === 'Transfer' ? 'Selecione' : 'Consolidado (sem conta específica)'}</option>
        {accountOptions.map(account => <option key={account.accountId} value={account.accountId}>{account.name} · {account.scope}{account.isActive ? '' : ' (inativa)'}</option>)}
      </select>
    </label>
    {kind === 'Transfer' && <label>Conta de destino
      <select value={destinationId} onChange={event => setDestinationId(event.target.value)} required>
        <option value="">Selecione</option>
        {accountOptions.map(account => <option key={account.accountId} value={account.accountId}>{account.name} · {account.scope}{account.isActive ? '' : ' (inativa)'}</option>)}
      </select>
    </label>}
    {kind !== 'Transfer' && <label>Categoria (opcional)
      <select value={categoryId} onChange={event => setCategoryId(event.target.value)}>
        <option value="">Sem categoria</option>
        {categoryOptions.map(category => <option key={category.id} value={category.id}>{category.name}{category.isActive ? '' : ' (inativa)'}</option>)}
      </select>
    </label>}
    {kind === 'Income' && <fieldset className="monthly-plan__mode"><legend>Como calcular o valor</legend>
      <label><input type="radio" name="plan-mode" checked={mode === 'fixed'} onChange={() => setMode('fixed')} /> Valor do mês</label>
      <label><input type="radio" name="plan-mode" checked={mode === 'hourly'} onChange={() => setMode('hourly')} /> Por hora trabalhada</label>
    </fieldset>}
    {mode === 'fixed'
      ? <label>Valor planejado<input inputMode="decimal" value={amount} onChange={event => setAmount(event.target.value)} placeholder="0,00" /></label>
      : <div className="monthly-plan__hourly">
        <label>Valor por hora<input inputMode="decimal" value={rate} onChange={event => setRate(event.target.value)} placeholder="0,00" /></label>
        <label>Horas por dia<input inputMode="decimal" value={hours} onChange={event => setHours(event.target.value)} /></label>
        <label>Dias faturáveis<input inputMode="numeric" value={days} onChange={event => setDays(event.target.value)} /></label>
        <p className="meta" role="status">{current && 'data' in current ? <>Valor esperado do mês: <strong>{money(current.data.amount)}</strong> · o calendário tem {current.data.businessDays} dia(s) útil(eis); nem todo dia útil é faturável.</> : current && 'error' in current ? current.error : 'Informe valor por hora, horas por dia e dias faturáveis para calcular.'}</p>
      </div>}
    <label>Data esperada (opcional)<input type="date" value={expectedDate} min={`${competence}-01`} max={endOfMonth(`${competence}-01`)} onChange={event => setExpectedDate(event.target.value)} /></label>
    {line && <label>Motivo da revisão (opcional)<input value={note} onChange={event => setNote(event.target.value)} maxLength={300} /></label>}
    <div className="form__actions"><button className="button--primary" type="submit" disabled={busy}>{busy ? 'Salvando…' : 'Salvar'}</button><button type="button" onClick={onCancel} disabled={busy}>Cancelar</button></div>
  </form>
}

/** Limite mensal por categoria de despesa: é um teto de gastos, nunca uma obrigação, e não cria despesa nem previsão. */
export function CategoryLimitForm({ workspaceId, competence, limit, categories, onSaved, onCancel }: {
  workspaceId: string; competence: string; limit?: CategoryLimit; categories: FinancialCategory[]; onSaved: () => void; onCancel: () => void
}) {
  const [categoryId, setCategoryId] = useState(limit?.categoryId ?? '')
  const [amount, setAmount] = useState(limit ? decimalToInput(limit.amount) : '')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const options = categories.filter(category => category.type === 'Expense' && (category.isActive || category.id === limit?.categoryId))
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (!categoryId) return setError('Selecione a categoria.')
    const parsed = parseAmount(amount, 'O limite')
    if (parsed.kind !== 'ok') return setError(parsed.kind === 'invalid' ? parsed.message : 'Informe o limite.')
    if (parsed.text.startsWith('-')) return setError('O limite não pode ser negativo.')
    setBusy(true); setError(null)
    try { await setCategoryLimit(workspaceId, competence, categoryId, parsed.text, note.trim()); onSaved() } catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }
  return <form className="dialog monthly-plan__form" role="dialog" aria-labelledby="plan-limit-title" onSubmit={event => void submit(event)}>
    <h5 id="plan-limit-title">{limit ? 'Revisar limite por categoria' : 'Definir limite por categoria'} · {formatMonthYear(`${competence}-01`)}</h5>
    <p className="meta">O limite é um teto de gastos da categoria. Ele não cria despesa nem previsão, e os gastos que ultrapassarem o limite não o alteram.</p>
    {error && <p className="status status--down" role="alert">{error}</p>}
    <label>Categoria de despesa
      <select value={categoryId} onChange={event => setCategoryId(event.target.value)} disabled={limit !== undefined} required>
        <option value="">Selecione</option>
        {options.map(category => <option key={category.id} value={category.id}>{category.name}</option>)}
      </select>
    </label>
    <label>Limite mensal<input inputMode="decimal" value={amount} onChange={event => setAmount(event.target.value)} placeholder="0,00" /></label>
    {limit && <label>Motivo da revisão (opcional)<input value={note} onChange={event => setNote(event.target.value)} maxLength={300} /></label>}
    <div className="form__actions"><button className="button--primary" type="submit" disabled={busy}>{busy ? 'Salvando…' : 'Salvar limite'}</button><button type="button" onClick={onCancel} disabled={busy}>Cancelar</button></div>
  </form>
}

/** Copia o planejamento do mês anterior (linhas e limites) para a competência mostrada, só após confirmação explícita. Nunca copia lançamentos realizados. */
export function CopyPlanDialog({ workspaceId, competence, onCopied, onCancel }: { workspaceId: string; competence: string; onCopied: (result: CopyResult) => void; onCancel: () => void }) {
  const from = addCalendarMonths(`${competence}-01`, -1).slice(0, 7)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const confirm = async () => {
    setBusy(true); setError(null)
    try { onCopied(await copyMonthlyPlan(workspaceId, from, competence)) } catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }
  return <div className="dialog" role="dialog" aria-labelledby="plan-copy-title">
    <h5 id="plan-copy-title">Copiar planejamento de {formatMonthYear(`${from}-01`)}</h5>
    <p>As linhas e os limites por categoria de {formatMonthYear(`${from}-01`)} serão copiados para {formatMonthYear(`${competence}-01`)}. Lançamentos realizados, previsões, faturas e a meta dos cartões <strong>não</strong> são copiados. Nada é sobrescrito: o mês de destino precisa estar sem linhas.</p>
    {error && <p className="status status--down" role="alert">{error}</p>}
    <div className="form__actions"><button className="button--primary" type="button" onClick={() => void confirm()} disabled={busy}>{busy ? 'Copiando…' : 'Confirmar cópia'}</button><button type="button" onClick={onCancel} disabled={busy}>Cancelar</button></div>
  </div>
}

/** Alterações do planejamento (todas do mês, ou de um item), da mais recente para a mais antiga. */
export function PlanHistoryPanel({ workspaceId, competence, subjectId, title, onClose }: { workspaceId: string; competence: string; subjectId?: string; title: string; onClose: () => void }) {
  const [entries, setEntries] = useState<PlanHistoryEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    getPlanHistory(workspaceId, competence, subjectId, controller.signal)
      .then(data => { if (!controller.signal.aborted) setEntries(data) })
      .catch((failure: unknown) => { if (!controller.signal.aborted) setError(toUserMessage(failure)) })
    return () => controller.abort()
  }, [workspaceId, competence, subjectId])
  return <section className="dialog" role="dialog" aria-labelledby="plan-history-title">
    <h5 id="plan-history-title">{title}</h5>
    {error && <p className="status status--down" role="alert">{error}</p>}
    {!entries && !error && <p role="status">Carregando histórico…</p>}
    {entries?.length === 0 && <p className="meta">Nenhuma alteração registrada neste mês.</p>}
    {entries && entries.length > 0 && <ol className="monthly-plan__history">{entries.map(entry => <li key={entry.id}>
      <strong>{actionLabels[entry.action]}</strong> · {entry.subject === 'Line' ? 'Linha' : 'Limite'} “{entry.label}”
      {entry.previousAmount !== null && entry.newAmount !== null && <> · {money(entry.previousAmount)} → {money(entry.newAmount)}</>}
      {entry.previousAmount === null && entry.newAmount !== null && <> · {money(entry.newAmount)}</>}
      {entry.previousAmount !== null && entry.newAmount === null && <> · era {money(entry.previousAmount)}</>}
      {entry.note && <> · {entry.note}</>}
      <small> · {dateTime(entry.at)}</small>
    </li>)}</ol>}
    <div className="form__actions"><button type="button" onClick={onClose}>Fechar</button></div>
  </section>
}

