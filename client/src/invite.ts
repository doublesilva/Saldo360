/**
 * Token de convite no navegador.
 *
 * O link do convite leva o token no fragmento (/convite#token=...), que não é enviado ao servidor
 * nem aparece em logs de proxy. Ao abrir a página, o token é guardado no sessionStorage (só esta aba)
 * e o fragmento é removido da URL na hora. Ele sobrevive ao redirecionamento de login e é apagado
 * após o aceite ou um erro definitivo.
 */
export const INVITE_PATH = '/convite'

const STORAGE_KEY = 'saldo360.pendingInvitation'

// Se o sessionStorage estiver indisponível, o token fica só na memória (não sobrevive ao login).
let memoryToken: string | null = null

function storage(): Storage | null {
  try {
    return window.sessionStorage
  } catch {
    return null
  }
}

/** Na página do convite, guarda o token do fragmento e remove o fragmento da URL. */
export function captureInviteToken(location: Location = window.location, history: History = window.history): void {
  if (location.pathname !== INVITE_PATH || !location.hash) return

  const token = new URLSearchParams(location.hash.slice(1)).get('token')
  history.replaceState(history.state, '', location.pathname + location.search)

  if (token) {
    memoryToken = token
    try {
      storage()?.setItem(STORAGE_KEY, token)
    } catch {
      // Armazenamento bloqueado: segue só com a memória.
    }
  }
}

export function getPendingInviteToken(): string | null {
  try {
    return storage()?.getItem(STORAGE_KEY) ?? memoryToken
  } catch {
    return memoryToken
  }
}

export function clearPendingInviteToken(): void {
  memoryToken = null
  try {
    storage()?.removeItem(STORAGE_KEY)
  } catch {
    // Nada a limpar.
  }
}

/** Link do convite montado com a origem do navegador (não com o Host visto pela API). */
export function buildInviteLink(token: string, origin: string = window.location.origin): string {
  return `${origin}${INVITE_PATH}#token=${encodeURIComponent(token)}`
}

/** Origens que só funcionam na própria máquina. */
export function isLocalOnlyHost(hostname: string = window.location.hostname): boolean {
  return (
    hostname === 'localhost' ||
    hostname.endsWith('.localhost') ||
    hostname === '127.0.0.1' ||
    hostname === '[::1]' ||
    hostname === '::1'
  )
}
