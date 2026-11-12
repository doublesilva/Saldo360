import { useEffect, useState } from 'react'
import type { FamilyDetails } from './api/families'
import { PersonalFinance } from './components/PersonalFinance'
import { InvitePage } from './components/InvitePage'
import { UserMenu } from './components/UserMenu'
import { SessionGate } from './components/SessionGate'
import { INVITE_PATH } from './invite'
import { useSession } from './session'
import './App.css'
import './AppShell.css'

function App() {
  const session = useSession()
  // Página do convite (/convite) ou página inicial; sem biblioteca de rotas.
  const [onInvitePage, setOnInvitePage] = useState(() => window.location.pathname === INVITE_PATH)
  const handleInvitationAccepted = (_family: FamilyDetails) => {
    window.history.replaceState(null, '', '/')
    setOnInvitePage(false)
  }

  // A API redireciona para "/?login=failed&ref=<correlação>" quando o login não é concluído.
  const params = new URLSearchParams(window.location.search)
  const financialArea = session.state.kind === 'authenticated' && !onInvitePage
  // O aviso de falha de login só vale enquanto não há sessão: com o usuário autenticado ele é resíduo da URL e aparecia no topo de qualquer tela
  // (inclusive Lançamentos) como se fosse erro da operação em curso. A parte "?login=failed&ref=..." é removida da URL quando a sessão existe.
  const loginFailed = params.get('login') === 'failed' && session.state.kind !== 'authenticated'
  const failureRef = params.get('ref')
  useEffect(() => {
    if (session.state.kind === 'authenticated' && new URLSearchParams(window.location.search).get('login') === 'failed') window.history.replaceState(null, '', window.location.pathname)
  }, [session.state.kind])

  return (
    <main className={`page${financialArea ? ' page--authenticated' : ''}`}>
      {!financialArea && <header className="page-header">
        <div>
          <h1>Saldo360</h1>
          <p className="subtitle">Orçamento pessoal</p>
        </div>
        {onInvitePage && <UserMenu session={session} />}
      </header>}

      {loginFailed && (
        <p className="notice notice--error" role="alert">
          Não foi possível concluir o login. Tente novamente.
          {failureRef && <> Código de referência: <code>{failureRef}</code></>}
        </p>
      )}

      {onInvitePage ? (
        <InvitePage session={session} onAccepted={handleInvitationAccepted} />
      ) : session.state.kind === 'authenticated' ? (
        <PersonalFinance key={`${session.state.user.issuer}|${session.state.user.subject}`} userArea={<UserMenu session={session} />} />
      ) : (
        <SessionGate session={session} />
      )}
    </main>
  )
}

export default App
