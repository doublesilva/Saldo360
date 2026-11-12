import { login } from '../api/auth'
import { parseRoute, sectionPhrases } from '../routing'
import type { Session } from '../session'

/**
 * Entrada do site antes de haver sessão: verificando, sessão ausente ou expirada (botão "Entrar", com o destino do link) e falha de consulta
 * ("Tentar novamente"). Falha de consulta nunca é apresentada como sessão expirada, nem o contrário. A área de ação tem altura reservada.
 */
export function SessionGate({ session }: { session: Session }) {
  const { state, slow, retry } = session
  if (state.kind === 'authenticated') return null
  const route = parseRoute(window.location)
  const direct = window.location.pathname !== '/' && route.problems.indexOf('endereço') < 0
  return <section className="card session-gate" aria-labelledby="session-gate-title" aria-busy={state.kind === 'loading' || undefined}>
    <h2 id="session-gate-title">{state.kind === 'error' ? 'Não foi possível verificar o acesso' : state.kind === 'loading' ? 'Verificando acesso…' : 'Entre para continuar'}</h2>
    {state.kind === 'loading' && <p role="status">{slow ? 'A verificação está demorando mais que o normal. Você pode tentar novamente.' : 'Estamos conferindo sua sessão.'}</p>}
    {state.kind === 'anonymous' && <p role="status">{direct ? `Entre para abrir ${sectionPhrases[route.section]}.` : 'Entre para acessar o seu orçamento.'}</p>}
    {state.kind === 'error' && <p role="alert">Não conseguimos consultar sua sessão agora. Isso não significa que ela tenha expirado: tente novamente em instantes.</p>}
    <div className="session-gate__actions">
      {state.kind === 'anonymous' && <button type="button" className="button--primary" onClick={() => login()}>Entrar</button>}
      {(state.kind === 'error' || (state.kind === 'loading' && slow)) && <button type="button" className="button--primary" onClick={() => retry?.()}>Tentar novamente</button>}
    </div>
  </section>
}
