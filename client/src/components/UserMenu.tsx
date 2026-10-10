import { login } from '../api/auth'
import type { Session } from '../session'

export function UserMenu({ session }: { session: Session }) {
  const { state, busy, logout } = session

  if (state.kind === 'loading') {
    return <div className="user-menu" aria-busy="true" />
  }

  if (state.kind === 'authenticated') {
    return (
      <div className="user-menu">
        <span className="user-menu__name" title={state.user.email ?? undefined}>
          {state.user.displayName}
        </span>
        <button type="button" onClick={() => void logout()} disabled={busy}>
          Sair
        </button>
      </div>
    )
  }

  return (
    <div className="user-menu">
      {state.kind === 'error' && <span className="user-menu__error">{state.message}</span>}
      <button type="button" className="button--primary" onClick={() => login()}>
        Entrar
      </button>
    </div>
  )
}
