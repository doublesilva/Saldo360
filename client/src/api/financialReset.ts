import { CSRF_HEADER, getCsrfToken } from './csrf'

export interface ResetPreview { workspaceId: string; status: string; counts: Record<string, number>; pendingFileCleanup: number }
export interface ResetResult { workspaceId: string; status: 'ResetCompleted' | 'AlreadyEmpty'; deleted: Record<string, number>; pendingFileCleanup: number }
const path = (id: string) => `/api/financial-workspaces/${encodeURIComponent(id)}/financial-reset`
async function result<T>(response: Response): Promise<T> {
  if (!response.ok) throw new Error(response.status === 409 ? 'O orçamento mudou ou está indisponível. Recarregue a prévia antes de continuar.' : `Não foi possível concluir (HTTP ${response.status}). Nenhuma confirmação de sucesso foi recebida; consulte o estado antes de repetir.`)
  return response.json() as Promise<T>
}
export async function previewFinancialReset(id: string, signal?: AbortSignal): Promise<ResetPreview> {
  return result(await fetch(`${path(id)}/preview`, { signal, cache: 'no-store' }))
}
export async function resetFinancialWorkspace(id: string, confirmation: string): Promise<ResetResult> {
  return result(await fetch(path(id), { method: 'POST', cache: 'no-store', headers: { [CSRF_HEADER]: await getCsrfToken(), 'Content-Type': 'application/json' }, body: JSON.stringify({ confirmation }) }))
}
export async function retryResetCleanup(id: string): Promise<{ pendingFileCleanup: number }> {
  return result(await fetch(`${path(id)}/cleanup`, { method: 'POST', cache: 'no-store', headers: { [CSRF_HEADER]: await getCsrfToken() } }))
}
