import { useRef, useState, type FormEvent } from 'react'
import { cancelAllocation, changeGoal, createContribution, createWithdrawal, listAllocations, type FinancialGoal, type GoalAllocation, type GoalProgress } from '../../api/goals'
import type { FinancialAccount } from '../../api/accounts'
import { normalizeRequestError } from '../../api/apiErrors'
import { formatBrlDecimal, formatDate } from '../../money'
import { todayInSaoPaulo } from '../../dates'
import { GoalProgressBar } from './GoalCard'
import { deadlineInfo, goalStatusLabels, goalTypeLabels } from './goalText'

const newOperation = () => ({ id: crypto.randomUUID(), fingerprint: '' })

/**
 * Detalhe de um objetivo: progresso, ações do ciclo de vida, registro de aporte/retirada e a lista das alocações que EXPLICAM o progresso. Alocar não cria
 * receita, despesa nem transferência e não mexe no saldo bancário: só reserva parte do saldo de uma conta para este objetivo.
 */
export function GoalDetails({ workspaceId, goal, progress, allocations, accounts, busy, onAction, onReload, onArchive }: {
  workspaceId: string; goal: FinancialGoal; progress: GoalProgress | null; allocations: GoalAllocation[]; accounts: FinancialAccount[]; busy: boolean
  onAction: (action: () => Promise<unknown>) => void; onReload: () => void; onArchive: () => void
}) {
  const [account, setAccount] = useState(accounts[0]?.id ?? '')
  const [amount, setAmount] = useState('')
  const [mode, setMode] = useState<'Contribution' | 'Withdrawal'>('Contribution')
  const operation = useRef(newOperation())
  const accountName = (id: string) => accounts.find(item => item.id === id)?.name ?? 'Conta removida'
  const deadline = deadlineInfo(goal, progress?.isFunded ?? false, todayInSaoPaulo())

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const effectiveDate = new Date().toISOString().slice(0, 10)
    const fingerprint = `${mode}|${account}|${amount}|${effectiveDate}`
    if (operation.current.fingerprint && operation.current.fingerprint !== fingerprint) operation.current = newOperation()
    operation.current.fingerprint = fingerprint
    const currentOperationId = operation.current.id
    const body = { operationId: currentOperationId, financialAccountId: account, amount, effectiveDate, description: null }
    onAction(async () => {
      try {
        if (mode === 'Contribution') await createContribution(workspaceId, goal.id, body)
        else await createWithdrawal(workspaceId, goal.id, body)
      } catch (error) {
        if (normalizeRequestError(error).resultUncertain) {
          try {
            const loaded = await listAllocations(workspaceId, goal.id)
            if (loaded.some(allocation => allocation.operationId === currentOperationId)) { operation.current = newOperation(); setAmount(''); onReload(); return }
          } catch { /* Sem confirmação por leitura, o reenvio manual preserva o operationId. */ }
        }
        throw error
      }
      operation.current = newOperation()
      setAmount('')
    })
  }

  const cancelGoalAllocation = async (allocation: GoalAllocation) => {
    try { await cancelAllocation(workspaceId, goal.id, allocation.id) } catch (error) {
      if (normalizeRequestError(error).resultUncertain) {
        try {
          const loaded = await listAllocations(workspaceId, goal.id)
          if (loaded.some(item => item.id === allocation.id && item.cancelledAt !== null)) { onReload(); return }
        } catch { /* Sem confirmação por leitura, mantém o resultado incerto. */ }
      }
      throw error
    }
    onReload()
  }

  const active = allocations.filter(item => !item.cancelledAt)
  const cancelled = allocations.filter(item => item.cancelledAt)
  const row = (item: GoalAllocation) => {
    const contribution = item.operationType === 'Contribution'
    return <li key={item.id} className={`goal-allocation${item.cancelledAt ? ' goal-allocation--cancelled' : ''}`}>
      <span className="goal-allocation__what"><strong>{contribution ? 'Aporte' : 'Retirada'}</strong> · {formatBrlDecimal(item.amount)} · {accountName(item.financialAccountId)}</span>
      <span className="meta">{formatDate(item.effectiveDate)} · {item.cancelledAt ? 'Cancelado (não conta no progresso)' : contribution ? 'Ativo (soma ao progresso)' : 'Ativo (subtrai do progresso)'}</span>
      {!item.cancelledAt && <button type="button" disabled={busy} onClick={() => {
        if (window.confirm(`Cancelar ${contribution ? 'o aporte' : 'a retirada'} de ${formatBrlDecimal(item.amount)}? A disponibilidade da conta e o progresso do objetivo serão recalculados.`)) onAction(() => cancelGoalAllocation(item))
      }}>Cancelar operação</button>}
    </li>
  }

  return <article className="card goal-details" aria-label={`Detalhe de ${goal.name}`}>
    <h4>{goal.name}</h4>
    <p className="meta">{goalTypeLabels[goal.goalType] ?? goal.goalType} · Estado: {goalStatusLabels[goal.status] ?? goal.status} · {deadline.text}</p>
    <GoalProgressBar name={goal.name} progress={progress} />
    <p>Meta: {formatBrlDecimal(goal.targetAmount)} · Alocado: {progress ? formatBrlDecimal(progress.allocatedAmount) : 'Carregando…'} · Restante: {progress ? formatBrlDecimal(progress.remainingToTarget) : '—'}</p>
    <div className="form__actions">
      {goal.status === 'Active' && <button disabled={busy} onClick={() => onAction(() => changeGoal(workspaceId, goal.id, 'pause'))}>Pausar</button>}
      {goal.status === 'Paused' && <button disabled={busy} onClick={() => onAction(() => changeGoal(workspaceId, goal.id, 'resume'))}>Retomar</button>}
      {(goal.status === 'Active' || goal.status === 'Paused') && <button disabled={busy} onClick={() => {
        if (window.confirm(`Concluir o objetivo “${goal.name}”? O histórico e os valores alocados serão preservados.`)) onAction(() => changeGoal(workspaceId, goal.id, 'complete'))
      }}>Concluir</button>}
      <button disabled={busy} onClick={() => { if (window.confirm(`Arquivar o objetivo “${goal.name}”? Ele sairá da lista ativa, mas seu histórico permanecerá disponível.`)) onArchive() }}>Arquivar</button>
    </div>
    <form className="form" onSubmit={submit} aria-busy={busy}>
      <label htmlFor="allocation-mode">Operação</label>
      <select id="allocation-mode" value={mode} onChange={event => setMode(event.target.value as 'Contribution' | 'Withdrawal')}><option value="Contribution">Aporte</option><option value="Withdrawal">Retirada</option></select>
      <label htmlFor="allocation-account">Conta</label>
      <select id="allocation-account" value={account} onChange={event => setAccount(event.target.value)}>{accounts.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select>
      <label htmlFor="allocation-amount">Valor</label>
      <input id="allocation-amount" value={amount} onChange={event => setAmount(event.target.value)} inputMode="decimal" required />
      <button type="submit" disabled={busy || goal.status !== 'Active'}>{busy ? 'Registrando operação…' : 'Registrar'}</button>
      {goal.status !== 'Active' && <p className="meta">Só objetivos ativos recebem aportes e retiradas.</p>}
    </form>
    <p className="meta">Alocar reserva parte do saldo de uma conta para este objetivo. Não cria receita, despesa nem transferência, não altera o saldo bancário e não soma de novo ao saldo.</p>
    <h5>Alocações que explicam o progresso</h5>
    {allocations.length === 0 ? <p className="meta">Nenhuma alocação registrada: o progresso é zero porque nada foi reservado para este objetivo.</p>
      : <>
        <p className="meta">Alocado = aportes ativos − retiradas ativas ({active.length} ativa(s){cancelled.length > 0 ? `, ${cancelled.length} cancelada(s) fora da conta` : ''}).</p>
        <ul className="goal-allocations">{allocations.map(row)}</ul>
      </>}
    <button type="button" onClick={onReload} disabled={busy}>Atualizar histórico</button>
  </article>
}
