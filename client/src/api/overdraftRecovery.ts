import { requestJson } from './apiErrors'
import { CSRF_HEADER, getCsrfToken } from './csrf'

export interface OverdraftRecovery {
  accountId: string; accountName: string; competence: string; periodKind: 'Past' | 'Current' | 'Future'; state: string
  contractedLimit: string | null; openingUsed: string; currentUsed: string; available: string | null; aboveLimit: string | null
  target: string; recovered: string; projectedRecovery: string | null; deviation: string | null; positiveBalance: string | null
  referenceDate: string; capturedAt: string; updatedAt: string; reasons: string[]
}

const url = (workspaceId: string, accountId: string, competence: string) =>
  `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/financial-dashboard/overdraft-recovery/${encodeURIComponent(accountId)}?${new URLSearchParams({ competence })}`

export function getOverdraftRecovery(workspaceId: string, accountId: string, competence: string, signal?: AbortSignal) {
  return requestJson<OverdraftRecovery>(url(workspaceId, accountId, competence), { signal }, { fallback: 'Meta de recuperação ainda não registrada.' })
}

export async function captureOverdraftRecovery(workspaceId: string, accountId: string, competence: string) {
  return requestJson<OverdraftRecovery>(url(workspaceId, accountId, competence), {
    method: 'POST', headers: { 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify({ competence }),
  }, { fallback: 'Não foi possível registrar a meta do mês.' })
}
