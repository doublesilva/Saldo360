import type { DecisionCoverage } from '../../api/overview'
import { formatDate } from '../../money'
import { unavailableBalanceReasons } from './overviewText'

// T11.1: textos da decisão financeira usados pelos pontos de atenção. Nenhum valor é calculado aqui: o servidor entrega estados, datas e valores; este módulo só os descreve.

/** Mensagem principal da cobertura; o estado também aparece como texto, então nunca depende só de cor. */
export function coverageMessage(c: Pick<DecisionCoverage, 'state' | 'firstBeyondLimitDate' | 'firstNegativeDate' | 'unavailableReason'>): string {
  switch (c.state) {
    case 'Covered': return 'Seus compromissos estão cobertos.'
    case 'CoveredWithCredit': return 'Os pagamentos dependem de utilização do cheque especial.'
    case 'Insufficient': return c.firstBeyondLimitDate ? `Há risco de insuficiência em ${formatDate(c.firstBeyondLimitDate)}.` : 'Há risco de insuficiência.'
    default: return 'Não é possível confirmar a cobertura com os dados disponíveis.'
  }
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

export const unavailableReason = (reason: string | null) => reason === null ? null
  : reason === 'ClosedCompetence' ? 'Competência encerrada: a cobertura só é analisada do mês corrente em diante.'
    : reason === 'NoAccounts' ? 'Não há contas bancárias no recorte.' : unavailableBalanceReasons[reason] ?? reason
