import type { PlanHistoryEntry, PlanLineKind, PlanLineNature, RiskLevel } from '../api/monthlyPlan'
import { formatBrlDecimal } from '../money'

/** Textos do planejamento mensal (T10.2). Só apresentação: nenhum número é calculado aqui. */
export const money = (value: string | null) => value === null ? 'Indisponível' : formatBrlDecimal(value)
export const plannedMoney = (value: string | null) => value === null ? 'Não definido' : formatBrlDecimal(value)
export const errorText = (error: unknown) => error instanceof Error ? error.message : 'Falha inesperada.'

export const kindLabels: Record<PlanLineKind, string> = { Income: 'Receita', Expense: 'Despesa', Transfer: 'Transferência' }
export const natureLabels: Record<PlanLineNature, string> = { Recurring: 'Recorrente', Fixed: 'Fixa', Variable: 'Variável', Extraordinary: 'Extraordinária', Reserve: 'Reserva / provisão' }
export const riskLabels: Record<RiskLevel, string> = { High: 'Alto', Medium: 'Médio', Unknown: 'Indefinido', Low: 'Baixo' }
export const periodLabels = { Past: 'Mês encerrado', Current: 'Mês corrente', Future: 'Mês futuro' } as const
export const actionLabels: Record<PlanHistoryEntry['action'], string> = { Created: 'Criada', Revised: 'Revisada', Removed: 'Removida', Restored: 'Restaurada', Copied: 'Copiada' }
export const limitStatusLabels: Record<string, string> = { WithinBudget: 'Dentro do limite', ApproachingLimit: 'Próximo do limite', LimitReached: 'Limite atingido', Exceeded: 'Limite excedido' }
export const coverageLabels = { Covered: 'Obrigações cobertas', Shortfall: 'Saldo insuficiente', Unknown: 'Indefinido' } as const

export const pendencyText: Record<string, string> = {
  NoPlan: 'Este mês ainda não tem planejamento. Adicione receitas, despesas ou limites, ou copie o mês anterior.',
  MovementsInReview: 'Há movimentações em revisão: elas ainda não entram nos totais realizados.',
  ItemizedAboveLimit: 'Há categorias em que as linhas detalhadas somam mais que o limite.',
  LimitsNotAttributable: 'Os limites por categoria valem para todas as contas: escolha “Todas as contas” para comparar o total.',
  HourlyIncomeAboveCalendar: 'Há receitas por hora com mais dias faturáveis que os dias úteis do calendário configurado.',
  LineOnInactiveAccount: 'Há linhas em conta inativa. Elas continuam no histórico, mas não aceitam novas linhas.',
}

export const riskReasonText: Record<string, string> = {
  NegativeProjectedBalance: 'Saldo projetado negativo na Agenda.',
  CategoryLimitExceeded: 'Limite por categoria excedido.',
  ProjectedExpenseAbovePlan: 'Despesa projetada acima da planejada.',
  ResultBelowPlan: 'Resultado projetado abaixo do planejado.',
  BalanceUnavailable: 'Saldo projetado indisponível.',
  IncompleteCoverage: 'Cobertura incompleta: parte das obrigações ainda é estimativa.',
}

export const sourceText: Record<string, string> = { MonthlyPlan: 'Planejamento mensal', PlannedTransactions: 'Previsões cadastradas', Budget: 'Metas por categoria', AggregateTarget: 'Meta agregada' }

export const dateTime = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)} ${new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: 'America/Sao_Paulo' })}`
