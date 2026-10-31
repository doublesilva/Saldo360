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
  TransferDateMismatch: 'Há uma possível contraparte com data diferente (uma transferência tem uma única data)',
  PossibleRefundByDescription: 'A descrição sugere devolução; qual gasto foi devolvido é decisão sua',
  UserUndone: 'Vínculo desfeito por você',
  RefundUndone: 'Devolução desfeita por você',
  SourceChanged: 'Os dados do banco mudaram depois do pareamento',
  'NoCounterpart:CashWithdrawal': 'Saque em dinheiro',
  'NoCounterpart:InvestmentRedemption': 'Resgate de aplicação',
  'Card:CardRefund': 'Estorno no cartão',
  'Card:CardCredit': 'Crédito no cartão',
  'Card:CardCreditOperation': 'Operação de crédito no cartão',
  'Card:UnclassifiedCardOperation': 'Operação de cartão não reconhecida',
}
export const reviewReason = (code: string | null) => (code === null ? NOT_INFORMED : reviewReasonLabels[code] ?? code)

/** Por que uma possível contraparte não pode ser confirmada. */
export const candidateBlockLabels: Record<string, string> = {
  DifferentDate: 'Data diferente: a transferência tem uma única data', DifferentAmount: 'Valor diferente', IncompatibleOperation: 'Operação bancária incompatível',
  SameDirection: 'Mesmo sentido', SameAccount: 'Mesma conta', ManualDecision: 'Há decisão manual incompatível', EndUsedElsewhere: 'Já usada em outro vínculo',
  EndUnavailable: 'Indisponível', NotTransferEvidence: 'Não é PIX, TED, DOC ou transferência', NotBankAccount: 'Não é conta bancária', AlreadyLinked: 'Já formam uma transferência',
}
/** Por que uma despesa não pode ser vinculada como a devolvida. */
export const refundBlockLabels: Record<string, string> = { ExceedsRefundable: 'O crédito é maior que o saldo ainda devolvível desta despesa' }

export const linkOriginLabels: Record<string, string> = { Inferred: 'Inferido pela regra automática', UserConfirmed: 'Confirmado por você' }
export const recoveryEffectLabels: Record<string, string> = {
  Income: 'Receita', Expense: 'Despesa', TransferIn: 'Transferência recebida', TransferOut: 'Transferência enviada', CardExpense: 'Compra no cartão', BillPayment: 'Pagamento de fatura',
  CardCredit: 'Crédito no cartão', Refund: 'Devolução', Evidence: 'Evidência de outro registro', None: 'Sem efeito',
}

/** Origem da categoria de um lançamento importado (T09.3.1). "Mapped" é o valor legado da categoria casada pela Pluggy. */
export const categorySourceLabels: Record<string, string> = {
  Pluggy: 'Pluggy', Mapped: 'Pluggy', Rule: 'Regra', AI: 'IA', User: 'Você',
}

/** Motivos de interrupção da IA devolvidos pela API, em linguagem do usuário. */
export const aiStopLabels: Record<string, string> = {
  AiUnavailable: 'A IA não está disponível neste ambiente (credencial ou configuração ausente); só a classificação local foi feita.',
  MaxItemsPerRun: 'O limite de lançamentos por execução foi atingido; rode novamente para continuar.',
  MaxTokensPerRun: 'O limite de tokens por execução foi atingido; rode novamente para continuar.',
  MaxCostPerRun: 'O limite de custo estimado por execução foi atingido; rode novamente para continuar.',
  ProviderFailure: 'A IA não respondeu; o que foi classificado localmente foi mantido. Tente novamente mais tarde.',
}

export const resolutionLabels: Record<string, string> = {
  AsIncome: 'Contabilizar como receita', AsExpense: 'Contabilizar como despesa', AsCardPurchase: 'Contabilizar como compra de cartão', Ignore: 'Não contabilizar',
}

export const billCycleLabels: Record<string, string> = { Open: 'Aberta', Closed: 'Fechada pelo banco' }
export const billPaymentLabels: Record<string, string> = { Unpaid: 'Não paga', PartiallyPaid: 'Parcialmente paga', Paid: 'Paga' }
export const billReconciliationLabels: Record<string, string> = {
  NotVerified: 'Conciliação não verificada', Reconciled: 'Conciliada com o banco', Divergent: 'Divergente do total do banco', Incomplete: 'Conciliação incompleta',
}
export const billReconciliationReasonLabels: Record<string, string> = {
  OpenItems: 'há itens pendentes ou em revisão nesta fatura', MissingHistory: 'o histórico importado não cobre a fatura inteira',
  NoImportedItems: 'nenhuma compra importada', TotalMismatch: 'a soma importada difere do total do banco', Open: 'fatura ainda aberta',
}
export const scheduleReasonLabels: Record<string, string> = { NotEnoughHistory: 'histórico insuficiente', Inconsistent: 'histórico inconsistente' }

export const unavailableReasonLabels: Record<string, string> = {
  OpeningBalanceNotConfigured: 'sem saldo inicial configurado', BeforeOpeningBalanceDate: 'antes da data do saldo inicial',
  CardHasNoCashBalance: 'cartão não tem saldo de caixa', NoObservedBalance: 'o banco não informou o saldo', ObservedBalancePrecision: 'saldo do banco com mais de duas casas',
  NeverSynced: 'a conta ainda não foi sincronizada por completo', NoImportedHistory: 'nenhum movimento foi importado', ReviewPending: 'há movimentos em revisão',
}
