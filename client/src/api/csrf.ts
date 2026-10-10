export const CSRF_HEADER = 'X-CSRF-TOKEN'

/** Token antiforgery da sessão atual, enviado no header das operações que alteram estado. */
export async function getCsrfToken(): Promise<string> {
  const response = await fetch('/auth/csrf', { headers: { Accept: 'application/json' } })
  if (!response.ok) throw new Error(`Falha ao obter o token CSRF (HTTP ${response.status}).`)
  const { token } = (await response.json()) as { token: string }
  return token
}
