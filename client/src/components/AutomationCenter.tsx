import { useCallback, useEffect, useState } from 'react'
import { getAutomationOverview, type AutomationOverview } from '../api/pluggyAutomation'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import { AutomationCards } from './AutomationCards'
import { AutomationConnections } from './AutomationConnections'
import { AutomationHistory } from './AutomationHistory'
import { ClassificationReviewList } from './ClassificationReviewList'
import type { ReviewKind } from '../api/pluggyAutomation'
import '../Automation.css'

export type AutomationTab = 'connections' | 'automations' | 'history'

/** Ponto de entrada de outras telas (orçamento, visão geral): abre direto a lista de revisão da classificação, no recorte pedido. */
export interface AutomationEntry { key: number; tab?: AutomationTab; review?: { kind: ReviewKind; from?: string; to?: string } }

const tabs: readonly (readonly [AutomationTab, string])[] = [['connections', 'Conexões'], ['automations', 'Automações'], ['history', 'Histórico']]

type Load = { key: string; workspaceId: string; state: 'loading' } | { key: string; workspaceId: string; state: 'error'; error: unknown } | { key: string; workspaceId: string; state: 'loaded'; data: AutomationOverview }

/**
 * Central de automação Pluggy + agente de IA (R04). Não cria motor, agente nem agendador: monitora e comanda a sincronização, o agendador e a
 * categorização que já existem, e mostra o histórico real das execuções. Abrir a central nunca dispara uma execução.
 */
export function AutomationCenter({ workspaceId, refreshKey = '', onChanged, entry }: { workspaceId: string; refreshKey?: string; onChanged?: () => void; entry?: AutomationEntry }) {
  const [tab, setTab] = useState<AutomationTab>(entry?.tab ?? (entry?.review ? 'automations' : 'connections'))
  const [reload, setReload] = useState(0)
  const [historyKind, setHistoryKind] = useState<'' | 'Sync' | 'Categorization'>('')
  const key = `${workspaceId}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, workspaceId, state: 'loading' })
  // Ao recarregar depois de uma ação, a tela anterior continua visível (mensagens e foco não se perdem); só um orçamento diferente volta ao carregamento.
  const visible: Load = loaded.key === key || (loaded.state === 'loaded' && loaded.workspaceId === workspaceId) ? loaded : { key, workspaceId, state: 'loading' }

  useEffect(() => {
    const controller = new AbortController()
    getAutomationOverview(workspaceId, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, workspaceId, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, workspaceId, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, key])

  const refresh = useCallback(() => { setReload(value => value + 1); onChanged?.() }, [onChanged])
  const goHistory = (kind: '' | 'Sync' | 'Categorization') => { setHistoryKind(kind); setTab('history') }

  return <section className="family-section automation" aria-labelledby="automation-title">
    <div className="section-heading"><div><h3 id="automation-title">Automação</h3>
      <p className="meta">Acompanhe as conexões bancárias, a sincronização e a categorização com IA. Esta tela mostra o que já existe: não cria lançamentos por conta própria e abrir a central nunca inicia uma execução.</p></div></div>
    <div className="planning-hub__tabs" role="tablist" aria-label="Seções da automação">
      {tabs.map(([id, label]) => <button key={id} type="button" role="tab" id={`automation-tab-${id}`} aria-selected={tab === id} aria-controls="automation-panel" onClick={() => setTab(id)}>{label}</button>)}
    </div>
    <div id="automation-panel" role="tabpanel" aria-labelledby={`automation-tab-${tab}`}>
      {visible.state === 'loading' && tab !== 'history' && <OperationalLoading>Carregando a central de automação…</OperationalLoading>}
      {visible.state === 'error' && tab !== 'history' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
      {tab === 'connections' && visible.state === 'loaded' && <AutomationConnections workspaceId={workspaceId} refreshKey={refreshKey} overview={visible.data} onChanged={refresh} />}
      {tab === 'automations' && visible.state === 'loaded' && <>
        <AutomationCards workspaceId={workspaceId} overview={visible.data} onChanged={refresh} onOpenConnections={() => setTab('connections')} onOpenHistory={goHistory} />
        <ClassificationReviewList key={entry?.key} workspaceId={workspaceId} refreshKey={refreshKey} initial={entry?.review} onChanged={refresh} />
      </>}
      {tab === 'history' && <AutomationHistory key={historyKind} workspaceId={workspaceId} refreshKey={`${refreshKey}|${reload}`} initialKind={historyKind} />}
    </div>
  </section>
}
