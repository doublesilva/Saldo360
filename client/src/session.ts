import { useEffect, useState } from 'react'
import { fetchCurrentUser, logout, type CurrentUser } from './api/auth'

export type SessionState =
  | { kind: 'loading' }
  | { kind: 'anonymous' }
  | { kind: 'authenticated'; user: CurrentUser }
  | { kind: 'error'; message: string }

export interface Session {
  state: SessionState
  busy: boolean
  /** Verificação ainda em andamento depois de SLOW_SESSION_MS: a tela deve oferecer retorno útil e "Tentar novamente". */
  slow?: boolean
  logout: () => Promise<void>
  /** Repete a verificação; a resposta da tentativa anterior é descartada. */
  retry?: () => void
}

export const SLOW_SESSION_MS = 2000

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

/** Sessão do usuário (GET /api/me) e logout, compartilhados pela página. */
export function useSession(): Session {
  const [state, setState] = useState<SessionState>({ kind: 'loading' })
  const [busy, setBusy] = useState(false)

  const [attempt, setAttempt] = useState(0)
  const [slow, setSlow] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    const timer = setTimeout(() => { if (!controller.signal.aborted) setSlow(true) }, SLOW_SESSION_MS)
    fetchCurrentUser(controller.signal)
      .then((user) => { if (!controller.signal.aborted) setState(user ? { kind: 'authenticated', user } : { kind: 'anonymous' }) })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
      .finally(() => { clearTimeout(timer); if (!controller.signal.aborted) setSlow(false) })
    return () => { controller.abort(); clearTimeout(timer) }
  }, [attempt])

  // A tentativa anterior é abortada pelo efeito acima; sua resposta tardia não sobrescreve o estado mais recente.
  const retry = () => { setSlow(false); setState({ kind: 'loading' }); setAttempt(value => value + 1) }

  const handleLogout = async () => {
    setBusy(true)
    try {
      await logout()
      setState({ kind: 'anonymous' })
    } catch (error: unknown) {
      setState({ kind: 'error', message: errorMessage(error) })
    } finally {
      setBusy(false)
    }
  }

  return { state, busy, slow, logout: handleLogout, retry }
}
