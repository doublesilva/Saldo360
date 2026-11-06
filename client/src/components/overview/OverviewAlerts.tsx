import { useState } from 'react'
import type { Go } from './Overview'
import { visibleItems, type PriorityAction, type PriorityItem } from './priorityItems'

// Dashboard Folio: um único lugar para os avisos. Risco de caixa ou da meta (rank 1) fica sempre visível e em destaque; orçamento e confiabilidade dos dados ocupam uma
// linha cada e levam à tela onde se resolve. Sem textos longos: o detalhe está no módulo de destino.

export function OverviewAlerts({ items, onNavigate, onReload }: { items: PriorityItem[]; onNavigate: Go; onReload: () => void }) {
  const [expanded, setExpanded] = useState(false)
  if (items.length === 0) return null
  const shown = visibleItems(items, expanded)
  const hidden = items.length - shown.length
  const run = (action: PriorityAction) => { if (action.reload) onReload(); else if (action.to) onNavigate(action.to.section, action.to.context) }
  return <section className="ov-alerts" aria-label="Pontos de atenção">
    <ul>{shown.map(item => <li key={item.key} className={`ov-alert ov-alert--${item.rank}`}>
      <span className="ov-alert__dot" aria-hidden="true" />
      <p><strong>{item.title}</strong> <span>{item.impact}</span></p>
      {item.action && <button type="button" className={item.rank === 1 ? 'button--primary' : 'ov-link'} onClick={() => run(item.action as PriorityAction)}>{item.action.label}{item.rank === 1 ? '' : ' ›'}</button>}
    </li>)}</ul>
    {(hidden > 0 || expanded) && <button type="button" className="ov-link ov-alerts__more" aria-expanded={expanded} onClick={() => setExpanded(value => !value)}>
      {expanded ? 'Mostrar menos' : `Ver todos os ${items.length} pontos de atenção`}</button>}
  </section>
}
