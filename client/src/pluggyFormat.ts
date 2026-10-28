import { formatBrlDecimal, formatDate } from './money'

export const NOT_INFORMED = 'Não informado'

export const connectionStates: Record<string, string> = {
  Healthy: 'Ativa', Updating: 'Atualizando', ActionRequired: 'Requer ação do usuário', ConsentExpired: 'Autorização expirada', Error: 'Com erro', Unknown: 'Desconhecido',
}
export const resourceStates: Record<string, string> = {
  Available: 'Disponível', Empty: 'Sem dados', NotSupported: 'Não suportado', Unavailable: 'Indisponível', Error: 'Erro de integração', ConsentExpired: 'Autorização expirada',
}

export function money(value: string | null, currency?: string | null): string {
  if (value === null) return NOT_INFORMED
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(value)
  if (!match || (match[3]?.length ?? 0) > 2) return currency && currency !== 'BRL' ? `${currency} ${value}` : value
  const normalized = `${match[1]}${match[2]}.${(match[3] ?? '').padEnd(2, '0')}`
  return !currency || currency === 'BRL' ? formatBrlDecimal(normalized) : `${currency} ${normalized}`
}

export const day = (value: string | null) => (value ? formatDate(value.slice(0, 10)) : NOT_INFORMED)
export const text = (value: string | number | null | undefined) => (value === null || value === undefined || value === '' ? NOT_INFORMED : String(value))

export const classificationLabels: Record<string, string> = {
  PotentialIncome: 'Receita potencial', PotentialExpense: 'Despesa potencial', PotentialTransfer: 'Transferência potencial',
  CardPurchase: 'Compra de cartão', CardInstallment: 'Parcela de cartão', InsufficientInformation: 'Informação insuficiente',
}
export const importStatusLabels: Record<string, string> = { Pending: 'Pendente', Posted: 'Efetivada', Unknown: 'Desconhecido' }
export const syncStatusLabels: Record<string, string> = { Running: 'Em andamento', Completed: 'Concluída', CompletedWithErrors: 'Concluída com erros', Failed: 'Falhou' }
export const dateTime = (value: string | null) => (value ? `${formatDate(value.slice(0, 10))} ${value.slice(11, 16)}` : NOT_INFORMED)

// ---- T09.3: integração das contas ----------------------------------------------------------------------------------

export const linkStatusLabels: Record<string, string> = { Active: 'Ativa', ConsentExpired: 'Autorização expirada', Attention: 'Requer atenção' }

/** Por que um movimento importado ficou fora de receitas, despesas e saldos até a sua decisão. */
export const reviewReasonLabels: Record<string, string> = {
  TransferWithoutCounterpart: 'Transferência sem a outra ponta em uma conta integrada',
  AmbiguousTransferCounterpart: 'Mais de uma possível contrapartida para a transferência',
  UnconfirmedCardBillPayment: 'Parece pagamento de fatura, mas o cartão não confirmou',
  AmbiguousCardBillPayment: 'Pode ser o pagamento de uma fatura, mas há mais de um candidato',
  AmbiguousBankDebit: 'Pagamento de fatura do cartão com mais de um débito bancário candidato',
  BillPaymentWithoutBankDebit: 'Pagamento de fatura sem débito correspondente em conta integrada',
  NoInvoiceForPayment: 'Pagamento de fatura sem fatura oficial que o comporte (o total do banco não confere com as compras)',
  InvoiceClosed: 'Compra em fatura que já foi fechada oficialmente',
  PossibleRefund: 'Possível estorno',
  CreditOperation: 'Operação de crédito ou empréstimo',
  MissingTypeOrAmount: 'Sem tipo ou valor informado',
  MissingDate: 'Sem data informada',
  MissingType: 'Sem tipo (débito/crédito) informado',
  MissingAmount: 'Sem valor informado',
  AmountPrecision: 'Valor com mais de duas casas decimais (não é arredondado)',
  ForeignCurrency: 'Moeda diferente de real',
  UnknownStatus: 'Situação desconhecida na Pluggy',
  TransferInvalid: 'Transferência inválida',
  SourceChanged: 'Os dados do banco mudaram depois do pareamento',
  'NoCounterpart:CashWithdrawal': 'Saque em dinheiro',
  'NoCounterpart:InvestmentRedemption': 'Resgate de aplicação',
  'Card:CardRefund': 'Estorno no cartão',
  'Card:CardCredit': 'Crédito no cartão',
  'Card:CardCreditOperation': 'Operação de crédito no cartão',
  'Card:UnclassifiedCardOperation': 'Operação de cartão não reconhecida',
}
export const reviewReason = (code: string | null) => (code === null ? NOT_INFORMED : reviewReasonLabels[code] ?? code)

export const resolutionLabels: Record<string, string> = {
  AsIncome: 'Contabilizar como receita', AsExpense: 'Contabilizar como despesa', AsCardPurchase: 'Contabilizar como compra de cartão', Ignore: 'Não contabilizar',
}

export const billStatusLabels: Record<string, string> = {
  Official: 'Fechada com o total do banco', OfficialDiverges: 'Fechada, mas o banco informou outro total', Open: 'Aberta (confere com o banco)',
  Mismatch: 'Aberta e diverge do banco', NoInvoice: 'Sem compras importadas',
}

export const unavailableReasonLabels: Record<string, string> = {
  OpeningBalanceNotConfigured: 'sem saldo inicial configurado', BeforeOpeningBalanceDate: 'antes da data do saldo inicial',
  CardHasNoCashBalance: 'cartão não tem saldo de caixa', NoObservedBalance: 'o banco não informou o saldo', ObservedBalancePrecision: 'saldo do banco com mais de duas casas',
  NeverSynced: 'a conta ainda não foi sincronizada por completo', NoImportedHistory: 'nenhum movimento foi importado', ReviewPending: 'há movimentos em revisão',
}
