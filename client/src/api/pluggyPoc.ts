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

// ---- T09.2: importação controlada de transações ------------------------------------------------------------------

export type ImportStatus = 'Pending' | 'Posted' | 'Unknown'
export type ImportClass = 'PotentialIncome' | 'PotentialExpense' | 'PotentialTransfer' | 'CardPurchase' | 'CardInstallment' | 'InsufficientInformation'

export interface ImportedTransaction {
  id: string; connectionId: string; accountId: string; accountType: string | null; accountLabel: string | null; pluggyTransactionId: string
  date: string | null; description: string | null; descriptionRaw: string | null; amount: string | null; currency: string | null; type: string | null
  status: ImportStatus; operationType: string | null; category: string | null; installmentNumber: number | null; totalInstallments: number | null
  billId: string | null; cardLast4: string | null; classification: ImportClass; classificationReason: string; firstImportedAt: string
  lastObservedAt: string; lastChangedAt: string | null; missingSyncCount: number; possibleDuplicateKey: string | null; possibleDuplicateCount: number
  isNewInLastSync: boolean; changedInLastSync: boolean
}
export interface ImportCounters { total: number; new: number; updated: number; pending: number; missing: number; lastSyncAt: string | null }
export interface ImportedPage { items: ImportedTransaction[]; total: number; page: number; pageSize: number; counters: ImportCounters }
export interface SyncRun {
  id: string; connectionId: string; startedAt: string; finishedAt: string | null; status: 'Running' | 'Completed' | 'CompletedWithErrors' | 'Failed'
  accountsTotal: number; accountsSynced: number; seen: number; created: number; updated: number; unchanged: number; missing: number; errors: number; errorSummary: string | null
}
export interface Stability {
  connectionId: string; completedRuns: number; previousRunAt: string | null; latestRunAt: string | null; previouslyImported: number; preservedInLatest: number
  notSeenInLatest: number; newInLatest: number; newWithDateBeforePreviousRun: number; changedFieldsInLatest: Record<string, number>; pendingToPostedInLatest: number
  otherStatusChangesInLatest: number; pendingToPostedTotal: number; amountChangesTotal: number; descriptionChangesTotal: number; futureDated: number
  futureDatedPending: number; missingNow: number; pendingNow: number; possibleDuplicateGroups: number; notes: string[]
}
export interface ImportFilter { connectionId?: string; accountId?: string; from?: string; to?: string; status?: string; classification?: string; page?: number; pageSize?: number }

export interface LabStatus { enabled: boolean; importEnabled: boolean }
export async function getLabStatus(workspaceId: string, signal?: AbortSignal): Promise<LabStatus | null> {
  const response = await fetch(`${base(workspaceId)}/status`, { headers: { Accept: 'application/json' }, signal })
  return response.ok ? await response.json() as LabStatus : null
}

export async function syncConnection(workspaceId: string, connectionId: string) {
  return requestJson<SyncRun>(`${base(workspaceId)}/connections/${encodeURIComponent(connectionId)}/sync`, {
    method: 'POST', headers: { Accept: 'application/json', [CSRF_HEADER]: await getCsrfToken() },
  }, { fallback: 'Falha ao sincronizar as transações.', timeoutMs: 600_000 })
}

export function listImported(workspaceId: string, filter: ImportFilter, signal?: AbortSignal) {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(filter)) if (value !== undefined && value !== '') query.set(key, String(value))
  return get<ImportedPage>(`${base(workspaceId)}/imported-transactions?${query}`, 'Falha ao carregar as transações importadas.', signal)
}

export const listSyncRuns = (workspaceId: string, connectionId: string, signal?: AbortSignal) =>
  get<SyncRun[]>(`${base(workspaceId)}/connections/${encodeURIComponent(connectionId)}/sync-runs`, 'Falha ao carregar as sincronizações.', signal)

export const getStability = (workspaceId: string, connectionId: string, signal?: AbortSignal) =>
  get<Stability>(`${base(workspaceId)}/connections/${encodeURIComponent(connectionId)}/stability`, 'Falha ao comparar as sincronizações.', signal)
