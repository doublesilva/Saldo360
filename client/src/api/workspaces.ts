import { getCsrfToken, CSRF_HEADER } from './csrf'

export interface PersonalWorkspace { id: string; ownerUserId: string; name: string; type: 'Personal'; isActive: boolean }
export async function ensurePersonal(signal?: AbortSignal): Promise<PersonalWorkspace> {
  const response = await fetch('/api/me/financial-workspace', { method: 'POST', signal, headers: { [CSRF_HEADER]: await getCsrfToken(), Accept: 'application/json' } })
  if (!response.ok) throw new Error(`Não foi possível abrir seu orçamento (HTTP ${response.status}).`)
  const result = await response.json() as PersonalWorkspace
  if (result.type !== 'Personal' || !result.isActive) throw new Error('Orçamento pessoal indisponível.')
  return result
}
