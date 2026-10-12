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
  memberId: string
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
}

/** Campos cadastrais enviados à API. Família e criador vêm do servidor. */
export interface AccountInput {
  name: string
  institution: string | null
  type: AccountType
  financialScope: FinancialScope
  responsibleMemberId: string | null
  /** Texto decimal exato ("-1234.56"). Valor e data juntos ou ambos null. */
  openingBalance: string | null
  openingBalanceDate: string | null
}

export type SaveAccountResult =
  | { ok: true; account: FinancialAccount }
  | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

function accountsUrl(familyId: string, accountId?: string): string {
  const base = `/api/families/${encodeURIComponent(familyId)}/accounts`
  return accountId ? `${base}/${encodeURIComponent(accountId)}` : base
}

export async function listAccounts(familyId: string, includeInactive: boolean, signal?: AbortSignal): Promise<FinancialAccount[]> {
  const url = includeInactive ? `${accountsUrl(familyId)}?includeInactive=true` : accountsUrl(familyId)
  const response = await fetch(url, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar as contas (HTTP ${response.status}).`)
  return (await response.json()) as FinancialAccount[]
}

async function send(method: 'POST' | 'PUT', url: string, body: unknown, expected: number): Promise<SaveAccountResult> {
  const response = await fetch(url, {
    method,
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: JSON.stringify(body),
  })

  if (response.status === expected) return { ok: true, account: (await response.json()) as FinancialAccount }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  if (response.status === 404) throw new Error('Conta ou família não encontrada.')
  throw new Error(`Falha ao salvar a conta (HTTP ${response.status}).`)
}

export function createAccount(familyId: string, input: AccountInput): Promise<SaveAccountResult> {
  return send('POST', accountsUrl(familyId), input, 201)
}

/** PUT completo: campos cadastrais e situação (ativa/inativa). */
export function updateAccount(
  familyId: string,
  accountId: string,
  input: AccountInput,
  isActive: boolean,
): Promise<SaveAccountResult> {
  return send('PUT', accountsUrl(familyId, accountId), { ...input, isActive }, 200)
}

export function toInput(account: FinancialAccount): AccountInput {
  return {
    name: account.name,
    institution: account.institution,
    type: account.type,
    financialScope: account.financialScope,
    responsibleMemberId: account.responsible?.memberId ?? null,
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
}

/** Saldo realizado até hoje (America/Sao_Paulo, definido pela API). */
export async function getAccountBalance(familyId: string, accountId: string, signal?: AbortSignal): Promise<AccountBalance> {
  const response = await fetch(`${accountsUrl(familyId, accountId)}/balance`, { signal, headers: jsonHeaders })
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
  familyId: string,
  accountId: string,
  through: string,
  signal?: AbortSignal,
): Promise<AccountProjection> {
  const query = new URLSearchParams({ through })
  const response = await fetch(`${accountsUrl(familyId, accountId)}/projection?${query}`, { signal, headers: jsonHeaders })
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    throw new Error(Object.values(problem.errors ?? {}).flat()[0] ?? 'Data-alvo inválida.')
  }
  if (!response.ok) throw new Error(`Falha ao calcular a projeção (HTTP ${response.status}).`)
  return (await response.json()) as AccountProjection
}
