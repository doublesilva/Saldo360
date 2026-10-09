export interface CurrentUser {
  /**
   * Igual a `subject`, mantido por compatibilidade. Sozinho NÃO identifica o usuário:
   * o mesmo subject pode existir em outro emissor. Use o par `issuer` + `subject`.
   */
  id: string
  /** Emissor (provedor OIDC) que autenticou o usuário. */
  issuer: string
  /** Identificador do usuário no emissor; string opaca, não normalizar. */
  subject: string
  displayName: string
  username: string | null
  email: string | null
}

/** Usuário da sessão atual, ou null quando não há sessão (401). */
export async function fetchCurrentUser(signal?: AbortSignal): Promise<CurrentUser | null> {
  const response = await fetch('/api/me', { signal, headers: { Accept: 'application/json' } })

  if (response.status === 401) return null
  if (!response.ok) throw new Error(`Falha ao consultar a sessão (HTTP ${response.status}).`)

  return (await response.json()) as CurrentUser
}

/** Navega para o login no provedor; ao final, a API devolve o usuário para a página atual. */
export function login(): void {
  const returnUrl = window.location.pathname + window.location.search
  window.location.assign(`/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`)
}

/** Encerra a sessão na API. O token CSRF é obtido na hora e enviado no header. */
export async function logout(): Promise<void> {
  const csrf = await fetch('/auth/csrf', { headers: { Accept: 'application/json' } })
  if (!csrf.ok) throw new Error(`Falha ao obter o token CSRF (HTTP ${csrf.status}).`)
  const { token } = (await csrf.json()) as { token: string }

  const response = await fetch('/auth/logout', { method: 'POST', headers: { 'X-CSRF-TOKEN': token } })
  if (!response.ok) throw new Error(`Falha ao sair (HTTP ${response.status}).`)
}
