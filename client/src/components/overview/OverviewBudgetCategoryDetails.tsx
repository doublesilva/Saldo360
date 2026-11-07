import { useCallback, useState } from 'react'
import { getBudgetCategories, type BudgetCategories, type BudgetCategoryLine } from '../../api/overview'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { isNegative, money } from './overviewText'
import { useLoad } from './useLoad'

// Detalhamento inline da linha "Meta total": para onde vai o total previsto PF, por categoria principal, em efetivado, comprometido e estimado. Só carrega ao expandir. Tudo (valores, ordem,
// agrupamento em "Outras categorias", componentes não alocados e conciliação com o total previsto) vem do servidor: o navegador só desenha. Estimativas não têm categoria e aparecem à parte.

const sourceText: Record<string, string> = { Cards: 'Cartões', Bank: 'Fora dos cartões', Fixed: 'Despesas fixas', Variable: 'Despesas variáveis', Undefined: 'Tipo não definido' }
const hasValue = (amount: string) => amount !== '0.00'

/** Barra de composição da linha: larguras proporcionais aos próprios valores (resolvidas pelo CSS), só para o que é positivo. */
function Composition({ line }: { line: BudgetCategoryLine }) {
  const parts = [['realized', line.realized], ['committed', line.committed], ['estimated', line.estimated]] as const
  if (parts.every(([, amount]) => isNegative(amount) || !hasValue(amount)) || isNegative(line.projected)) return null
  return <span className="ov-catfc__bar" aria-hidden="true">{parts.filter(([, amount]) => !isNegative(amount) && hasValue(amount)).map(([key, amount]) => <span key={key} className={`ov-catfc__seg ov-catfc__seg--${key}`} style={{ flexGrow: amount, flexBasis: 0 }} />)}</span>
}

function Line({ line, open, onToggle, id }: { line: BudgetCategoryLine; open: boolean; onToggle: () => void; id: string }) {
  const sources = line.sources.filter(source => hasValue(source.amount))
  return <li className="ov-catfc__item">
    <button type="button" className="ov-catfc__row" aria-expanded={open} aria-controls={id} onClick={onToggle}>
      <span className="ov-catfc__name"><span className="ov-catfc__chevron" aria-hidden="true">{open ? '▴' : '▾'}</span>{line.name}{line.kind === 'Others' && <span className="visually-hidden"> ({line.categoryCount} categorias)</span>}</span>
      <span className={`ov-mono ov-catfc__amount${isNegative(line.projected) ? ' ov-negative' : ''}`}>{money(line.projected)}</span>
      <Composition line={line} />
    </button>
    {open && <div id={id} className="ov-catfc__detail">
      <dl className="ov-catfc__parts">
        <div><dt>Efetivado</dt><dd className="ov-mono">{money(line.realized)}</dd></div>
        <div><dt>Comprometido</dt><dd className="ov-mono">{money(line.committed)}</dd></div>
        <div><dt>Estimado</dt><dd className="ov-mono">{money(line.estimated)}</dd></div>
      </dl>
      {line.children.length > 1 && <ul className="ov-catfc__children" aria-label={`Subcategorias de ${line.name}`}>{line.children.map(child => <li key={child.categoryId ?? child.name}><span>{child.name}</span><span className="ov-mono">{money(child.projected)}</span></li>)}</ul>}
      <p className="ov-meta">Como foi pago ou classificado (cada grupo soma o total da categoria):</p>
      <ul className="ov-catfc__children" aria-label={`Composição de ${line.name}`}>{sources.map(source => <li key={source.group + source.key}><span>{sourceText[source.key] ?? source.key}</span><span className="ov-mono">{money(source.amount)}</span></li>)}</ul>
    </div>}
  </li>
}

function Body({ data, panelId }: { data: BudgetCategories; panelId: string }) {
  const [all, setAll] = useState(false)
  const [openLine, setOpenLine] = useState<string | null>(null)
  const lines = all ? data.allCategories : data.categories
  const keyOf = (line: BudgetCategoryLine) => line.categoryId ?? line.kind
  return <>
    <div className="ov-catfc__head"><strong>Previsão por categoria</strong><span>Total previsto</span></div>
    <ul className="ov-catfc__legend" aria-label="Legenda"><li><i className="ov-catfc__seg--realized" />Efetivado</li><li><i className="ov-catfc__seg--committed" />Comprometido</li><li><i className="ov-catfc__seg--estimated" />Estimado</li></ul>
    {lines.length === 0 ? <p className="ov-meta">Nenhuma despesa categorizada nesta competência.</p>
      : <ul className="ov-catfc__list">{lines.map(line => <Line key={keyOf(line)} line={line} id={`${panelId}-${keyOf(line)}`} open={openLine === keyOf(line)} onToggle={() => setOpenLine(current => current === keyOf(line) ? null : keyOf(line))} />)}</ul>}
    {data.categoryCount > data.categories.filter(line => line.kind === 'Category').length && <button type="button" className="ov-catfc__all" aria-expanded={all} onClick={() => setAll(value => !value)}>{all ? 'Mostrar menos' : 'Ver todas as categorias'}</button>}
    {(hasValue(data.unallocatedEstimated) || hasValue(data.unallocatedRealized) || hasValue(data.unallocatedCommitted)) && <dl className="ov-catfc__unallocated">
      {hasValue(data.unallocatedEstimated) && <div><dt>Estimativas não alocadas a categorias</dt><dd className="ov-mono">{money(data.unallocatedEstimated)}</dd></div>}
      {hasValue(data.unallocatedRealized) && <div><dt>Efetivado não discriminado por categoria</dt><dd className="ov-mono">{money(data.unallocatedRealized)}</dd></div>}
      {hasValue(data.unallocatedCommitted) && <div><dt>Comprometido não discriminado por categoria</dt><dd className="ov-mono">{money(data.unallocatedCommitted)}</dd></div>}
    </dl>}
    <p className="ov-catfc__total"><span>Total previsto PF</span><strong className="ov-mono">{money(data.projected)}</strong></p>
    {data.state === 'Partial' && <p className="ov-note" role="status">Conciliação parcial: parte do total ainda não pôde ser atribuída a categorias; os valores acima são preservados à parte, sem estimativas inventadas.</p>}
  </>
}

export function OverviewBudgetCategoryDetails({ workspaceId, competence, id }: { workspaceId: string; competence: string; id: string }) {
  const { load, retry } = useLoad(`${workspaceId}|${competence}`, useCallback((signal: AbortSignal) => getBudgetCategories(workspaceId, competence, signal), [workspaceId, competence]))
  return <div id={id} className="ov-catfc">
    {load.state === 'loaded' ? <Body data={load.data} panelId={id} />
      : load.state === 'error' ? <OperationalError error={load.error} onRetry={retry} retryLabel="Recarregar" /> : <OperationalLoading>Carregando as categorias…</OperationalLoading>}
  </div>
}
