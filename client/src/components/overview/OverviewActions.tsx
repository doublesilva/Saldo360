import { useState } from 'react'
import type { DetailKey } from './Disclosure'
import type { OverviewNavigation, OverviewSection } from './Overview'
import { VISIBLE_ITEMS, visibleItems, type PriorityAction, type PriorityItem } from './priorityItems'

// T12.2: "Onde preciso agir". Até três itens por prioridade (os de risco de caixa ou da meta nunca ficam escondidos), cada um com descrição curta, impacto conhecido,
// ação objetiva e acesso ao detalhe. Avisos técnicos vêm agrupados por consequência; as causas ficam em expansão.

type Go = (section: OverviewSection, context?: OverviewNavigation) => void
const rankText = { 1: 'Risco de caixa ou da meta', 2: 'Orçamento', 3: 'Confiabilidade dos dados' } as const

export function OverviewActionsSection({ items, loading, onNavigate, onReload, onOpenDetail }: {
  items: PriorityItem[]; loading: boolean; onNavigate: Go; onReload: () => void; onOpenDetail: (key: DetailKey) => void
}) {
  const [expanded, setExpanded] = useState(false)
  const shown = visibleItems(items, expanded)
  const hidden = items.length - shown.length
  const run = (action: PriorityAction) => { if (action.reload) onReload(); else if (action.to) onNavigate(action.to.section, action.to.context) }
  return <section className="ov-actions" aria-labelledby="ov-actions-title">
    <header className="ov-area__head"><h2 id="ov-actions-title">Onde preciso agir</h2></header>
    {items.length === 0
      ? <p className="ov-card ov-empty" role="status">{loading ? 'Verificando alertas e recomendações…' : 'Nenhuma ação necessária com os dados disponíveis.'}</p>
      : <ol className="ov-actions__list">{shown.map(item => <li key={item.key} className={`ov-card ov-action ov-action--${item.rank}`} aria-label={item.title}>
        <p className="ov-action__kind"><span className={`ov-tag${item.rank === 1 ? ' ov-tag--danger' : item.rank === 2 ? ' ov-tag--warning' : ''}`}>{rankText[item.rank]}</span></p>
        <p className="ov-action__title"><strong>{item.title}</strong></p>
        <p className="ov-action__impact">{item.impact}</p>
        <div className="ov-action__buttons">
          {item.action && <button type="button" className="button--primary" onClick={() => run(item.action as PriorityAction)}>{item.action.label}</button>}
          {item.detail && <button type="button" onClick={() => onOpenDetail(item.detail as DetailKey)}>Ver detalhe</button>}
        </div>
        {item.causes && <details className="ov-details"><summary>Ver causas ({item.causes.length})</summary>
          <ul className="ov-action__causes">{item.causes.map(cause => <li key={cause.key}><span>{cause.text}</span>
            {cause.action && <button type="button" className="button--link" onClick={() => run(cause.action as PriorityAction)}>{cause.action.label}</button>}</li>)}</ul></details>}
      </li>)}</ol>}
    {(hidden > 0 || expanded && items.length > VISIBLE_ITEMS) && <button type="button" className="ov-actions__more" aria-expanded={expanded} onClick={() => setExpanded(value => !value)}>
      {expanded ? 'Mostrar menos' : `Ver todas as recomendações (${items.length})`}</button>}
  </section>
}
