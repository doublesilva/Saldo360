import { useEffect, useRef, useState, type FormEvent } from 'react'
import { archiveGoal, createGoal, getGoalAvailability, getGoalSummary, getProgress, listAllocations, listGoals, type FinancialGoal, type GoalAllocation, type GoalAvailability, type GoalProgress, type GoalSummary } from '../api/goals'
import { formatBrlDecimal } from '../money'
import { todayInSaoPaulo } from '../dates'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import { toUserMessage } from '../api/apiErrors'
import { GoalCard } from './goals/GoalCard'
import { GoalDetails } from './goals/GoalDetails'
import { goalTypeLabels } from './goals/goalText'
import '../Goals.css'

/**
 * Objetivos financeiros: metas de ACUMULAÇÃO (quanto reservar até um alvo), não limites de gasto do orçamento. O valor alocado vem das alocações ativas
 * (aportes − retiradas); alocar não cria movimentação bancária nem soma ao saldo.
 */
export function FinancialGoals({ workspaceId, refreshKey = 0, onChanged, onNavigateToPlanning, initialGoalId }: { workspaceId: string; refreshKey?: number; onChanged?: () => void; onNavigateToPlanning?: () => void; initialGoalId?: string }) {
  const [goals, setGoals] = useState<FinancialGoal[] | null>(null)
  const [progresses, setProgresses] = useState<Record<string, GoalProgress | null>>({})
  const [summary, setSummary] = useState<GoalSummary | null>(null)
  const [accounts, setAccounts] = useState<FinancialAccount[]>([])
  const [selected, setSelected] = useState<FinancialGoal | null>(null)
  const [progress, setProgress] = useState<GoalProgress | null>(null)
  const [allocations, setAllocations] = useState<GoalAllocation[]>([])
  const [availability, setAvailability] = useState<GoalAvailability[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const abort = useRef<AbortController | null>(null)
  const actionInFlight = useRef(false)
  const opened = useRef<string | null>(null)

  const open = async (goal: FinancialGoal) => {
    setSelected(goal)
    try {
      const [loadedProgress, loadedAllocations] = await Promise.all([getProgress(workspaceId, goal.id), listAllocations(workspaceId, goal.id)])
      setProgress(loadedProgress); setAllocations(loadedAllocations)
      setProgresses(current => ({ ...current, [goal.id]: loadedProgress }))
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Erro ao carregar detalhes') }
  }

  const load = () => {
    abort.current?.abort()
    const controller = new AbortController(); abort.current = controller
    Promise.all([listGoals(workspaceId, false, controller.signal), getGoalSummary(workspaceId, controller.signal), getGoalAvailability(workspaceId, controller.signal), listAccounts(workspaceId, false, controller.signal)])
      .then(async ([loadedGoals, loadedSummary, loadedAvailability, loadedAccounts]) => {
        setGoals(loadedGoals); setSummary(loadedSummary); setAvailability(loadedAvailability); setAccounts(loadedAccounts)
        // Um objetivo cujo progresso não carregou aparece como "indisponível", nunca como zero.
        const entries = await Promise.all(loadedGoals.map(async goal => [goal.id, await getProgress(workspaceId, goal.id, controller.signal).catch(() => null)] as const))
        if (!controller.signal.aborted) setProgresses(Object.fromEntries(entries))
        if (initialGoalId && opened.current !== initialGoalId) { const target = loadedGoals.find(goal => goal.id === initialGoalId); if (target) { opened.current = initialGoalId; void open(target) } }
      })
      .catch(cause => { if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Erro ao carregar objetivos') })
    return () => controller.abort()
  }
  useEffect(load, [workspaceId, refreshKey])

  const action = async (fn: () => Promise<unknown>) => {
    if (actionInFlight.current) return
    actionInFlight.current = true; setBusy(true); setError(null)
    try { await fn(); onChanged?.(); load(); if (selected) void open(selected) } catch (cause) { setError(toUserMessage(cause)) } finally { actionInFlight.current = false; setBusy(false) }
  }

  const today = todayInSaoPaulo()
  return <section className="family-section goals" aria-labelledby="goals-title">
    <h3 id="goals-title">Objetivos financeiros</h3>
    <p className="meta">Objetivos são metas de acumulação: quanto você já reservou para um alvo. Eles são diferentes dos limites do Orçamento, que controlam quanto gastar. Reservar valor aqui não cria receita, despesa nem transferência.</p>
    {error && <p className="status status--down" role="alert">{error}</p>}
    <GoalSummaryView summary={summary} availability={availability} accounts={accounts} />
    <GoalForm workspaceId={workspaceId} onDone={load} />
    <button type="button" onClick={onNavigateToPlanning}>Ver Planejamento</button>
    {goals === null ? (error ? null : <p className="status status--pending" role="status">Carregando objetivos…</p>)
      : goals.length === 0 ? <p className="meta">Nenhum objetivo cadastrado. Crie o primeiro acima para acompanhar quanto já foi reservado.</p>
        : <ul className="goal-list">{goals.map(goal => <GoalCard key={goal.id} goal={goal} progress={progresses[goal.id] ?? null} today={today} selected={selected?.id === goal.id} onOpen={() => void open(goal)} />)}</ul>}
    {selected && <GoalDetails key={selected.id} workspaceId={workspaceId} goal={goals?.find(goal => goal.id === selected.id) ?? selected} progress={progress} allocations={allocations} accounts={accounts} busy={busy}
      onAction={action} onReload={() => void open(selected)} onArchive={() => void action(() => archiveGoal(workspaceId, selected.id))} />}
  </section>
}

function GoalSummaryView({ summary, availability, accounts }: { summary: GoalSummary | null; availability: GoalAvailability[]; accounts: FinancialAccount[] }) {
  if (!summary) return <p className="status status--pending">Carregando resumo…</p>
  const name = (id: string) => accounts.find(account => account.id === id)?.name ?? 'Conta'
  return <div className="card goal-summary">
    <p>Alocado a objetivos: {formatBrlDecimal(summary.allocatedToGoals)}</p>
    <p>Saldo realizado: {formatBrlDecimal(summary.realizedBalance)}</p>
    <p>Não alocado: {formatBrlDecimal(summary.unallocatedRealizedBalance)}</p>
    <p className="meta">O valor alocado faz parte do saldo realizado: é uma reserva dentro dele, não um valor a mais. Alocado + não alocado = saldo realizado.</p>
    {!summary.coverageComplete && <p className="notice notice--warning" role="status">O saldo de alguma conta não pôde ser calculado por completo; os totais acima podem estar incompletos e não devem ser lidos como zero.</p>}
    <p>Ativos: {summary.activeGoals} · Pausados: {summary.pausedGoals} · Concluídos: {summary.completedGoals}</p>
    <ul>{availability.map(item => <li key={item.financialAccountId}>{name(item.financialAccountId)}: {formatBrlDecimal(item.unallocatedBalance)} não alocado</li>)}</ul>
  </div>
}

function GoalForm({ workspaceId, onDone }: { workspaceId: string; onDone: () => void }) {
  const [name, setName] = useState('')
  const [amount, setAmount] = useState('')
  const [type, setType] = useState('Other')
  const [priority, setPriority] = useState('Medium')
  const [targetDate, setTargetDate] = useState('')
  const [error, setError] = useState<string | null>(null)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    try {
      await createGoal(workspaceId, { name, description: null, goalType: type, targetAmount: amount, targetDate: targetDate || null, priority })
      setName(''); setAmount(''); setTargetDate(''); setError(null); onDone()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Falha ao criar') }
  }
  return <form className="form" onSubmit={submit}>
    <h4>Novo objetivo</h4>
    <label htmlFor="goal-name">Nome</label><input id="goal-name" value={name} onChange={event => setName(event.target.value)} required />
    <label htmlFor="goal-type">Modalidade</label>
    <select id="goal-type" value={type} onChange={event => setType(event.target.value)}>{Object.entries(goalTypeLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>
    <label htmlFor="goal-amount">Valor-alvo</label><input id="goal-amount" value={amount} onChange={event => setAmount(event.target.value)} inputMode="decimal" required />
    <label htmlFor="goal-date">Prazo (opcional)</label><input id="goal-date" type="date" value={targetDate} onChange={event => setTargetDate(event.target.value)} />
    <label htmlFor="goal-priority">Prioridade</label>
    <select id="goal-priority" value={priority} onChange={event => setPriority(event.target.value)}><option value="Low">Baixa</option><option value="Medium">Média</option><option value="High">Alta</option></select>
    {error && <p className="field-error" role="alert">{error}</p>}
    <button type="submit">Criar objetivo</button>
  </form>
}
