import { useEffect, useState } from 'react'
import { listIntegrationConnections, listLinks, type AccountLink } from '../api/pluggyIntegration'
import type { PluggyConnection } from '../api/pluggyPoc'
import type { AutomationOverview } from '../api/pluggyAutomation'
import { connectionStates, dateTime, day } from '../pluggyFormat'
import { OperationalEmpty, OperationalError, OperationalLoading } from './OperationalFeedback'
import { AddConnectionForm, PluggyLab } from './PluggyLab'
import { PluggySyncPanel } from './PluggySyncPanel'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; connections: PluggyConnection[]; links: AccountLink[] }

/** Estados da instituição que exigem uma ação do usuário fora do Saldo360 (renovar a autorização no Meu Pluggy). */
const needsAuthorization = (state: string) => state === 'ActionRequired' || state === 'ConsentExpired'

const linkKindLabel = { Bank: 'Conta', Card: 'Cartão' } as const

/**
 * Conexões bancárias: instituição, estado informado pela Pluggy, contas e cartões vinculados, última atualização da instituição e última sincronização concluída
 * (cadastrada não é atualizada). Só mostra ações que existem: cadastrar o Item, sincronizar e inspecionar. Não há desconectar: a conexão é encerrada no Meu Pluggy.
 */
export function AutomationConnections({ workspaceId, refreshKey, overview, onChanged }: { workspaceId: string; refreshKey: string; overview: AutomationOverview; onChanged: () => void }) {
  const [reload, setReload] = useState(0)
  const [diagnostics, setDiagnostics] = useState(false)
  const available = overview.server.integrationAvailable
  const key = `${workspaceId}|${refreshKey}|${reload}|${available}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }

  useEffect(() => {
    if (!available) return
    const controller = new AbortController()
    Promise.all([listIntegrationConnections(workspaceId, controller.signal), listLinks(workspaceId, controller.signal)])
      .then(([connections, links]) => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', connections, links }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, key, available])

  if (!available) return <section className="card" aria-labelledby="automation-connections-title">
    <h4 id="automation-connections-title">Conexões bancárias</h4>
    <p className="notice" role="status">A integração bancária não está disponível neste ambiente (desligada ou sem credenciais configuradas). Nada é consultado enquanto isso.</p>
  </section>

  const changed = () => { setReload(value => value + 1); onChanged() }
  return <>
    <section className="card" aria-labelledby="automation-connections-title">
      <h4 id="automation-connections-title">Conexões bancárias</h4>
      {visible.state === 'loading' && <OperationalLoading>Carregando as conexões…</OperationalLoading>}
      {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
      {visible.state === 'loaded' && visible.connections.length === 0 && <OperationalEmpty>Nenhuma conexão cadastrada. Cadastre abaixo o itemId obtido no Meu Pluggy.</OperationalEmpty>}
      {visible.state === 'loaded' && visible.connections.length > 0 && <ul className="automation-connections">
        {visible.connections.map(connection => {
          const links = visible.links.filter(link => link.connectionId === connection.id)
          const name = connection.label ?? connection.institution ?? 'Conexão'
          const lastSync = links.map(link => link.lastSyncedAt).filter((value): value is string => value !== null).sort().at(-1) ?? null
          return <li key={connection.id} className="automation-connection" aria-label={`Conexão ${name}`}>
            <header>
              <strong>{name}</strong>{connection.label && connection.institution ? <span className="meta"> · {connection.institution}</span> : null}{' '}
              <span className={`badge ${needsAuthorization(connection.state) || connection.state === 'Error' ? 'badge--sync-failed' : 'badge--sync-completed'}`}>{connectionStates[connection.state] ?? connection.state}</span>
            </header>
            <dl className="card-overview__facts">
              <div><dt>Cadastrada em</dt><dd>{day(connection.createdAt)}</dd></div>
              <div><dt>Última atualização pela instituição</dt><dd>{day(connection.lastUpdatedAt)}</dd></div>
              <div><dt>Última sincronização concluída</dt><dd>{lastSync ? dateTime(lastSync) : 'Ainda não sincronizada'}</dd></div>
              <div><dt>Autorização válida até</dt><dd>{day(connection.consentExpiresAt)}</dd></div>
            </dl>
            {needsAuthorization(connection.state) && <p className="notice notice--warning" role="alert">Esta conexão precisa de autorização: renove-a no Meu Pluggy. Enquanto isso, nada é atualizado.</p>}
            {(connection.integrationError || connection.errorCode) && <p className="meta" role="status">{connection.integrationError ? `Falha de integração: ${connection.integrationError}` : `Código da instituição: ${connection.errorCode}`}</p>}
            {links.length === 0
              ? <p className="meta">Nenhuma conta ou cartão vinculado a esta conexão ainda. Vincule em Contas → Integração bancária.</p>
              : <ul className="automation-connection__links" aria-label={`Contas e cartões de ${name}`}>{links.map(link => <li key={link.id}>
                <span className="badge">{linkKindLabel[link.kind]}</span> {link.displayName}
                <span className="meta"> · {link.lastSyncedAt ? `sincronizada em ${dateTime(link.lastSyncedAt)}` : 'ainda não sincronizada'}{link.status !== 'Active' ? ` · ${link.status === 'ConsentExpired' ? 'autorização expirada' : 'requer atenção'}` : ''}</span>
              </li>)}</ul>}
          </li>
        })}
      </ul>}
      {visible.state === 'loaded' && <AddConnectionForm workspaceId={workspaceId} onAdded={changed} />}
      <p className="meta">Para encerrar uma conexão, use o Meu Pluggy: o Saldo360 não desconecta instituições por conta própria. Os lançamentos já importados permanecem.</p>
    </section>
    {visible.state === 'loaded' && visible.connections.length > 0 && <section className="card" aria-label="Sincronização por conexão">
      <PluggySyncPanel workspaceId={workspaceId} refreshKey={`${refreshKey}|${reload}`} onChanged={changed} />
    </section>}
    <details className="automation-diagnostics" onToggle={event => setDiagnostics(event.currentTarget.open)}>
      <summary>Diagnóstico técnico da Pluggy (avançado)</summary>
      {diagnostics && <PluggyLab workspaceId={workspaceId} embedded />}
    </details>
  </>
}
