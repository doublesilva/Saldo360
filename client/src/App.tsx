import { useEffect, useState } from 'react'
import { fetchHealth, type HealthResponse } from './api/health'
import type { FamilyDetails } from './api/families'
import { Families } from './components/Families'
import { InvitePage } from './components/InvitePage'
import { UserMenu } from './components/UserMenu'
import { INVITE_PATH } from './invite'
import { useSession } from './session'
import './App.css'

type State =
  | { kind: 'loading' }
  | { kind: 'ok'; health: HealthResponse; checkedAt: Date }
  | { kind: 'error'; message: string }

const statusLabels: Record<HealthResponse['status'], string> = {
  Healthy: 'Saudável',
  Degraded: 'Degradada',
  Unhealthy: 'Indisponível',
}

function toErrorState(error: unknown): State {
  const message = error instanceof Error ? error.message : 'Erro desconhecido.'
  return { kind: 'error', message: `Não foi possível conectar à API. ${message}` }
}

function App() {
  const session = useSession()
  // Página do convite (/convite) ou página inicial; sem biblioteca de rotas.
  const [onInvitePage, setOnInvitePage] = useState(() => window.location.pathname === INVITE_PATH)
  const [openFamily, setOpenFamily] = useState<FamilyDetails | null>(null)
  const [state, setState] = useState<State>({ kind: 'loading' })
  // Incrementado pelo botão para disparar uma nova verificação.
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    fetchHealth(controller.signal)
      .then((health) => setState({ kind: 'ok', health, checkedAt: new Date() }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState(toErrorState(error))
      })
    return () => controller.abort()
  }, [attempt])

  const handleInvitationAccepted = (family: FamilyDetails) => {
    window.history.replaceState(null, '', '/')
    setOpenFamily(family)
    setOnInvitePage(false)
  }

  const handleRetry = () => {
    setState({ kind: 'loading' })
    setAttempt((n) => n + 1)
  }

  // A API redireciona para "/?login=failed&ref=<correlação>" quando o login não é concluído.
  const params = new URLSearchParams(window.location.search)
  const loginFailed = params.get('login') === 'failed'
  const failureRef = params.get('ref')

  return (
    <main className="page">
      <header className="page-header">
        <div>
          <h1>Saldo360</h1>
          <p className="subtitle">Orçamento familiar</p>
        </div>
        <UserMenu session={session} />
      </header>

      {loginFailed && (
        <p className="notice notice--error" role="alert">
          Não foi possível concluir o login. Tente novamente.
          {failureRef && <> Código de referência: <code>{failureRef}</code></>}
        </p>
      )}

      {onInvitePage ? (
        <InvitePage session={session} onAccepted={handleInvitationAccepted} />
      ) : (
        session.state.kind === 'authenticated' && (
          <Families key={`${session.state.user.issuer}|${session.state.user.subject}`} initialFamily={openFamily} />
        )
      )}

      <section className="card" aria-live="polite">
        <h2>Conexão com a API</h2>

        {state.kind === 'loading' && <p className="status status--pending">Verificando…</p>}

        {state.kind === 'error' && <p className="status status--down">{state.message}</p>}

        {state.kind === 'ok' && (
          <>
            <p className={`status ${state.health.status === 'Healthy' ? 'status--up' : 'status--down'}`}>
              API {statusLabels[state.health.status]}
            </p>
            <ul className="checks">
              {state.health.checks.map((item) => (
                <li key={item.name}>
                  <span>{item.name}</span>
                  <span>{statusLabels[item.status]}</span>
                </li>
              ))}
            </ul>
            <p className="meta">Verificado às {state.checkedAt.toLocaleTimeString('pt-BR')}</p>
          </>
        )}

        <button type="button" onClick={handleRetry} disabled={state.kind === 'loading'}>
          Verificar novamente
        </button>
      </section>
    </main>
  )
}

export default App
