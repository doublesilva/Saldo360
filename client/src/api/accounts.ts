import { CSRF_HEADER, getCsrfToken } from './csrf'

export type AccountType = 'Checking' | 'Savings' | 'Cash'
export type FinancialScope = 'PF' | 'PJ'

export const ACCOUNT_NAME_MAX_LENGTH = 100
export const ACCOUNT_INSTITUTION_MAX_LENGTH = 100

export const accountTypeLabels: Record<AccountType, string> = {
  Checking: 'Conta corrente',
  Savings: 'Poupança',
  Cash: 'Dinheiro',
}

export interface AccountResponsible {
  userId: string
  displayName: string
}

/**
 * Conta da família. Traz apenas o saldo inicial configurado (no início de openingBalanceDate, antes das
 * movimentações desse dia); não há saldo atual nesta etapa.
 */
export interface FinancialAccount {
  id: string
  name: string
  institution: string | null
  type: AccountType
  financialScope: FinancialScope
  /** null = conta conjunta. */
  responsible: AccountResponsible | null
  /**
   * Texto decimal exato ("-1234.56"); pode ser negativo. null (junto com a data) = saldo inicial não
   * informado, que não é zero. Só é formatado, nunca convertido para number.
   */
  openingBalance: string | null
  /** "yyyy-MM-dd", sem horário. */
  openingBalanceDate: string | null
  isActive: boolean
  currency: string
  createdAt: string
  /** 'Pluggy' quando a conta está integrada a uma conta bancária externa (T09.3); a origem é derivada do vínculo. */
  origin?: 'Manual' | 'Pluggy'
  integration?: AccountIntegration | null
  /** Conta principal do orçamento (T09.3.3): no máximo uma, sempre bancária e ativa. É só a seleção padrão das telas. */
  isPrimary?: boolean
}

/** Integração da conta. observedBalance é o saldo informado pelo banco (texto decimal), separado do saldo calculado. */
export interface AccountIntegration {
  linkId: string
  connectionId: string
  displayName: string
  institutionName: string | null
  status: 'Active' | 'ConsentExpired' | 'Attention'
  lastSyncedAt: string | null
  importFrom: string | null
  observedBalance: string | null
  observedAt: string | null
}

/** Campos cadastrais enviados à API. Família e criador vêm do servidor. */
export interface AccountInput {
  name: string
  institution: string | null
  type: AccountType
  financialScope: FinancialScope
  responsibleUserId: string | null
  /** Texto decimal exato ("-1234.56"). Valor e data juntos ou ambos null. */
  openingBalance: string | null
  openingBalanceDate: string | null
  /** true define (e substitui a anterior), false remove a condição de principal e ausente não altera. */
  isPrimary?: boolean
  /** Só ao desativar a conta principal: a substituta vira a principal na mesma operação. */
  replacementPrimaryAccountId?: string | null
}

/** Conta bancária (corrente ou poupança): a única que pode ser principal ou pagadora padrão de cartão. */
export const isBankAccount = (account: Pick<FinancialAccount, 'type'>): boolean => account.type !== 'Cash'

export type SaveAccountResult =
  | { ok: true; account: FinancialAccount }
  | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

function accountsUrl(workspaceId: string, accountId?: string): string {
  const base = `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/accounts`
  return accountId ? `${base}/${encodeURIComponent(accountId)}` : base
}

export async function listAccounts(workspaceId: string, includeInactive: boolean, signal?: AbortSignal): Promise<FinancialAccount[]> {
  const url = includeInactive ? `${accountsUrl(workspaceId)}?includeInactive=true` : accountsUrl(workspaceId)
  const response = await fetch(url, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar as contas (HTTP ${response.status}).`)
  return (await response.json()) as FinancialAccount[]
}

async function send(method: 'POST' | 'PUT' | 'DELETE', url: string, body: unknown, expected: number): Promise<SaveAccountResult> {
  const response = await fetch(url, {
    method,
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (response.status === expected) return { ok: true, account: (await response.json()) as FinancialAccount }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  if (response.status === 404) throw new Error('Conta ou orçamento não encontrado.')
  throw new Error(`Falha ao salvar a conta (HTTP ${response.status}).`)
}

export function createAccount(workspaceId: string, input: AccountInput): Promise<SaveAccountResult> {
  return send('POST', accountsUrl(workspaceId), input, 201)
}

/** PUT completo: campos cadastrais e situação (ativa/inativa). */
export function updateAccount(
  workspaceId: string,
  accountId: string,
  input: AccountInput,
  isActive: boolean,
): Promise<SaveAccountResult> {
  return send('PUT', accountsUrl(workspaceId, accountId), { ...input, isActive }, 200)
}

/** Define a conta como a principal do orçamento, substituindo a anterior de forma atômica. */
export function setPrimaryAccount(workspaceId: string, accountId: string): Promise<SaveAccountResult> {
  return send('PUT', `${accountsUrl(workspaceId, accountId)}/primary`, undefined, 200)
}

/** Remove explicitamente a condição de conta principal; o orçamento fica sem conta principal. */
export function clearPrimaryAccount(workspaceId: string, accountId: string): Promise<SaveAccountResult> {
  return send('DELETE', `${accountsUrl(workspaceId, accountId)}/primary`, undefined, 200)
}

/** A conta principal ativa do orçamento, quando existe. */
export const primaryAccountOf = (accounts: FinancialAccount[]): FinancialAccount | undefined =>
  accounts.find((account) => account.isPrimary === true && account.isActive)

export function toInput(account: FinancialAccount): AccountInput {
  return {
    name: account.name,
    institution: account.institution,
    type: account.type,
    financialScope: account.financialScope,
    responsibleUserId: account.responsible?.userId ?? null,
    openingBalance: account.openingBalance,
    openingBalanceDate: account.openingBalanceDate,
  }
}

export type BalanceUnavailableReason = 'OpeningBalanceNotConfigured' | 'BeforeOpeningBalanceDate'

/**
 * Saldo realizado no fechamento de asOf. Valores como texto decimal exato ("1300.00"): exibir com
 * formatBrlDecimal, nunca converter para number nem somar no navegador. Indisponível ⇔ totais e saldo null.
 */
export interface AccountBalance {
  accountId: string
  asOf: string
  openingBalance: string | null
  openingBalanceDate: string | null
  totalIncome: string | null
  totalExpense: string | null
  /** Transferências recebidas (destino) e enviadas (origem), separadas de receitas e despesas. */
  totalTransferIn: string | null
  totalTransferOut: string | null
  balance: string | null
  unavailableReason: BalanceUnavailableReason | null
  /** Origem do saldo inicial usado: 'Manual' (cadastro) ou 'BalanceAnchor' (derivado do saldo informado pelo banco, sem cadastro manual). */
  openingSource?: 'Manual' | 'BalanceAnchor' | null
}

/** Saldo realizado até hoje (America/Sao_Paulo, definido pela API). */
export async function getAccountBalance(workspaceId: string, accountId: string, signal?: AbortSignal): Promise<AccountBalance> {
  const response = await fetch(`${accountsUrl(workspaceId, accountId)}/balance`, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao calcular o saldo (HTTP ${response.status}).`)
  return (await response.json()) as AccountBalance
}

/**
 * Saldo projetado até `through`: saldo realizado de hoje (baseDate, America/Sao_Paulo) + previsões pendentes vencidas
 * e até a data-alvo. Valores em texto decimal exato: só formatar, nunca converter para number nem somar.
 * projectedBalance = realizedBalance + overdueIncome − overdueExpense + plannedIncome − plannedExpense (feito na API).
 * Indisponível ⇔ unavailableReason preenchido e todos os valores null.
 */
export interface AccountProjection {
  accountId: string
  baseDate: string
  through: string
  realizedBalance: string | null
  overdueIncome: string | null
  overdueExpense: string | null
  plannedIncome: string | null
  plannedExpense: string | null
  projectedBalance: string | null
  unavailableReason: BalanceUnavailableReason | null
}

export async function getAccountProjection(
  workspaceId: string,
  accountId: string,
  through: string,
  signal?: AbortSignal,
): Promise<AccountProjection> {
  const query = new URLSearchParams({ through })
  const response = await fetch(`${accountsUrl(workspaceId, accountId)}/projection?${query}`, { signal, headers: jsonHeaders })
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    throw new Error(Object.values(problem.errors ?? {}).flat()[0] ?? 'Data-alvo inválida.')
  }
  if (!response.ok) throw new Error(`Falha ao calcular a projeção (HTTP ${response.status}).`)
  return (await response.json()) as AccountProjection
}
