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
