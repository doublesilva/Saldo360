import { useState, type FormEvent } from 'react'
import { addBudgetTarget, createBudget, type BudgetGroupingType, type FinancialBudget } from '../../api/budgets'
import type { FinancialCategory } from '../../api/categories'
import { categoryPath, selectableCategories } from '../../categoryTree'
import { formatMonthYear } from '../../dates'
import { parseAmount } from '../../money'
import { CategoryOptions } from '../CategoryOptions'
import { groupTitles } from './budgetText'

function firstError(errors: Record<string, string[]>): string {
  return Object.values(errors).flat()[0] ?? 'Verifique os dados informados.'
}

/**
 * Nova meta do mês: tipo (despesas fixas, todos os cartões ou categoria), categoria (só no tipo categoria), limite e a competência, identificada. Se o mês
 * ainda não tem orçamento, ele é criado junto, sem passo extra. Não existe escolha de cartão individual.
 */
export function BudgetTargetForm({ workspaceId, competence, budget, categories, preset, onDone, onCancel }: {
  workspaceId: string; competence: string; budget: FinancialBudget | null; categories: FinancialCategory[]; preset?: BudgetGroupingType
  onDone: () => void; onCancel: () => void
}) {
  const active = (budget?.targets ?? []).filter((target) => !target.isArchived)
  const taken = (type: BudgetGroupingType) => active.some((target) => target.groupingType === type)
  const firstFree = (['FixedExpenses', 'AllCards', 'Category'] as BudgetGroupingType[]).find((type) => type === 'Category' || !taken(type)) ?? 'Category'
  const [grouping, setGrouping] = useState<BudgetGroupingType>(preset && (preset === 'Category' || !taken(preset)) ? preset : firstFree)
  const [categoryId, setCategoryId] = useState('')
  const [amount, setAmount] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const used = new Set(active.map((target) => target.category?.id))
  const eligible = selectableCategories(categories, 'Expense').filter((category) => !used.has(category.id))

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const parsed = parseAmount(amount, 'O limite')
    if (grouping === 'Category' && !categoryId) return setError('Escolha a categoria da meta.')
    if (parsed.kind !== 'ok') return setError(parsed.kind === 'invalid' ? parsed.message : 'Informe o valor do limite.')
    setBusy(true)
    setError(null)
    try {
      let budgetId = budget?.id
      if (!budgetId) {
        const created = await createBudget(workspaceId, competence)
        if (!created.ok) return setError(firstError(created.errors))
        budgetId = created.budget.id
      }
      const category = categories.find((item) => item.id === categoryId)
      const result = await addBudgetTarget(workspaceId, budgetId, grouping === 'Category'
        ? { groupingType: 'Category', categoryId, name: category ? categoryPath(category) : 'Categoria', plannedAmount: parsed.text }
        : { groupingType: grouping, plannedAmount: parsed.text })
      if (result.ok) onDone(); else setError(firstError(result.errors))
    } catch (failure: unknown) { setError(failure instanceof Error ? failure.message : 'Erro desconhecido.') } finally { setBusy(false) }
  }

  return <form className="budget-target-form" onSubmit={submit} noValidate aria-label="Nova meta">
    <h5>Nova meta</h5>
    <p className="meta">Competência: <strong>{formatMonthYear(`${competence}-01`)}</strong></p>
    <label htmlFor="budget-target-type">Tipo de meta</label>
    <select id="budget-target-type" value={grouping} onChange={(event) => { setGrouping(event.target.value as BudgetGroupingType); setError(null) }}>
      <option value="FixedExpenses" disabled={taken('FixedExpenses')}>{groupTitles.FixedExpenses}{taken('FixedExpenses') ? ' (já definida)' : ''}</option>
      <option value="AllCards" disabled={taken('AllCards')}>{groupTitles.AllCards}{taken('AllCards') ? ' (já definida)' : ''}</option>
      <option value="Category">Categoria</option>
    </select>
    {grouping === 'Category' && <>
      <label htmlFor="budget-target-category">Categoria de despesa</label>
      <select id="budget-target-category" value={categoryId} onChange={(event) => setCategoryId(event.target.value)}>
        <option value="">Selecione…</option>
        <CategoryOptions categories={eligible} type="Expense" />
      </select>
      {eligible.length === 0 && <p className="meta">Não há categorias de despesa elegíveis.</p>}
    </>}
    <label htmlFor="budget-target-amount">Valor do limite</label>
    <input id="budget-target-amount" inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} placeholder="0,00" autoComplete="off" />
    {error && <p className="field-error" role="alert">{error}</p>}
    <div className="form__actions">
      <button type="submit" className="button--primary" disabled={busy}>{busy ? 'Adicionando…' : 'Adicionar meta'}</button>
      <button type="button" onClick={onCancel} disabled={busy}>Cancelar</button>
    </div>
  </form>
}
