import { CSRF_HEADER, getCsrfToken } from './csrf'

/** Transferência PREVISTA entre contas próprias. Valores em texto decimal exato (só formatar). Situação calculada pela API. */
export type PlannedTransferStatus = 'Planned' | 'Overdue' | 'Realized' | 'Cancelled'

export interface PlannedTransferAccount { id: string; name: string; financialScope: string; isActive: boolean }
export interface PlannedTransferCandidate { transferId: string; transferDate: string; amount: string; description: string }
export interface PlannedTransfer {
  id: string; sourceAccount: PlannedTransferAccount; destinationAccount: PlannedTransferAccount; amount: string; dueDate: string; description: string
  status: PlannedTransferStatus; realizedTransferId: string | null; linkOrigin: 'Inferred' | 'UserConfirmed' | null; createdAt: string; cancelledAt: string | null
  candidates: PlannedTransferCandidate[]
}
export interface PlannedTransferInput { sourceAccountId: string; destinationAccountId: string; amount: number; dueDate: string; description: string }

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/planned-transfers`
const jsonHeaders = { Accept: 'application/json' }

export async function listPlannedTransfers(workspaceId: string, from: string, through: string, includeCancelled: boolean, signal?: AbortSignal): Promise<PlannedTransfer[]> {
  const query = new URLSearchParams({ from, through, includeCancelled: String(includeCancelled) })
  const response = await fetch(`${base(workspaceId)}?${query}`, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar as transferências previstas (HTTP ${response.status}).`)
  return (await response.json()) as PlannedTransfer[]
}

async function post(url: string, body?: unknown): Promise<PlannedTransfer> {
  const response = await fetch(url, {
    method: 'POST', headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (response.ok) return (await response.json()) as PlannedTransfer
  const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]>; message?: string }
  throw new Error(Object.values(problem.errors ?? {}).flat()[0] ?? problem.message ?? `Falha ao salvar a transferência prevista (HTTP ${response.status}).`)
}

export const createPlannedTransfer = (workspaceId: string, input: PlannedTransferInput) => post(base(workspaceId), input)
export const cancelPlannedTransfer = (workspaceId: string, id: string) => post(`${base(workspaceId)}/${encodeURIComponent(id)}/cancel`)
export const linkPlannedTransfer = (workspaceId: string, id: string, transferId: string) => post(`${base(workspaceId)}/${encodeURIComponent(id)}/link`, { transferId })
export const unlinkPlannedTransfer = (workspaceId: string, id: string) => post(`${base(workspaceId)}/${encodeURIComponent(id)}/unlink`)

export const plannedTransferStatusLabels: Record<PlannedTransferStatus, string> = {
  Planned: 'Prevista', Overdue: 'Vencida (não realizada)', Realized: 'Realizada', Cancelled: 'Cancelada',
}
