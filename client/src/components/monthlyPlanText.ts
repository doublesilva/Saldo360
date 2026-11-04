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

/** Fontes ausentes e avisos da projeção integrada (PROJ-01): faltar cartão, receita ou fonte nunca é ausência de despesa ou de receita. */
export const qualityText: Record<string, string> = {
  NoCardsRegistered: 'Nenhum cartão cadastrado: faturas e compras de cartão não estão na projeção.',
  IncomeForecastNotConfigured: 'Receita PJ sem previsão aprovada: nenhuma receita futura entra na projeção.',
  IncomeForecastDismissed: 'A previsão deste recebimento foi descartada.',
  IncomeForecastInferred: 'Receita PJ inferida do faturamento recente (mediana de seis meses): estimativa, não garantida.',
  TaxReserveNotConfigured: 'Reserva de tributos não configurada: a disponibilidade da PJ não desconta tributos.',
  TransferDependsOnInferredIncome: 'O repasse PJ → PF projetado depende de receita inferida.',
  IncomeReceiptOverdue: 'Há valor a receber com data vencida: segue identificado, mas não entra no caixa de hoje.',
  IncomeReceiptUnidentified: 'Há entrada bancária sem pagador reconhecido no mês do recebimento: não liquida a previsão e pode duplicá-la.',
  IncomeReceiptExceedsForecast: 'Recebimento acima do esperado: o valor efetivo foi preservado.',
  CardCoverageIncomplete: 'Faturas de cartão ainda não fechadas pelo banco.',
  DebtCoverageIncomplete: 'Parcelas de dívida sem conta responsável.',
  PendingBankAmbiguous: 'Há débito bancário pendente que pode já estar na agenda: não foi somado ao saldo projetado.',
}
export const incomeSourceLabels: Record<string, string> = {
  Forecast: 'Estimativa aprovada', Realized: 'Realizada (substitui a estimativa)', Dismissed: 'Descartada', NotConfigured: 'Sem previsão aprovada', None: 'Sem recebimento no mês',
}
export const overdraftStateLabels: Record<string, string> = { Fixed: 'Meta fixada', Provisional: 'Meta provisória (calculada, não gravada)', None: 'Sem uso do limite na abertura' }

export const sourceText: Record<string, string> = { MonthlyPlan: 'Planejamento mensal', PlannedTransactions: 'Previsões cadastradas', Budget: 'Metas por categoria', AggregateTarget: 'Meta agregada' }

export const dateTime = (iso: string) => `${iso.slice(8, 10)}/${iso.slice(5, 7)}/${iso.slice(0, 4)} ${new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: 'America/Sao_Paulo' })}`
