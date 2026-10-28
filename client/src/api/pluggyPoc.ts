import { requestJson } from './apiErrors'
import { CSRF_HEADER, getCsrfToken } from './csrf'

export type ConnectionState = 'Healthy' | 'Updating' | 'ActionRequired' | 'ConsentExpired' | 'Error' | 'Unknown'
export type ResourceState = 'Available' | 'Empty' | 'NotSupported' | 'Unavailable' | 'Error' | 'ConsentExpired'

export interface PluggyConnection {
  id: string; label: string | null; itemReference: string; institution: string | null; institutionType: string | null; isOpenFinance: boolean | null
  state: ConnectionState; status: string | null; executionStatus: string | null; lastUpdatedAt: string | null; consentExpiresAt: string | null
  errorCode: string | null; errorMessage: string | null; integrationError: string | null; createdAt: string
}

export interface PluggyAccount {
  id: string; connectionId: string; type: string | null; subtype: string | null; name: string | null; marketingName: string | null
  maskedNumber: string | null; maskedBranchAccount: string | null; currency: string | null; balance: string | null; updatedAt: string | null
  overdraftContractedLimit: string | null; overdraftUsedLimit: string | null; creditLimit: string | null; availableCreditLimit: string | null
  minimumPayment: string | null; cardBrand: string | null; cardLevel: string | null; balanceCloseDate: string | null; balanceDueDate: string | null
}

export interface PluggyTransaction {
  id: string; date: string | null; description: string | null; descriptionRaw: string | null; amount: string | null; amountInAccountCurrency: string | null
  currency: string | null; type: string | null; status: string | null; category: string | null; balance: string | null; operationType: string | null
  paymentMethod: string | null; hasPayer: boolean; hasReceiver: boolean; hasReferenceNumber: boolean; merchant: string | null; merchantCategory: string | null
  installmentNumber: number | null; totalInstallments: number | null; installmentsTotalAmount: string | null; billId: string | null; cardLast4: string | null
}

export interface PluggyTransactionsPage { items: PluggyTransaction[]; nextCursor: string | null }

export interface PluggyBill {
  id: string; dueDate: string | null; closingDate: string | null; totalAmount: string | null; currency: string | null; minimumPayment: string | null; allowsInstallments: boolean | null
  financeCharges: { type: string | null; amount: string | null; currency: string | null; additionalInfo: string | null }[]
  payments: { amount: string | null; currency: string | null; paymentDate: string | null; paymentMode: string | null; valueType: string | null }[]
}

export interface PluggyBillsPage { items: PluggyBill[]; page: number; totalPages: number | null; total: number | null }

export interface FieldCoverage { field: string; present: number; total: number }
export interface ResourceCoverage {
  resource: string; state: ResourceState; count: number; earliestDate: string | null; latestDate: string | null
  fields: FieldCoverage[]; limitations: string[]; errorKind: string | null; fieldsReturned: string[]; fieldsMissing: string[]
}
export interface AccountCoverage { accountId: string; name: string | null; type: string | null; subtype: string | null; updatedAt: string | null; resources: ResourceCoverage[] }
export interface PluggyDiagnostics {
  connectionId: string; institution: string | null; state: ConnectionState; lastSync: string | null; generatedAt: string
  connectorProducts: string[]; itemResources: ResourceCoverage[]; accounts: AccountCoverage[]; notes: string[]
}

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/pluggy-poc`
const get = <T,>(url: string, fallback: string, signal?: AbortSignal) =>
  requestJson<T>(url, { headers: { Accept: 'application/json' }, signal }, { fallback, timeoutMs: 120_000 })

/** Falso quando o laboratório não está habilitado para este usuário/orçamento (a API responde 404). */
export async function isPluggyLabAvailable(workspaceId: string, signal?: AbortSignal): Promise<boolean> {
  const response = await fetch(`${base(workspaceId)}/status`, { headers: { Accept: 'application/json' }, signal })
  return response.ok
}

export const listConnections = (workspaceId: string, signal?: AbortSignal) =>
  get<PluggyConnection[]>(`${base(workspaceId)}/connections`, 'Falha ao carregar as conexões.', signal)

export const listPluggyAccounts = (workspaceId: string, connectionId: string, signal?: AbortSignal) =>
  get<PluggyAccount[]>(`${base(workspaceId)}/connections/${encodeURIComponent(connectionId)}/accounts`, 'Falha ao carregar as contas.', signal)

export function listPluggyTransactions(workspaceId: string, accountId: string, filter: { from?: string; to?: string; after?: string }, signal?: AbortSignal) {
  const query = new URLSearchParams()
  if (filter.from) query.set('from', filter.from)
  if (filter.to) query.set('to', filter.to)
  if (filter.after) query.set('after', filter.after)
  return get<PluggyTransactionsPage>(`${base(workspaceId)}/accounts/${encodeURIComponent(accountId)}/transactions?${query}`, 'Falha ao carregar as transações.', signal)
}

export const listPluggyBills = (workspaceId: string, accountId: string, page = 1, signal?: AbortSignal) =>
  get<PluggyBillsPage>(`${base(workspaceId)}/accounts/${encodeURIComponent(accountId)}/bills?page=${page}&pageSize=50`, 'Falha ao carregar as faturas.', signal)

export const getDiagnostics = (workspaceId: string, connectionId: string, signal?: AbortSignal) =>
  get<PluggyDiagnostics>(`${base(workspaceId)}/connections/${encodeURIComponent(connectionId)}/diagnostics`, 'Falha ao gerar o diagnóstico.', signal)

export async function addConnection(workspaceId: string, input: { itemId: string; label?: string }) {
  return requestJson<PluggyConnection>(`${base(workspaceId)}/connections`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: JSON.stringify(input),
  }, { fallback: 'Falha ao cadastrar o Item.' })
}
