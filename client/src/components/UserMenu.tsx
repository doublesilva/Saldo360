import { useEffect, useState } from 'react'
import { fetchCurrentUser, login, logout, type CurrentUser } from '../api/auth'

type State =
  | { kind: 'loading' }
  | { kind: 'anonymous' }
  | { kind: 'authenticated'; user: CurrentUser }
  | { kind: 'error'; message: string }

export function UserMenu() {
  const [state, setState] = useState<State>({ kind: 'loading' })
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    fetchCurrentUser(controller.signal)
      .then((user) => setState(user ? { kind: 'authenticated', user } : { kind: 'anonymous' }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setState({ kind: 'error', message: error instanceof Error ? error.message : 'Erro desconhecido.' })
      })
    return () => controller.abort()
  }, [])

  const handleLogout = async () => {
    setBusy(true)
    try {
      await logout()
      setState({ kind: 'anonymous' })
    } catch (error: unknown) {
      setState({ kind: 'error', message: error instanceof Error ? error.message : 'Erro desconhecido.' })
    } finally {
      setBusy(false)
    }
  }

  if (state.kind === 'loading') {
    return <div className="user-menu" aria-busy="true" />
  }

  if (state.kind === 'authenticated') {
    return (
      <div className="user-menu">
        <span className="user-menu__name" title={state.user.email ?? undefined}>
          {state.user.displayName}
        </span>
        <button type="button" onClick={handleLogout} disabled={busy}>
          Sair
        </button>
      </div>
    )
  }

  return (
    <div className="user-menu">
      {state.kind === 'error' && <span className="user-menu__error">{state.message}</span>}
      <button type="button" className="button--primary" onClick={login}>
        Entrar
      </button>
    </div>
  )
}
