import type { CoverageState, DecisionCoverage, DecisionPeriod, DecisionRecovery, DecisionSafeMargin } from '../../api/overview'
import { formatDate } from '../../money'
import { unavailableBalanceReasons } from './overviewText'

// T11.1: textos da decisão financeira. Nenhum valor é calculado aqui: o servidor entrega estados, datas e valores; este módulo só os descreve de forma objetiva.

export const coverageQuestion = 'Vou conseguir pagar minhas contas?'

export const periodText: Record<DecisionPeriod, string> = { Current: 'Mês corrente', Future: 'Mês futuro', Closed: 'Mês encerrado' }

export const coverageTag: Record<CoverageState, { label: string; tone: 'ok' | 'warning' | 'danger' | 'neutral' }> = {
  Covered: { label: 'Coberto', tone: 'ok' }, CoveredWithCredit: { label: 'Só com cheque especial', tone: 'warning' },
  Insufficient: { label: 'Insuficiente', tone: 'danger' }, Unavailable: { label: 'Indisponível', tone: 'neutral' },
}

/** Mensagem principal da cobertura; o estado também aparece como texto na etiqueta, então nunca depende só de cor. */
export function coverageMessage(c: Pick<DecisionCoverage, 'state' | 'firstBeyondLimitDate' | 'firstNegativeDate' | 'unavailableReason'>): string {
  switch (c.state) {
    case 'Covered': return 'Seus compromissos estão cobertos.'
    case 'CoveredWithCredit': return 'Os pagamentos dependem de utilização do cheque especial.'
    case 'Insufficient': return c.firstBeyondLimitDate ? `Há risco de insuficiência em ${formatDate(c.firstBeyondLimitDate)}.` : 'Há risco de insuficiência.'
    default: return 'Não é possível confirmar a cobertura com os dados disponíveis.'
  }
}

export const natureText: Record<string, string> = {
  Expenses: 'Despesas pendentes', CardInvoices: 'Faturas de cartão a pagar', Debts: 'Parcelas de financiamentos e dívidas', PlannedTransfers: 'Outros pagamentos futuros conhecidos',
}

const caveatText: Record<string, string> = {
  CardCoverageIncomplete: 'Há faturas de cartão sem conta pagadora identificada: a cobertura está incompleta.',
  DebtCoverageIncomplete: 'Há parcelas de dívida sem conta responsável: a cobertura está incompleta.',
  PendingBankAmbiguous: 'Há débitos pendentes do banco que não puderam ser associados com segurança a uma obrigação.',
  BalanceDivergentFromBank: 'O saldo calculado diverge do saldo informado pelo banco.',
  OverdraftLimitNotInformed: 'O limite do cheque especial não está cadastrado: nenhum crédito foi considerado.',
  DependsOnInferredIncome: 'O resultado depende de receitas estimadas, que não são entradas garantidas.',
  BalanceNotVerifiedWithBank: 'O saldo calculado não pôde ser conferido com o banco.',
  PositionsInconsistent: 'Os dados mudaram durante a leitura. Atualize para ver um retrato consistente.',
}
export const caveat = (code: string) => caveatText[code] ?? code

const marginReasonText: Record<string, string> = {
  ConsolidatedScope: 'No consolidado não há uma única conta de onde gastar. Selecione uma conta.',
  CompetenceNotCurrent: 'A margem vale para um gasto de hoje: selecione a competência corrente.',
  OpeningBalanceMissing: 'A conta não tem saldo inicial nem posição bancária de referência.',
  BalanceUnavailable: 'O saldo diário da conta não pôde ser calculado.',
  RecoveryTargetUnavailable: 'A meta de recuperação desta competência ainda não está disponível, e nenhuma meta é inventada.',
  ProvisionalRecoveryTarget: 'A meta de recuperação ainda é provisória (será fixada no dia 10).',
}
export const marginReason = (code: string) => marginReasonText[code] ?? caveatText[code] ?? code

export const marginNote: Record<string, string> = {
  CardSpendNotCovered: 'Vale para gasto à vista. Compras no cartão afetam o banco só no vencimento da fatura e não estão incluídas.',
  InferredIncomeExcluded: 'Receitas estimadas não entram no cálculo.',
}

export const constraintText: Record<string, string> = {
  DailyCoverage: 'Menor saldo diário até o fim da competência', RecoveryTarget: 'Meta original de recuperação', MinimumReserve: 'Reserva mínima', BudgetMargin: 'Margem do orçamento',
}
const notAppliedText: Record<string, string> = {
  NotConfigured: 'não configurada', SpendNotCategorised: 'não se aplica a um gasto sem categoria', AccountDidNotOpenNegative: 'a conta não abriu a competência usando o cheque especial',
}
export const notApplied = (reason: string | null) => reason ? notAppliedText[reason] ?? reason : 'não se aplica'

export const marginMessage = (m: DecisionSafeMargin) => m.state === 'Unavailable' ? 'Não é possível calcular uma margem segura com os dados disponíveis.' : null

export const recoveryStateText: Record<DecisionRecovery['state'], string> = {
  Provisional: 'Meta provisória calculada', Fixed: 'Meta fixada e registrada', Pending: 'Ausência de meta', NotRegistered: 'Ausência de meta', NotApplicable: 'Não se aplica', Unavailable: 'Ausência de meta',
}
export const recoveryNotApplicable: Record<string, string> = {
  AccountDidNotOpenNegative: 'A conta não abriu a competência usando o cheque especial: não há meta de recuperação.',
  ConsolidatedScope: 'A meta é acompanhada por conta. Selecione uma conta para vê-la.',
  OpeningBalanceMissing: 'Sem saldo de abertura, a meta não pode ser verificada.',
  RecoveryUnavailable: 'A recuperação não pôde ser apurada com os dados disponíveis.',
}

export const unavailableReason = (reason: string | null) => reason === null ? null
  : reason === 'ClosedCompetence' ? 'Competência encerrada: a cobertura só é analisada do mês corrente em diante.'
    : reason === 'NoAccounts' ? 'Não há contas bancárias no recorte.' : unavailableBalanceReasons[reason] ?? reason
