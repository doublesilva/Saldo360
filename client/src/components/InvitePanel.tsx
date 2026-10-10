import { useRef, useState } from 'react'
import { createInvitation, revokeInvitation } from '../api/invitations'
import { buildInviteLink, isLocalOnlyHost } from '../invite'

type State =
  | { kind: 'idle' }
  | { kind: 'creating' }
  | { kind: 'created'; id: string; link: string; expiresAt: string; copied: boolean; revoking: boolean }
  | { kind: 'revoked' }
  | { kind: 'error'; message: string }

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

/**
 * Convite para a família (somente Owner). O link fica só na memória desta tela;
 * a origem vem do navegador, e o token vai no fragmento.
 */
export function InvitePanel({ familyId }: { familyId: string }) {
  const [state, setState] = useState<State>({ kind: 'idle' })
  const linkInput = useRef<HTMLInputElement>(null)

  const handleCreate = async () => {
    setState({ kind: 'creating' })
    try {
      const invitation = await createInvitation(familyId)
      setState({
        kind: 'created',
        id: invitation.id,
        link: buildInviteLink(invitation.token),
        expiresAt: invitation.expiresAt,
        copied: false,
        revoking: false,
      })
    } catch (error: unknown) {
      setState({ kind: 'error', message: errorMessage(error) })
    }
  }

  const handleCopy = async () => {
    if (state.kind !== 'created') return
    try {
      await navigator.clipboard.writeText(state.link)
      setState({ ...state, copied: true })
    } catch {
      // Sem acesso à área de transferência: seleciona o texto para cópia manual.
      linkInput.current?.select()
    }
  }

  const handleRevoke = async () => {
    if (state.kind !== 'created') return
    setState({ ...state, revoking: true })
    try {
      await revokeInvitation(familyId, state.id)
      setState({ kind: 'revoked' })
    } catch (error: unknown) {
      setState({ kind: 'error', message: errorMessage(error) })
    }
  }

  return (
    <div className="invite-panel">
      <h3>Convidar pessoa</h3>

      {state.kind === 'created' ? (
        <>
          <p className="meta">
            Envie este link para quem vai participar. Ele vale uma única vez, até{' '}
            {new Date(state.expiresAt).toLocaleString('pt-BR')}. Quem tiver o link pode entrar como membro.
          </p>
          <label htmlFor="invite-link" className="visually-hidden">
            Link do convite
          </label>
          <input
            id="invite-link"
            ref={linkInput}
            className="invite-panel__link"
            value={state.link}
            readOnly
            onFocus={(event) => event.currentTarget.select()}
          />
          {isLocalOnlyHost() && (
            <p className="notice">
              Este link usa <code>{window.location.host}</code> e só funciona nesta máquina. Para testar, abra-o em
              outro perfil do navegador (com outra conta) neste mesmo computador.
            </p>
          )}
          <div className="form__actions">
            <button type="button" className="button--primary" onClick={() => void handleCopy()}>
              {state.copied ? 'Copiado!' : 'Copiar convite'}
            </button>
            <button type="button" onClick={() => void handleRevoke()} disabled={state.revoking}>
              {state.revoking ? 'Revogando…' : 'Revogar convite'}
            </button>
          </div>
        </>
      ) : (
        <>
          {state.kind === 'revoked' && <p className="meta">Convite revogado. O link não funciona mais.</p>}
          {state.kind === 'error' && (
            <p className="field-error" role="alert">
              {state.message}
            </p>
          )}
          <button type="button" onClick={() => void handleCreate()} disabled={state.kind === 'creating'}>
            {state.kind === 'creating' ? 'Gerando…' : state.kind === 'revoked' ? 'Gerar novo convite' : 'Convidar pessoa'}
          </button>
        </>
      )}
    </div>
  )
}
