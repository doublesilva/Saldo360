import type { FinancialGoal, GoalProgress } from '../../api/goals'
import { formatBrlDecimal } from '../../money'
import { barPercent, deadlineInfo, goalStanding, goalStatusLabels, goalTypeLabels, percentLabel, priorityLabels, standingLabels } from './goalText'

/** Barra de progresso com o percentual E os valores em texto; a barra para em 100%, o texto não. */
export function GoalProgressBar({ name, progress }: { name: string; progress: GoalProgress | null }) {
  if (!progress) return <p className="meta" role="status">Progresso indisponível no momento: os valores alocados não puderam ser carregados.</p>
  const standing = goalStanding(progress)
  return <div className="goal-progress">
    <div className="goal-progress__head">
      <strong>{percentLabel(progress.progressPercentage)}</strong>
      <span className={`badge goal-standing goal-standing--${standing.toLowerCase()}`}>{standingLabels[standing]}</span>
    </div>
    <div className="goal-progress__track" role="progressbar" aria-label={`Progresso de ${name}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(barPercent(progress.progressPercentage))}
      aria-valuetext={`${percentLabel(progress.progressPercentage)}: ${formatBrlDecimal(progress.allocatedAmount)} de ${formatBrlDecimal(progress.targetAmount)}`}>
      <span style={{ width: `${barPercent(progress.progressPercentage)}%` }} />
    </div>
  </div>
}

/**
 * Um objetivo de acumulação: alvo, alocado, restante, prazo e estado. O alocado é a soma dos aportes ativos menos as retiradas ativas deste objetivo (a lista
 * está no detalhe); não é saldo bancário nem limite de orçamento.
 */
export function GoalCard({ goal, progress, today, selected, onOpen }: { goal: FinancialGoal; progress: GoalProgress | null; today: string; selected: boolean; onOpen: () => void }) {
  const deadline = deadlineInfo(goal, progress?.isFunded ?? false, today)
  return <li className={`goal-card goal-card--${goal.status.toLowerCase()}`} aria-label={`Objetivo ${goal.name}`}>
    <div className="goal-card__head">
      <button type="button" className="goal-card__open" aria-expanded={selected} onClick={onOpen}><strong>{goal.name}</strong></button>
      <span className="badge goal-status">{goalStatusLabels[goal.status] ?? goal.status}</span>
    </div>
    <p className="meta">{goalTypeLabels[goal.goalType] ?? goal.goalType} · {priorityLabels[goal.priority] ?? goal.priority}</p>
    <GoalProgressBar name={goal.name} progress={progress} />
    <dl className="goal-card__facts">
      <div><dt>Alvo</dt><dd>{formatBrlDecimal(goal.targetAmount)}</dd></div>
      <div><dt>Alocado</dt><dd>{progress ? formatBrlDecimal(progress.allocatedAmount) : 'Indisponível'}</dd></div>
      <div><dt>Restante</dt><dd>{progress ? formatBrlDecimal(progress.remainingToTarget) : 'Indisponível'}</dd></div>
      <div><dt>Prazo</dt><dd className={deadline.overdue ? 'goal-card__overdue' : undefined}>{deadline.text.replace(/^Prazo: /, '')}</dd></div>
    </dl>
    {goal.status === 'Paused' && <p className="meta">Objetivo pausado: novas alocações ficam bloqueadas até retomar. Os valores já alocados continuam contando.</p>}
    {goal.status === 'Completed' && <p className="meta">Objetivo concluído: o histórico e os valores alocados foram preservados.</p>}
    <button type="button" onClick={onOpen} aria-label={`Ver detalhe de ${goal.name}`}>{selected ? 'Detalhe aberto' : 'Ver detalhe'}</button>
  </li>
}
