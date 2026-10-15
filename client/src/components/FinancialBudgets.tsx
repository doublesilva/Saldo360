import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { listCategories, type FinancialCategory } from '../api/categories'
import {
  addBudgetTarget,
  archiveBudget,
  archiveBudgetTarget,
  createBudget,
  getBudgetExecution,
  listBudgets,
  type FinancialBudget,
  type FinancialBudgetExecution,
  type FinancialBudgetTarget,
  updateBudgetTarget,
} from '../api/budgets'
import { decimalToInput, formatBrlDecimal, parseAmount } from '../money'
import { addCalendarMonths, formatMonthYear, startOfMonth, todayInSaoPaulo } from '../dates'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

function firstError(errors: Record<string, string[]>): string {
  return Object.values(errors).flat()[0] ?? 'Verifique os dados informados.'
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'loaded'; budget: FinancialBudget | null; execution: FinancialBudgetExecution | null }
  | { kind: 'error'; message: string }

export function FinancialBudgetsPage({ familyId }: { familyId: string }) {
  const [competence, setCompetence] = useState(() => startOfMonth(todayInSaoPaulo()).slice(0, 7))
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [reloadKey, setReloadKey] = useState(0)
  const [showBudgetForm, setShowBudgetForm] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([
      listBudgets(familyId, competence, controller.signal),
      listCategories(familyId, true, controller.signal),
    ])
      .then(async ([budgets]) => {
        const budget = budgets.find((item) => !item.isArchived) ?? budgets[0] ?? null
        if (!budget || budget.isArchived) {
          setState({ kind: 'loaded', budget: null, execution: null })
          return
        }
        const execution = await getBudgetExecution(familyId, budget.id, controller.signal)
        setState({ kind: 'loaded', budget, execution })
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, competence, reloadKey])

  const moveMonth = (offset: number) => {
    setState({ kind: 'loading' })
    setCompetence((month) => addCalendarMonths(`${month}-01`, offset).slice(0, 7))
  }
  const refresh = () => {
    setState({ kind: 'loading' })
    setNotice(null)
    setReloadKey((value) => value + 1)
  }
  const selectCurrentMonth = () => {
    setState({ kind: 'loading' })
    setCompetence(startOfMonth(todayInSaoPaulo()).slice(0, 7))
  }

  return (
    <div className="budgets" aria-live="polite">
      <div className="budgets__header">
        <div>
          <h3>Orçamentos</h3>
          <p className="meta">Acompanhe metas e compromissos de despesas por competência.</p>
        </div>
        <div className="month-navigation" aria-label="Navegação de competência">
          <button type="button" aria-label="Competência anterior" onClick={() => moveMonth(-1)}>←</button>
          <strong>{formatMonthYear(`${competence}-01`)}</strong>
          <button type="button" aria-label="Próxima competência" onClick={() => moveMonth(1)}>→</button>
          <button type="button" onClick={selectCurrentMonth}>Mês atual</button>
        </div>
      </div>

      {notice && <p className="notice" role="status">{notice}</p>}
      {state.kind === 'loading' && <p className="status status--pending">Carregando orçamento…</p>}
      {state.kind === 'error' && <p className="status status--down" role="alert">{state.message}</p>}
      {state.kind === 'loaded' && !state.budget && (
        <section className="budget-empty">
          <p>Nenhum orçamento definido para esta competência.</p>
          {showBudgetForm ? (
            <BudgetCreateForm
              familyId={familyId}
              competence={competence}
              onDone={() => { setShowBudgetForm(false); refresh() }}
              onCancel={() => setShowBudgetForm(false)}
            />
          ) : (
            <button type="button" className="button--primary" onClick={() => setShowBudgetForm(true)}>Criar orçamento</button>
          )}
        </section>
      )}
      {state.kind === 'loaded' && state.budget && state.execution && (
        <BudgetContent
          familyId={familyId}
          budget={state.budget}
          execution={state.execution}
          onChanged={refresh}
          onNotice={setNotice}
        />
      )}
    </div>
  )
}

function BudgetCreateForm({ familyId, competence, onDone, onCancel }: { familyId: string; competence: string; onDone: () => void; onCancel: () => void }) {
  const [description, setDescription] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault(); setBusy(true); setError(null)
    try {
      const result = await createBudget(familyId, competence, description)
      if (result.ok) onDone(); else setError(firstError(result.errors))
    } catch (failure: unknown) { setError(errorMessage(failure)) } finally { setBusy(false) }
  }
  return <form className="form" onSubmit={submit} noValidate>
    <label htmlFor="budget-description">Descrição (opcional)</label>
    <input id="budget-description" value={description} onChange={(event) => setDescription(event.target.value)} maxLength={200} />
    {error && <p className="field-error" role="alert">{error}</p>}
    <div className="form__actions"><button className="button--primary" type="submit" disabled={busy}>{busy ? 'Criando…' : 'Criar orçamento'}</button><button type="button" onClick={onCancel}>Cancelar</button></div>
  </form>
}

function BudgetContent({
  familyId, budget, execution, onChanged, onNotice,
}: { familyId: string; budget: FinancialBudget; execution: FinancialBudgetExecution; onChanged: () => void; onNotice: (notice: string | null) => void }) {
  const [categories, setCategories] = useState<FinancialCategory[]>([])
  useEffect(() => {
    const controller = new AbortController()
    listCategories(familyId, true, controller.signal).then(setCategories).catch(() => undefined)
    return () => controller.abort()
  }, [familyId])
  const activeTargets = useMemo(() => budget.targets.filter((target) => !target.isArchived), [budget.targets])
  const availableCategories = categories.filter((category) => category.type === 'Expense' && !budget.targets.some((target) => target.category.id === category.id))
  const archived = budget.isArchived
  return <>
    <div className="budget-heading">
      <div><h4>{budget.description || `Orçamento de ${formatMonthYear(`${budget.competence}-01`)}`}</h4><span className="meta">Competência {budget.competence}</span></div>
      {!archived && <button type="button" onClick={() => {
        if (window.confirm('Arquivar este orçamento? O histórico será preservado e ele não poderá ser recriado automaticamente nesta competência.')) {
          void archiveBudget(familyId, budget.id).then((result) => result.ok ? onChanged() : onNotice(result.message))
        }
      }}>Arquivar orçamento</button>}
    </div>
    <BudgetSummary values={execution.total} />
    <section className="budget-targets" aria-labelledby="budget-targets-title">
      <div className="budgets__section-header"><h4 id="budget-targets-title">Metas por categoria</h4><span className="meta">{activeTargets.length} ativas</span></div>
      {activeTargets.length === 0 && <p className="meta">Nenhuma meta ativa neste orçamento.</p>}
      <div className="budget-target-grid">
        {execution.targets.slice().sort((a, b) => categoryName(a.categoryId, budget, categories).localeCompare(categoryName(b.categoryId, budget, categories), 'pt-BR')).map((item) => {
          const target = budget.targets.find((candidate) => candidate.id === item.targetId)
          if (!target) return null
          return <BudgetTargetCard key={item.targetId} target={target} execution={item} familyId={familyId} budgetId={budget.id} onChanged={onChanged} onNotice={onNotice} />
        })}
      </div>
      {!archived && <BudgetTargetForm familyId={familyId} budgetId={budget.id} categories={availableCategories} onDone={onChanged} onError={onNotice} />}
    </section>
  </>
}

function categoryName(categoryId: string, budget: FinancialBudget, categories: FinancialCategory[]) {
  return budget.targets.find((target) => target.category.id === categoryId)?.category.name ?? categories.find((category) => category.id === categoryId)?.name ?? 'Categoria'
}

function BudgetSummary({ values }: { values: FinancialBudgetExecution['total'] }) {
  return <section className="budget-summary" aria-label="Resumo da execução">
    {(['planned', 'realized', 'committed', 'projectedConsumption', 'available', 'exceededAmount'] as const).map((key) => <div className={key === 'exceededAmount' && values[key] !== '0.00' ? 'budget-summary__item budget-summary__item--danger' : 'budget-summary__item'} key={key}><span>{summaryLabel(key)}</span><strong>{formatBrlDecimal(values[key])}</strong></div>)}
    <div className="budget-summary__item"><span>Consumo</span><strong>{values.consumptionPercentage === null ? 'Sem referência' : `${values.consumptionPercentage}%`}</strong></div>
  </section>
}

function summaryLabel(key: string) {
  return ({ planned: 'Planejado', realized: 'Realizado', committed: 'Comprometido', projectedConsumption: 'Consumo projetado', available: 'Disponível', exceededAmount: 'Excedente' } as Record<string, string>)[key]
}

function BudgetTargetCard({ target, execution, familyId, budgetId, onChanged, onNotice }: { target: FinancialBudgetTarget; execution: FinancialBudgetExecution['targets'][number]; familyId: string; budgetId: string; onChanged: () => void; onNotice: (notice: string | null) => void }) {
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const percentage = execution.consumptionPercentage === null ? null : Number.parseFloat(execution.consumptionPercentage)
  return <article className={execution.exceededAmount !== '0.00' ? 'budget-target budget-target--exceeded' : 'budget-target'}>
    <div className="budget-target__header"><div><h5>{target.category.name} {!target.category.isActive && <span className="meta">(inativa)</span>}</h5><span className="meta">Meta {formatBrlDecimal(execution.planned)}</span></div>{execution.exceededAmount !== '0.00' && <span className="budget-badge">Excedida</span>}</div>
    <div className="budget-target__values"><span>Realizado <strong>{formatBrlDecimal(execution.realized)}</strong></span><span>Comprometido <strong>{formatBrlDecimal(execution.committed)}</strong></span><span>Projetado <strong>{formatBrlDecimal(execution.projectedConsumption)}</strong></span><span>Disponível <strong>{formatBrlDecimal(execution.available)}</strong></span></div>
    <div className="budget-progress" aria-label={percentage === null ? 'Consumo sem referência percentual' : `Consumo de ${execution.consumptionPercentage}%`}><div className="budget-progress__bar" style={{ width: percentage === null ? '0%' : `${Math.min(100, Math.max(0, percentage))}%` }} /><span>{execution.consumptionPercentage === null ? 'Sem referência' : `${execution.consumptionPercentage}% consumido`}</span></div>
    {editing ? <BudgetTargetEditForm familyId={familyId} budgetId={budgetId} target={target} onDone={() => { setEditing(false); onChanged() }} onCancel={() => setEditing(false)} onError={onNotice} /> : <div className="budget-target__actions"><button type="button" onClick={() => setEditing(true)}>Editar</button><button type="button" disabled={busy} onClick={() => { if (window.confirm(`Arquivar a meta de ${target.category.name} (${formatBrlDecimal(target.plannedAmount)})? Isso retira a meta dos totais, sem cancelar despesas.`)) { setBusy(true); void archiveBudgetTarget(familyId, budgetId, target.id).then((result) => result.ok ? onChanged() : onNotice(result.message)).finally(() => setBusy(false)) } }}>Arquivar meta</button></div>}
  </article>
}

function BudgetTargetForm({ familyId, budgetId, categories, onDone, onError }: { familyId: string; budgetId: string; categories: FinancialCategory[]; onDone: () => void; onError: (message: string) => void }) {
  const [categoryId, setCategoryId] = useState(''); const [amount, setAmount] = useState(''); const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => { event.preventDefault(); const parsed = parseAmount(amount, 'O valor planejado'); if (!categoryId) return onError('Selecione uma categoria de despesa.'); if (parsed.kind !== 'ok') return onError(parsed.kind === 'invalid' ? parsed.message : 'Informe o valor planejado.'); setBusy(true); const category = categories.find((item) => item.id === categoryId); if (!category) return setBusy(false); const result = await addBudgetTarget(familyId, budgetId, categoryId, parsed.text, category.name); if (result.ok) { setAmount(''); setCategoryId(''); onDone() } else onError(firstError(result.errors)); setBusy(false) }
  return <form className="budget-target-form" onSubmit={submit} noValidate><h5>Adicionar meta</h5><label htmlFor="budget-target-category">Categoria de despesa</label><select id="budget-target-category" value={categoryId} onChange={(event) => setCategoryId(event.target.value)}><option value="">Selecione…</option>{categories.map((category) => <option key={category.id} value={category.id}>{category.name}{!category.isActive ? ' (inativa)' : ''}</option>)}</select><label htmlFor="budget-target-amount">Valor planejado</label><input id="budget-target-amount" inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} placeholder="0,00" /><button type="submit" className="button--primary" disabled={busy || categories.length === 0}>{busy ? 'Adicionando…' : 'Adicionar meta'}</button>{categories.length === 0 && <p className="meta">Não há categorias de despesa elegíveis.</p>}</form>
}

function BudgetTargetEditForm({ familyId, budgetId, target, onDone, onCancel, onError }: { familyId: string; budgetId: string; target: FinancialBudgetTarget; onDone: () => void; onCancel: () => void; onError: (message: string) => void }) {
  const [amount, setAmount] = useState(() => decimalToInput(target.plannedAmount)); const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => { event.preventDefault(); const parsed = parseAmount(amount, 'O valor planejado'); if (parsed.kind !== 'ok') return onError(parsed.kind === 'invalid' ? parsed.message : 'Informe o valor planejado.'); setBusy(true); const result = await updateBudgetTarget(familyId, budgetId, target.id, target.name, parsed.text); if (result.ok) onDone(); else onError(firstError(result.errors)); setBusy(false) }
  return <form className="budget-target-edit" onSubmit={submit}><label htmlFor={`target-${target.id}`}>Valor planejado</label><input id={`target-${target.id}`} inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} /><button type="submit" className="button--primary" disabled={busy}>Salvar</button><button type="button" onClick={onCancel} disabled={busy}>Cancelar</button></form>
}
