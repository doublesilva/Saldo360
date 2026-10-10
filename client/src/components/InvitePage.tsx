import { useState } from 'react'
import { login } from '../api/auth'
import type { FamilyDetails } from '../api/families'
import { acceptInvitation } from '../api/invitations'
import { clearPendingInviteToken, getPendingInviteToken } from '../invite'
import type { Session } from '../session'

type State =
  | { kind: 'ready' }
  | { kind: 'accepting' }
  | { kind: 'unavailable' }
  | { kind: 'retry'; message: string }
  | { kind: 'discarded' }

/**
 * Página /convite. O token já foi tirado da URL (ver captureInviteToken) e está no sessionStorage.
 * Sem sessão, pede login e volta para cá; com sessão, só aceita depois de um clique explícito.
 */
export function InvitePage({ session, onAccepted }: { session: Session; onAccepted: (family: FamilyDetails) => void }) {
  const [token] = useState(getPendingInviteToken)
  const [state, setState] = useState<State>({ kind: 'ready' })

  const handleAccept = async () => {
    if (!token) return
    setState({ kind: 'accepting' })
    try {
      const result = await acceptInvitation(token)
      switch (result.kind) {
        case 'accepted':
          clearPendingInviteToken()
          onAccepted(result.family)
          return
        case 'unavailable':
          clearPendingInviteToken()
          setState({ kind: 'unavailable' })
          return
        case 'unauthenticated':
          // Sessão expirou: mantém o token e volta ao login.
          login()
          return
      }
    } catch (error: unknown) {
      // Falha transitória (rede, CSRF, servidor): mantém o token para tentar de novo.
      setState({ kind: 'retry', message: error instanceof Error ? error.message : 'Erro desconhecido.' })
    }
  }

  const handleDiscard = () => {
    clearPendingInviteToken()
    setState({ kind: 'discarded' })
  }

  return (
    <section className="card" aria-labelledby="invite-title" aria-live="polite">
      <h2 id="invite-title">Convite para núcleo familiar</h2>
      <InviteBody
        hasToken={token !== null}
        session={session}
        state={state}
        onAccept={() => void handleAccept()}
        onDiscard={handleDiscard}
      />
    </section>
  )
}

function InviteBody({
  hasToken,
  session,
  state,
  onAccept,
  onDiscard,
}: {
  hasToken: boolean
  session: Session
  state: State
  onAccept: () => void
  onDiscard: () => void
}) {
  if (state.kind === 'unavailable') {
    return (
      <p className="status status--down">
        Convite inválido, expirado ou já utilizado. Peça um novo convite ao responsável pela família.
      </p>
    )
  }

  if (state.kind === 'discarded' || !hasToken) {
    return <p className="meta">Nenhum convite pendente. Abra o link de convite que você recebeu.</p>
  }

  if (session.state.kind === 'loading') {
    return <p className="status status--pending">Verificando sua sessão…</p>
  }

  if (session.state.kind !== 'authenticated') {
    return (
      <>
        <p>Você recebeu um convite para participar de um núcleo familiar no Saldo360.</p>
        <p className="meta">Entre com sua conta para continuar. Depois do login você volta para esta página.</p>
        <div className="form__actions">
          <button type="button" className="button--primary" onClick={() => login()}>
            Entrar para aceitar
          </button>
          <button type="button" onClick={onDiscard}>
            Descartar convite
          </button>
        </div>
      </>
    )
  }

  return (
    <>
      <p>
        Você está conectado como <strong>{session.state.user.displayName}</strong>. Ao aceitar, você entra no
        núcleo familiar como membro.
      </p>
      {state.kind === 'retry' && (
        <p className="field-error" role="alert">
          {state.message} Tente novamente.
        </p>
      )}
      <div className="form__actions">
        <button type="button" className="button--primary" onClick={onAccept} disabled={state.kind === 'accepting'}>
          {state.kind === 'accepting' ? 'Aceitando…' : 'Aceitar convite'}
        </button>
        <button type="button" onClick={onDiscard} disabled={state.kind === 'accepting'}>
          Descartar convite
        </button>
      </div>
    </>
  )
}
