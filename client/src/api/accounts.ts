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
  /** Pode ser negativo. null (junto com a data) = saldo inicial não informado, que não é zero. */
  openingBalance: number | null
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
  /** Valor e data juntos ou ambos null. */
  openingBalance: number | null
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
