import { useEffect, useMemo, useState } from 'react'
import { listCategories, type FinancialCategory } from '../api/categories'
import {
  archiveBudget,
  getBudgetExecution,
  listBudgets,
  type BudgetGroupingType,
  type FinancialBudget,
  type FinancialBudgetAlertStatus,
  type FinancialBudgetExecution,
  type FinancialBudgetTarget,
  type FinancialBudgetTargetExecution,
} from '../api/budgets'
import { addCalendarMonths, formatMonthYear, startOfMonth, todayInSaoPaulo } from '../dates'
import { formatBrlDecimal } from '../money'
import { BudgetTargetCard } from './budget/BudgetTargetCard'
import { BudgetTargetForm } from './budget/BudgetTargetForm'
import { groupHints, groupTitles, groupingOfGroup, type BudgetGroup } from './budget/budgetText'
import '../Budget.css'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'loaded'; budget: FinancialBudget | null; execution: FinancialBudgetExecution | null }
  | { kind: 'error'; message: string }

const severity = (status: FinancialBudgetAlertStatus) => ({ Exceeded: 0, LimitReached: 1, ApproachingLimit: 2, WithinBudget: 3 })[status]

function lastDay(competence: string): string {
  const [year, month] = competence.split('-').map(Number)
  return `${competence}-${String(new Date(Date.UTC(year, month, 0)).getUTCDate()).padStart(2, '0')}`
}

/**
 * Orçamento do mês (R03): três tipos de meta (despesas fixas, todos os cartões e categorias), cada uma respondendo "qual é o limite, quanto foi
 * consumido e quanto está disponível". As perspectivas se sobrepõem de propósito (uma assinatura fixa no cartão conta nas três) e por isso nada aqui é
 * somado entre elas. Todos os valores e totais vêm do servidor.
 */
export function FinancialBudgetsPage({ workspaceId, initialCompetence, initialGroup, refreshKey: externalRefreshKey = 0, onNavigateToAgenda, onReviewClassification }: {
  workspaceId: string; initialCompetence?: string; initialGroup?: BudgetGroup; refreshKey?: number
  onNavigateToAgenda?: (competence: string) => void
  /** Leva à lista de lançamentos filtrada nas despesas sem tipo definido do período (o fluxo existente de classificação). */
  onReviewClassification?: (range: { from: string; to: string }) => void
}) {
  const [competence, setCompetence] = useState(() => initialCompetence ?? startOfMonth(todayInSaoPaulo()).slice(0, 7))
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [reloadKey, setReloadKey] = useState(0)
  const [form, setForm] = useState<{ preset?: BudgetGroupingType } | null>(null)
  const [openTarget, setOpenTarget] = useState<string | null>(null)
  const [categories, setCategories] = useState<FinancialCategory[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const [groupApplied, setGroupApplied] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    listCategories(workspaceId, true, controller.signal).then(setCategories).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId])

  useEffect(() => {
    const controller = new AbortController()
    listBudgets(workspaceId, competence, controller.signal)
      .then(async (budgets) => {
        const budget = budgets.find((item) => !item.isArchived) ?? null
        if (!budget) {
          setState({ kind: 'loaded', budget: null, execution: null })
          return
        }
        const execution = await getBudgetExecution(workspaceId, budget.id, controller.signal)
        setState({ kind: 'loaded', budget, execution })
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, competence, reloadKey, externalRefreshKey])

  const moveMonth = (offset: number) => {
    setState({ kind: 'loading' })
    setOpenTarget(null)
    setCompetence((month) => addCalendarMonths(`${month}-01`, offset).slice(0, 7))
  }
  const refresh = () => {
    setState({ kind: 'loading' })
    setNotice(null)
    setForm(null)
    setReloadKey((value) => value + 1)
  }
  const selectCurrentMonth = () => {
    setState({ kind: 'loading' })
    setOpenTarget(null)
    setCompetence(startOfMonth(todayInSaoPaulo()).slice(0, 7))
  }

  const budget = state.kind === 'loaded' ? state.budget : null
  const execution = state.kind === 'loaded' ? state.execution : null
  const active = useMemo(() => (budget?.targets ?? []).filter((target) => !target.isArchived), [budget])
  const byGroup = (grouping: BudgetGroupingType) => active.filter((target) => target.groupingType === grouping)
  const runOf = (target: FinancialBudgetTarget) => execution?.targets.find((item) => item.targetId === target.id) ?? null

  // Vindo da Visão Geral: abre a composição da meta do grupo pedido (fixas ou cartões) uma única vez.
  useEffect(() => {
    if (groupApplied || !initialGroup || !execution) return
    setGroupApplied(true)
    const first = (budget?.targets ?? []).find((target) => !target.isArchived && target.groupingType === groupingOfGroup[initialGroup])
    if (first && initialGroup !== 'categories') setOpenTarget(first.id)
  }, [groupApplied, initialGroup, execution, budget])

  const review = onReviewClassification ? () => onReviewClassification({ from: `${competence}-01`, to: lastDay(competence) }) : undefined

  return (
    <div className="budgets" aria-live="polite">
      <div className="budgets__header">
        <div>
          <h3>Orçamento</h3>
          <p className="meta">Para cada meta: qual é o limite, quanto foi consumido e quanto está disponível.</p>
        </div>
        <div className="month-navigation" aria-label="Navegação de competência">
          <button type="button" aria-label="Competência anterior" onClick={() => moveMonth(-1)}>←</button>
          <strong>{formatMonthYear(`${competence}-01`)}</strong>
          <button type="button" aria-label="Próxima competência" onClick={() => moveMonth(1)}>→</button>
          <button type="button" onClick={selectCurrentMonth}>Mês atual</button>
        </div>
        <div className="budgets__actions">
          <button type="button" className="button--primary" onClick={() => setForm({})}>Nova meta</button>
          {onNavigateToAgenda && <button type="button" className="button--link" onClick={() => onNavigateToAgenda(competence)}>Ver na Agenda</button>}
        </div>
      </div>

      {notice && <p className="notice" role="status">{notice}</p>}
      {state.kind === 'loading' && <p className="status status--pending">Carregando orçamento…</p>}
      {state.kind === 'error' && <p className="status status--down" role="alert">{state.message}</p>}
      {state.kind === 'loaded' && form && (
        <BudgetTargetForm workspaceId={workspaceId} competence={competence} budget={budget} categories={categories} preset={form.preset} onDone={refresh} onCancel={() => setForm(null)} />
      )}
      {state.kind === 'loaded' && active.length === 0 && !form && (
        <section className="budget-empty" aria-label="Sem metas">
          <p>Nenhuma meta definida para {formatMonthYear(`${competence}-01`)}.</p>
          <p className="meta">Defina um limite para as despesas fixas, para todos os cartões somados ou para uma categoria.</p>
          <button type="button" className="button--primary" onClick={() => setForm({})}>Nova meta</button>
        </section>
      )}
      {state.kind === 'loaded' && budget && execution && active.length > 0 && <>
        {(['FixedExpenses', 'AllCards'] as const).map((grouping) => {
          const target = byGroup(grouping)[0]
          const run = target ? runOf(target) : null
          return <section key={grouping} className="budget-group" aria-labelledby={`budget-group-${grouping}`}>
            <div className="budgets__section-header">
              <h4 id={`budget-group-${grouping}`}>{groupTitles[grouping]}</h4>
              {grouping === 'AllCards' && <span className="meta">Faturas com vencimento em {formatMonthYear(`${competence}-01`)}</span>}
            </div>
            {target && run
              ? <BudgetTargetCard workspaceId={workspaceId} budgetId={budget.id} target={target} run={run} title={groupTitles[grouping]} canEdit open={openTarget === target.id} version={reloadKey + externalRefreshKey}
                onToggle={() => setOpenTarget(openTarget === target.id ? null : target.id)} onChanged={refresh} onNotice={setNotice} onReviewClassification={grouping === 'FixedExpenses' ? review : undefined} />
              : <div className="budget-group__empty"><p className="meta">Sem meta de {groupTitles[grouping].toLowerCase()} neste mês. {groupHints[grouping]}</p>
                <button type="button" onClick={() => setForm({ preset: grouping })}>Definir limite</button></div>}
          </section>
        })}
        <CategoryGroup workspaceId={workspaceId} budget={budget} execution={execution} targets={byGroup('Category')} runOf={runOf} openTarget={openTarget} version={reloadKey + externalRefreshKey}
          onToggle={(id) => setOpenTarget(openTarget === id ? null : id)} onChanged={refresh} onNotice={setNotice} onAdd={() => setForm({ preset: 'Category' })} />
        <p className="meta budget-overlap-note">Despesas fixas, cartões e categorias olham os mesmos gastos por ângulos diferentes: uma assinatura fixa paga no cartão aparece nas três, mas é uma única despesa. Por isso estes valores não são somados entre si.</p>
        <div className="budget-footer"><button type="button" onClick={() => {
          if (window.confirm('Arquivar este orçamento? O histórico será preservado e ele não poderá ser recriado automaticamente nesta competência.')) {
            void archiveBudget(workspaceId, budget.id).then((result) => result.ok ? refresh() : setNotice(result.message))
          }
        }}>Arquivar orçamento</button></div>
      </>}
    </div>
  )
}

function CategoryGroup({ workspaceId, budget, execution, targets, runOf, openTarget, version, onToggle, onChanged, onNotice, onAdd }: {
  workspaceId: string; budget: FinancialBudget; execution: FinancialBudgetExecution; targets: FinancialBudgetTarget[]
  runOf: (target: FinancialBudgetTarget) => FinancialBudgetTargetExecution | null; openTarget: string | null; version: number
  onToggle: (id: string) => void; onChanged: () => void; onNotice: (notice: string | null) => void; onAdd: () => void
}) {
  const ordered = targets.map((target) => ({ target, run: runOf(target) })).filter((item): item is { target: FinancialBudgetTarget; run: FinancialBudgetTargetExecution } => item.run !== null)
    .sort((a, b) => severity(a.run.status) - severity(b.run.status) || (a.target.category?.path ?? a.target.name).localeCompare(b.target.category?.path ?? b.target.name, 'pt-BR'))
  const summary = execution.categories
  return <section className="budget-group" aria-labelledby="budget-group-Category">
    <div className="budgets__section-header"><h4 id="budget-group-Category">{groupTitles.Category}</h4><span className="meta">{targets.length} meta(s) ativa(s)</span></div>
    {targets.length === 0
      ? <div className="budget-group__empty"><p className="meta">Sem metas por categoria neste mês. {groupHints.Category}</p><button type="button" onClick={onAdd}>Definir limite</button></div>
      : <>
        <p className="budget-group__summary meta">
          Metas por categoria somadas (não se sobrepõem): limite {formatBrlDecimal(summary.planned)} · consumido {formatBrlDecimal(summary.consumed)}
          {summary.exceededCount > 0 ? ` · ${summary.exceededCount} excedida(s)` : ''}{summary.approachingCount + summary.reachedCount > 0 ? ` · ${summary.approachingCount + summary.reachedCount} perto do limite ou no limite` : ''}
        </p>
        <div className="budget-target-grid">
          {ordered.map(({ target, run }) => <BudgetTargetCard key={target.id} workspaceId={workspaceId} budgetId={budget.id} target={target} run={run}
            title={target.category?.path ?? target.name} canEdit open={openTarget === target.id} version={version} onToggle={() => onToggle(target.id)} onChanged={onChanged} onNotice={onNotice} />)}
        </div>
        <button type="button" onClick={onAdd}>Adicionar meta de categoria</button>
      </>}
  </section>
}
