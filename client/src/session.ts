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
  logout: () => Promise<void>
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

/** Sessão do usuário (GET /api/me) e logout, compartilhados pela página. */
export function useSession(): Session {
  const [state, setState] = useState<SessionState>({ kind: 'loading' })
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    fetchCurrentUser(controller.signal)
      .then((user) => setState(user ? { kind: 'authenticated', user } : { kind: 'anonymous' }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [])

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

  return { state, busy, logout: handleLogout }
}
