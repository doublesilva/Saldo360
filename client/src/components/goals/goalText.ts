import { formatDate } from '../../money'
import type { FinancialGoal, GoalProgress } from '../../api/goals'

export const goalStatusLabels: Record<string, string> = { Active: 'Ativo', Paused: 'Pausado', Completed: 'Concluído', Archived: 'Arquivado' }
export const goalTypeLabels: Record<string, string> = {
  EmergencyFund: 'Reserva de emergência', Purchase: 'Compra', Travel: 'Viagem', Education: 'Educação', DebtRepayment: 'Quitação de dívida', Other: 'Outro',
}
export const priorityLabels: Record<string, string> = { Low: 'Prioridade baixa', Medium: 'Prioridade média', High: 'Prioridade alta' }

const PERCENT = /^-?\d+(\.\d+)?$/

/** Percentual como o servidor calculou (alocado ÷ alvo), sem teto: 120% significa que o alocado passou do alvo. */
export function percentValue(percent: string | null | undefined): number | null {
  return typeof percent === 'string' && PERCENT.test(percent) ? Number.parseFloat(percent) : null
}

export function percentLabel(percent: string | null | undefined): string {
  const value = percentValue(percent)
  return value === null ? 'Indisponível' : `${value.toLocaleString('pt-BR', { minimumFractionDigits: 0, maximumFractionDigits: 1 })}%`
}

/** O desenho da barra para em 100%; o texto continua mostrando o percentual real. */
export const barPercent = (percent: string | null | undefined): number => Math.max(0, Math.min(100, percentValue(percent) ?? 0))

export type GoalStanding = 'Funded' | 'Overfunded' | 'InProgress' | 'Empty' | 'Unavailable'

/** Situação do valor: só compara o alocado com o alvo, nada de "em dia" ou "em risco" (não há regra que sustente isso). */
export function goalStanding(progress: Pick<GoalProgress, 'allocatedAmount' | 'isFunded' | 'progressPercentage'> | null): GoalStanding {
  if (!progress) return 'Unavailable'
  const percent = percentValue(progress.progressPercentage)
  if (percent !== null && percent > 100) return 'Overfunded'
  if (progress.isFunded) return 'Funded'
  return Number.parseFloat(progress.allocatedAmount) > 0 ? 'InProgress' : 'Empty'
}

export const standingLabels: Record<GoalStanding, string> = {
  Funded: 'Alvo atingido', Overfunded: 'Alocado acima do alvo', InProgress: 'Em andamento', Empty: 'Nada alocado ainda', Unavailable: 'Progresso indisponível',
}

export interface DeadlineInfo { text: string; overdue: boolean }

const DAY = 86_400_000
const toUtc = (date: string) => Date.UTC(Number(date.slice(0, 4)), Number(date.slice(5, 7)) - 1, Number(date.slice(8, 10)))

/** Prazo em texto. Vencido só é dito quando o objetivo ainda está aberto (ativo ou pausado) e o alvo não foi atingido. */
export function deadlineInfo(goal: Pick<FinancialGoal, 'targetDate' | 'status'>, funded: boolean, today: string): DeadlineInfo {
  if (!goal.targetDate) return { text: 'Sem prazo definido', overdue: false }
  const date = formatDate(goal.targetDate)
  const open = goal.status === 'Active' || goal.status === 'Paused'
  const days = Math.round((toUtc(goal.targetDate) - toUtc(today)) / DAY)
  if (!open || funded) return { text: `Prazo: ${date}`, overdue: false }
  if (days < 0) return { text: `Prazo vencido em ${date} (há ${-days} ${-days === 1 ? 'dia' : 'dias'})`, overdue: true }
  if (days === 0) return { text: `Prazo: ${date} (hoje)`, overdue: false }
  return { text: `Prazo: ${date} (em ${days} ${days === 1 ? 'dia' : 'dias'})`, overdue: false }
}
