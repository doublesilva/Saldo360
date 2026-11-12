import { useState } from 'react'
import type { Go } from './Overview'
import { LoadingNote, Skeleton } from './OverviewSkeleton'
import { severityText, visibleItems, type PriorityAction, type PriorityItem } from './priorityItems'

// Próximas ações: logo abaixo dos filtros, até três decisões — o que precisa de atenção (título), por quê (evidência) e onde resolver (ação). Os demais ficam atrás de
// "Ver todos", sem mudar a ordem. A gravidade vem em texto além da cor. Cada fonte carrega e falha de forma independente: o que já está confirmado aparece enquanto o resto carrega.

export interface AlertsState { loading: boolean; failed: boolean }

export function OverviewAlerts({ items, state, onNavigate, onReload }: { items: PriorityItem[]; state: AlertsState; onNavigate: Go; onReload: () => void }) {
  const [expanded, setExpanded] = useState(false)
  const shown = visibleItems(items, expanded)
  const hidden = items.length - shown.length
  const run = (action: PriorityAction) => { if (action.reload) onReload(); else if (action.to) onNavigate(action.to.section, action.to.context) }
  return <section className="ov-alerts" aria-labelledby="ov-alerts-title" aria-busy={state.loading || undefined}>
    <h3 id="ov-alerts-title" className="ov-alerts__title">Próximas ações</h3>
    {items.length > 0 && <ul className="ov-alerts__list">{shown.map(item => <li key={item.key} className={`ov-alert ov-alert--${item.rank}`}>
      <p className="ov-alert__head"><span className="ov-alert__severity">{severityText[item.rank]}</span> <strong>{item.title}</strong></p>
      <p className="ov-alert__evidence">{item.impact}</p>
      {item.action && <button type="button" className={item.rank === 1 ? 'button--primary' : 'ov-link'} onClick={() => run(item.action as PriorityAction)}>{item.action.label}{item.rank === 1 ? '' : ' ›'}</button>}
    </li>)}</ul>}
    {state.loading && (items.length === 0
      ? <div className="ov-alerts__loading"><LoadingNote>Verificando pontos de atenção</LoadingNote><Skeleton width="min(22rem, 90%)" height="1rem" /><Skeleton width="min(14rem, 60%)" height=".8rem" /></div>
      : <p className="ov-meta" role="status">Carregando mais sinais…</p>)}
    {state.failed && !state.loading && <p className="ov-meta ov-alerts__failed" role="alert">Não foi possível carregar todos os sinais, então esta lista pode estar incompleta. <button type="button" className="ov-link" onClick={onReload}>Tentar novamente</button></p>}
    {!state.loading && !state.failed && items.length === 0 && <p className="ov-empty ov-alerts__empty">Nenhuma ação prioritária identificada com os dados atuais.</p>}
    {(hidden > 0 || expanded) && <button type="button" className="ov-link ov-alerts__more" aria-expanded={expanded} onClick={() => setExpanded(value => !value)}>
      {expanded ? 'Mostrar menos' : `Ver todas as ${items.length} ações`}</button>}
  </section>
}
