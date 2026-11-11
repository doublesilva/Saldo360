import { formatBrlDecimal, formatDate } from '../../money'
import { formatMonthYear } from '../../dates'
import type { PlanActualScope } from '../../api/planActual'

// Textos e formatadores da Visão Geral. Valores monetários continuam sendo texto decimal exato: aqui só se formata, nunca se calcula.

export const money = (value: string | null | undefined) => value === null || value === undefined ? 'Indisponível' : formatBrlDecimal(value)
/** Valor monetário em texto maior que zero (só comparação de texto; nada é somado nem arredondado). */
export const isPositiveAmount = (value: string | null | undefined) => typeof value === 'string' && /^\d+\.\d{2}$/.test(value) && /[1-9]/.test(value)
/** Percentual em texto acima de 100 (alocado passou do alvo). */
export const exceedsHundred = (percent: string | null | undefined) => typeof percent === 'string' && /^\d+(\.\d+)?$/.test(percent)
  && (Number.parseInt(percent, 10) > 100 || (Number.parseInt(percent, 10) === 100 && /[1-9]/.test(percent.split('.')[1] ?? '')))
export const isNegative = (value: string | null | undefined) => typeof value === 'string' && /^-\d+\.\d{2}$/.test(value) && /[1-9]/.test(value)
export const percentText = (value: string | null) => value === null ? null : `${value.replace('.', ',')}%`

/** Largura de barra em CSS a partir do percentual em texto do servidor ("120.0" → "min(100%, 120.0%)"): sem converter para número. */
const PERCENT_TEXT = /^\d+(\.\d+)?$/
export const barWidth = (percent: string | null) => percent !== null && PERCENT_TEXT.test(percent) ? `min(100%, ${percent}%)` : '0%'
/** Valor inteiro (0–100) para leitores de tela; o percentual exibido continua sendo o texto do servidor. */
export const percentNow = (percent: string | null) => percent !== null && PERCENT_TEXT.test(percent) ? Math.min(100, Number.parseInt(percent, 10)) : 0

export const periodLabels = { Past: 'Mês encerrado', Current: 'Mês corrente', Future: 'Mês futuro' } as const

export const monthTitle = (competence: string) => formatMonthYear(`${competence}-01`)
export const shortMonth = (competence: string) => {
  const [year, month] = competence.split('-')
  const names = ['jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez']
  return `${names[Number(month) - 1] ?? month}/${year.slice(2)}`
}

export const dateTime = (iso: string | null) => iso
  ? `${formatDate(iso.slice(0, 10))} às ${new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: 'America/Sao_Paulo' })}`
  : null

export const unavailableBalanceReasons: Record<string, string> = {
  OpeningBalanceNotConfigured: 'Sem saldo inicial configurado', BeforeOpeningBalanceDate: 'Anterior ao saldo inicial', Unavailable: 'Não foi possível calcular',
}

/** Explicações curtas das ressalvas do servidor (códigos de `reasons`). Ficam em detalhe expansível, não no corpo da tela. */
export const reasonText: Record<string, string> = {
  MovementsInReview: 'Há movimentações do banco em revisão: elas ainda não entram nos totais.',
  PartialPeriod: 'O histórico importado não cobre o mês inteiro.',
  CardCoverageIncomplete: 'Parte das obrigações dos cartões ainda é estimativa ou aguarda a fatura do banco.',
  DebtCoverageIncomplete: 'Há parcelas de dívida sem conta responsável.',
  PendingBankAmbiguous: 'Há débito bancário pendente que pode já estar na agenda: não foi somado ao saldo projetado.',
  IncomeForecastInferred: 'Receita PJ inferida do faturamento recente: estimativa, não garantida.',
  TaxReserveNotConfigured: 'Reserva de tributos não configurada: a disponibilidade da PJ não desconta tributos.',
  TransferDependsOnInferredIncome: 'O repasse PJ → PF projetado depende de receita inferida.',
  IncomeReceiptOverdue: 'Há valor a receber com data vencida: segue identificado, mas não entra no caixa de hoje.',
  IncomeReceiptUnidentified: 'Há entrada bancária sem pagador reconhecido no mês do recebimento: não liquida a previsão e pode duplicá-la.',
  IncomeReceiptExceedsForecast: 'Recebimento acima do esperado: o valor efetivo foi preservado.',
  IncomeReceiptAmbiguous: 'Mais de uma competência disputa o mesmo recebimento: a associação é ambígua e a projeção é parcial.',
  PlanNotAttributable: 'O orçamento por categoria vale para todas as contas; escolha “Todas as contas” para compará-lo.',
  PlanNotDefined: 'Não há valor planejado para este mês.',
  MissingPayerAccount: 'Há cartões sem conta pagadora.',
  InsufficientHistory: 'Histórico insuficiente para comparar com o início do mês.',
  NoBankObservation: 'Sem observação de saldo do banco.',
  ProjectionUnavailable: 'Sem saldo projetado confiável.',
  FuturePeriod: 'Mês futuro: só há projeção.',
  'Cards:WeakSchedule': 'Cartões: o ciclo da fatura ainda não foi confirmado pelo histórico do banco.',
  'Cards:AwaitingBankBill': 'Cartões: aguardando a fatura do banco.',
  'Cards:UnprojectedInstallments': 'Cartões: há parcelas sem mês definido.',
  'Cards:AmbiguousInstallments': 'Cartões: há parcelamentos ambíguos.',
  'Cards:SyncAttention': 'Cartões: a conexão com o banco pede atenção.',
  'Cards:NoBankHistory': 'Cartões: sem faturas do banco.',
  'Cards:KnownWithoutDueDate': 'Cartões: obrigação conhecida sem vencimento.',
}
export const explain = (reasons: string[]) => reasons.map(reason => reasonText[reason]).filter((text): text is string => Boolean(text))

export const scopeLabel = (scope: Pick<PlanActualScope, 'kind' | 'accountName'>) =>
  scope.kind === 'all' || !scope.accountName ? 'Todas as contas' : scope.kind === 'primary' ? `Conta principal · ${scope.accountName}` : scope.accountName

/** Rótulos dos componentes do fluxo de caixa (códigos do servidor). */
export const componentText: Record<string, string> = {
  IncomeRealized: 'Receitas recebidas', IncomePending: 'Receitas previstas a receber', TransfersReceived: 'Transferências recebidas',
  BankExpenses: 'Despesas pagas pela conta', CardInvoicePayments: 'Faturas de cartão pagas', PendingPlanned: 'Despesas e parcelas de dívidas e financiamentos pendentes',
  PendingBank: 'Despesas bancárias pendentes', PendingCardInvoices: 'Faturas de cartão ainda devidas', TransfersSent: 'Transferências enviadas',
}
export const resultStatusText = { Positive: 'Positivo', Negative: 'Negativo', Neutral: 'Neutro', Unavailable: 'Indisponível' } as const
export const bridgeReasonText: Record<string, string> = {
  BridgeNotReconciled: 'Os dados existentes não explicam toda a diferença: a projeção é parcial.', NoBankBalance: 'Sem saldo observado do banco: a ponte não pode ser montada.',
  ProjectionUnavailable: 'Sem saldo projetado confiável.', ClosedMonth: 'Mês encerrado: vale o saldo realizado.',
}
export const resultReasonText: Record<string, string> = {
  PlanPartial: 'Há ressalvas nos dados do mês: o resultado é parcial.', TransfersUnavailable: 'As transferências da conta não puderam ser lidas: o resultado de caixa é parcial.',
}

/** Motivo pelo qual a meta de recuperação ainda não pôde ser fixada (códigos do servidor). */
export const recoveryPendingText: Record<string, string> = {
  ProjectionUnavailable: 'não há projeção confiável do fechamento', CardCoverageIncomplete: 'há faturas de cartão sem conta pagadora identificada',
  DebtCoverageIncomplete: 'há parcelas de dívida sem conta responsável', NoBankObservation: 'o banco ainda não informou o saldo',
}
