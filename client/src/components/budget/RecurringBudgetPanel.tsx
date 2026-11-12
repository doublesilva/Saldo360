import { useEffect, useState } from 'react'
import { toUserMessage } from '../../api/apiErrors'
import type { FinancialCategory } from '../../api/categories'
import {
  applyBudgetReference, changeBudgetRule, getBudgetRuleHistory, getEffectiveBudget, getEffectiveExecution, getReferencePreview,
  type BudgetChangeAction, type BudgetChangeMode, type BudgetRuleHistoryItem, type BudgetRuleTarget, type EffectiveBudget, type EffectiveExecution, type EffectiveTarget, type ReferencePreview,
} from '../../api/effectiveBudget'
import { addCalendarMonths, formatMonthYear } from '../../dates'
import { decimalToInput, formatBrlDecimal, parseAmount } from '../../money'
import { isPositiveAmount } from '../overview/overviewText'
import { actionText, monthShort, originText, referenceWarning, scopeText, targetHint, targetLabel, warningText } from './recurringBudgetText'

type Load = { state: 'loading' } | { state: 'error'; error: unknown } | { state: 'loaded'; budget: EffectiveBudget; execution: EffectiveExecution | null }
interface Editing { target: BudgetRuleTarget; categoryId: string | null; name: string; amount: string; mode: BudgetChangeMode }

const FIXED: BudgetRuleTarget[] = ['TotalExpenses', 'AllCards', 'FixedExpenses']

/**
 * T12.1: orçamento mensal padrão. Mostra os limites EFETIVOS da competência (resolvidos no servidor) e a origem de cada um — herdado do padrão, personalizado neste mês, registro
 * mensal anterior ou sem meta —, sem exigir que se abra um formulário para descobrir. Editar oferece duas escolhas explícitas: somente este mês (exceção) ou este mês e os próximos
 * (nova versão do padrão). Ler nunca cria um orçamento mensal; nada é recalculado no navegador.
 */
export function RecurringBudgetPanel({ workspaceId, competence, categories, refreshKey = 0, onChanged }: {
  workspaceId: string; competence: string; categories: FinancialCategory[]; refreshKey?: number; onChanged?: () => void
}) {
  const [load, setLoad] = useState<Load>({ state: 'loading' })
  const [reload, setReload] = useState(0)
  const [editing, setEditing] = useState<Editing | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [newCategory, setNewCategory] = useState('')
  const [history, setHistory] = useState<BudgetRuleHistoryItem[] | null>(null)
  const [reference, setReference] = useState<{ source: string; preview: ReferencePreview | null; chosen: Set<string> } | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([getEffectiveBudget(workspaceId, competence, controller.signal), getEffectiveExecution(workspaceId, competence, controller.signal).catch(() => null)])
      .then(([budget, execution]) => { if (!controller.signal.aborted) setLoad({ state: 'loaded', budget, execution }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoad({ state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, competence, reload, refreshKey])

  const refresh = () => { setLoad({ state: 'loading' }); setHistory(null); setReload(value => value + 1); onChanged?.() }
  const keyOf = (target: BudgetRuleTarget, categoryId: string | null) => `${target}:${categoryId ?? ''}`

  const submit = async (change: { target: BudgetRuleTarget; categoryId: string | null; mode: BudgetChangeMode; action: BudgetChangeAction; amount?: string }, done: string) => {
    setSaving(true); setMessage(null)
    try {
      await changeBudgetRule(workspaceId, { competence, ...change })
      setEditing(null); setNewCategory(''); setMessage(done); refresh()
    } catch (error) { setMessage(toUserMessage(error)) } finally { setSaving(false) }
  }

  const save = () => {
    if (!editing) return
    const parsed = parseAmount(editing.amount, 'A meta')
    if (parsed.kind === 'empty') return setMessage('Informe o valor da meta.')
    if (parsed.kind === 'invalid') return setMessage(parsed.message)
    void submit({ target: editing.target, categoryId: editing.categoryId, mode: editing.mode, action: 'Set', amount: parsed.text },
      editing.mode === 'ThisMonth' ? 'Meta personalizada somente neste mês.' : 'Meta salva para este mês e os próximos.')
  }

  const openReference = () => {
    const source = addCalendarMonths(`${competence}-01`, -1).slice(0, 7)
    setReference({ source, preview: null, chosen: new Set() }); setMessage(null)
    void previewReference(source)
  }
  const previewReference = async (source: string) => {
    try {
      const preview = await getReferencePreview(workspaceId, source, competence)
      setReference({ source, preview, chosen: new Set(preview.items.map(item => keyOf(item.target, item.categoryId))) })
    } catch (error) { setReference({ source, preview: null, chosen: new Set() }); setMessage(toUserMessage(error)) }
  }
  const applyReference = async () => {
    if (!reference?.preview) return
    setSaving(true); setMessage(null)
    try {
      await applyBudgetReference(workspaceId, reference.source, competence, reference.preview.items.filter(item => reference.chosen.has(keyOf(item.target, item.categoryId))).map(item => ({ target: item.target, categoryId: item.categoryId })))
      setReference(null); setMessage(`Padrão criado a partir de ${monthShort(reference.source)}, com início em ${monthShort(competence)}.`); refresh()
    } catch (error) { setMessage(toUserMessage(error)) } finally { setSaving(false) }
  }
  const showHistory = async () => {
    try { setHistory(await getBudgetRuleHistory(workspaceId)) } catch (error) { setMessage(toUserMessage(error)) }
  }

  const budget = load.state === 'loaded' ? load.budget : null
  const runOf = (target: EffectiveTarget) => (load.state === 'loaded' ? load.execution?.targets.find(item => item.targetId === target.id) : null) ?? null
  const rows = budget ? [...FIXED.map(kind => budget.targets.find(t => t.target === kind)).filter((t): t is EffectiveTarget => t !== undefined), ...budget.targets.filter(t => t.target === 'Category')] : []
  const usedCategories = new Set((budget?.targets ?? []).filter(t => t.categoryId !== null).map(t => t.categoryId))
  const addable = categories.filter(category => category.type === 'Expense' && category.isActive && !usedCategories.has(category.id))

  return <section className="rbp" aria-labelledby="rbp-title">
    <header className="rbp__head">
      <div><p className="rbp__title" id="rbp-title">Orçamento padrão · {formatMonthYear(`${competence}-01`)}</p>
        <p className="meta">Configure uma vez: os meses seguintes herdam as metas. Ajuste só um mês quando precisar.</p></div>
      <div className="rbp__actions">
        <button type="button" onClick={openReference}>Usar orçamento existente como referência</button>
        <button type="button" className="button--link" onClick={() => void showHistory()}>Histórico de alterações</button>
      </div>
    </header>

    {message && <p className="notice" role="status">{message}</p>}
    {load.state === 'loading' && <p className="status status--pending">Carregando orçamento padrão…</p>}
    {load.state === 'error' && <p className="status status--down" role="status">{toUserMessage(load.error)}</p>}
    {budget && budget.warnings.map(code => warningText[code] && <p key={code} className="meta rbp__warning">{warningText[code]}</p>)}

    {budget && <ul className="rbp__list">
      {rows.map(target => {
        const run = runOf(target)
        const label = target.target === 'Category' ? target.name : targetLabel[target.target]
        const defined = target.amount !== null
        const isEditing = editing?.target === target.target && editing.categoryId === target.categoryId
        return <li key={keyOf(target.target, target.categoryId)} className={`rbp__row rbp__row--${target.origin}`}>
          <div className="rbp__main">
            <strong>{label}</strong>
            <span className="rbp__amount" data-unavailable={!defined || undefined}>{defined ? formatBrlDecimal(target.amount!) : target.removed ? 'Sem meta neste mês' : 'Sem meta configurada'}</span>
            <span className={`ov-tag${target.origin === 'Inherited' ? ' ov-tag--ok' : target.origin === 'Customized' ? ' ov-tag--warning' : ''}`}>{originText(target)}</span>
          </div>
          {targetHint[target.target] && <p className="meta">{targetHint[target.target]}</p>}
          {run && defined && <p className="meta">Consumido {formatBrlDecimal(run.consumed)} · {isPositiveAmount(run.exceededAmount) ? `Excedido em ${formatBrlDecimal(run.exceededAmount)}` : `Disponível ${formatBrlDecimal(run.available)}`}</p>}
          <div className="rbp__buttons">
            <button type="button" aria-label={`${defined ? 'Editar' : 'Definir'} ${label}`} onClick={() => { setMessage(null); setEditing({ target: target.target, categoryId: target.categoryId, name: label, amount: defined ? decimalToInput(target.amount!) : '', mode: 'ThisMonth' }) }}>{defined ? 'Editar' : 'Definir'}</button>
            {defined && <button type="button" aria-label={`Suspender ${label} neste mês`} disabled={saving} onClick={() => void submit({ target: target.target, categoryId: target.categoryId, mode: 'ThisMonth', action: 'Remove' }, `${label} suspensa neste mês.`)}>Suspender neste mês</button>}
            {defined && target.origin === 'Inherited' && <button type="button" className="button--link" aria-label={`Retirar ${label} a partir deste mês`} disabled={saving} onClick={() => void submit({ target: target.target, categoryId: target.categoryId, mode: 'ThisAndNext', action: 'Remove' }, `${label} retirada a partir de ${monthShort(competence)}.`)}>Retirar daqui em diante</button>}
            {target.origin === 'Customized' && <button type="button" aria-label={`Restaurar o padrão de ${label}`} disabled={saving} onClick={() => void submit({ target: target.target, categoryId: target.categoryId, mode: 'ThisMonth', action: 'Restore' }, `${label} voltou ao padrão.`)}>Restaurar padrão (só este mês)</button>}
          </div>
          {isEditing && editing && <Editor editing={editing} saving={saving} competence={competence} onChange={setEditing} onSave={save} onCancel={() => setEditing(null)} />}
        </li>
      })}
      {budget.outsideCardsTarget !== null && <li className="rbp__row rbp__row--derived">
        <div className="rbp__main"><strong>{targetLabel.OutsideCards}</strong><span className="rbp__amount">{formatBrlDecimal(budget.outsideCardsTarget)}</span><span className="ov-tag">Calculado</span></div>
        <p className="meta">{targetHint.OutsideCards}</p></li>}
    </ul>}

    {budget && addable.length > 0 && <div className="rbp__add">
      <label>Meta de categoria<select value={newCategory} onChange={event => { setNewCategory(event.target.value); const category = categories.find(item => item.id === event.target.value); setEditing(category ? { target: 'Category', categoryId: category.id, name: category.name, amount: '', mode: 'ThisMonth' } : null) }}>
        <option value="">Adicionar meta de categoria…</option>
        {addable.map(category => <option key={category.id} value={category.id}>{category.name}</option>)}</select></label>
      {editing?.target === 'Category' && !usedCategories.has(editing.categoryId) && <Editor editing={editing} saving={saving} competence={competence} onChange={setEditing} onSave={save} onCancel={() => { setEditing(null); setNewCategory('') }} />}
    </div>}

    {reference && <div className="rbp__reference" role="group" aria-label="Usar orçamento existente como referência">
      <p className="rbp__title">Usar um mês existente como referência</p>
      <label>Mês de referência<input type="month" value={reference.source} onChange={event => { if (/^\d{4}-\d{2}$/.test(event.target.value)) void previewReference(event.target.value) }} /></label>
      {reference.preview && <>
        {reference.preview.items.length === 0 ? <p className="meta">Esse mês não tem metas definidas.</p>
          : <ul className="rbp__list">{reference.preview.items.map(item => {
            const key = keyOf(item.target, item.categoryId)
            return <li key={key}><label><input type="checkbox" checked={reference.chosen.has(key)} onChange={event => { const next = new Set(reference.chosen); if (event.target.checked) next.add(key); else next.delete(key); setReference({ ...reference, chosen: next }) }} />
              {' '}{item.target === 'Category' ? item.name : targetLabel[item.target]} · {formatBrlDecimal(item.amount)}</label></li>
          })}</ul>}
        {reference.preview.warnings.map(code => referenceWarning[code] && <p key={code} className="meta rbp__warning">{referenceWarning[code]}</p>)}
        <p className="meta">As metas escolhidas viram o padrão a partir de {monthShort(competence)}. O mês de referência não muda.</p>
        <div className="rbp__buttons">
          <button type="button" className="button--primary" disabled={saving || reference.chosen.size === 0} onClick={() => void applyReference()}>Aplicar como padrão a partir de {monthShort(competence)}</button>
          <button type="button" onClick={() => setReference(null)}>Cancelar</button>
        </div></>}
    </div>}

    {history && <details open className="rbp__history"><summary>Histórico de alterações ({history.length})</summary>
      {history.length === 0 ? <p className="meta">Nenhuma alteração registrada.</p> : <ul>{history.map((item, index) => <li key={`${item.at}-${index}`}>
        {monthShort(item.competence)} · {item.target === 'Category' ? item.categoryName ?? 'Categoria' : targetLabel[item.target]} · {scopeText[item.scope]} · {actionText[item.action]}{item.amount ? ` ${formatBrlDecimal(item.amount)}` : ''} · revisão {item.revision}</li>)}</ul>}
    </details>}
  </section>
}

function Editor({ editing, saving, competence, onChange, onSave, onCancel }: { editing: Editing; saving: boolean; competence: string; onChange: (value: Editing) => void; onSave: () => void; onCancel: () => void }) {
  return <div className="rbp__editor" role="group" aria-label={`Editar ${editing.name}`}>
    <label>Valor da meta<input inputMode="decimal" value={editing.amount} onChange={event => onChange({ ...editing, amount: event.target.value })} placeholder="0,00" /></label>
    <fieldset><legend>Vale para</legend>
      <label><input type="radio" name={`mode-${editing.target}-${editing.categoryId}`} checked={editing.mode === 'ThisMonth'} onChange={() => onChange({ ...editing, mode: 'ThisMonth' })} /> Somente este mês ({monthShort(competence)})</label>
      <label><input type="radio" name={`mode-${editing.target}-${editing.categoryId}`} checked={editing.mode === 'ThisAndNext'} onChange={() => onChange({ ...editing, mode: 'ThisAndNext' })} /> Este mês e os próximos (novo padrão)</label></fieldset>
    <div className="rbp__buttons">
      <button type="button" className="button--primary" disabled={saving} onClick={onSave}>Salvar</button>
      <button type="button" onClick={onCancel}>Cancelar</button>
    </div>
  </div>
}
