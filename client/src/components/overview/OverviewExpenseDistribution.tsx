import { useCallback, useEffect, useRef, useState } from 'react'
import { getExpenseDistribution, type ExpenseCategoryShare, type ExpenseDistribution } from '../../api/overview'
import type { PlanActualScopeChoice } from '../../api/planActual'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { isNegative, money, percentText } from './overviewText'
import { useLoad } from './useLoad'

// Dashboard Folio — Despesas por categoria. O servidor agrupa os gastos efetivados por categoria principal, ordena, separa as cinco maiores, "Outras categorias" e "Sem categoria", e calcula
// total e percentuais (que somam 100,0); aqui só se desenha a barra empilhada (larguras = percentuais do servidor) e a lista. "Ver todas" busca a distribuição completa sob demanda.

const PALETTE = 7

/** Cor pela posição (como no mockup: a maior categoria em verde-azulado, a segunda em índigo…), a mesma na barra e na lista. "Outras" e "Sem categoria" são neutras. */
function colorsOf(rows: ExpenseCategoryShare[]): string[] {
  let next = 0
  return rows.map(row => row.kind === 'Others' ? 'other' : row.kind === 'Uncategorized' ? 'none' : String(next++ % PALETTE + 1))
}

function Rows({ rows }: { rows: ExpenseCategoryShare[] }) {
  const colors = colorsOf(rows)
  return <ul className="ov-distribution__list">{rows.map((row, index) => <li key={row.categoryId ?? row.kind}>
    <span className={`ov-swatch ov-cat-${colors[index]}`} aria-hidden="true" />
    <span className="ov-distribution__name">{row.name}{row.kind === 'Others' && <span className="visually-hidden"> ({row.categoryCount} categorias)</span>}</span>
    <span className="ov-mono ov-distribution__amount">{money(row.gross)}{row.amount !== row.gross && <small className="ov-distribution__net">líquido {money(row.amount)}</small>}</span>
    {row.percentage !== null && <span className="ov-distribution__pct">{percentText(row.percentage)}</span>}
  </li>)}</ul>
}

function Stack({ rows }: { rows: ExpenseCategoryShare[] }) {
  if (rows.length === 0 || rows.some(row => row.percentage === null)) return null
  const colors = colorsOf(rows)
  return <div className="ov-stack" aria-hidden="true">{rows.map((row, index) => <span key={row.categoryId ?? row.kind} className={`ov-cat-${colors[index]}`} style={{ flexGrow: 0, flexBasis: `${row.percentage}%` }} />)}</div>
}

const basisText = {
  Bank: 'Banco: o indicador abate toda devolução recebida, e esta lista só as vinculadas a uma despesa.',
  Cards: 'Cartões: o indicador usa o total do banco nas faturas fechadas, e esta lista soma as compras e ajustes lançados.',
} as const

/** Diferença com "Despesas do mês": quanto e em qual bloco, com a razão financeira de cada bloco (nunca uma categoria de compensação). */
function Notes({ distribution }: { distribution: ExpenseDistribution }) {
  if (!distribution.reasons.includes('TotalDiffersFromExpenses') && !distribution.reasons.includes('ReferenceUnavailable')) return null
  if (distribution.reasons.includes('ReferenceUnavailable')) return <p className="ov-note" role="status">Resultado parcial: não foi possível conferir o total com as despesas do mês.</p>
  const buckets = (distribution.reconciliation ?? []).filter(item => item.difference !== '0.00')
  return <div className="ov-note" role="status">
    <p>Resultado parcial: o total difere das despesas do mês ({money(distribution.reference)}).</p>
    <ul>{buckets.map(item => <li key={item.bucket}>{basisText[item.bucket]} Aqui {money(item.distributed)}, no indicador {money(item.reference)}.</li>)}</ul>
  </div>
}

function Body({ distribution, monthLabel }: { distribution: ExpenseDistribution; monthLabel: string }) {
  if (distribution.state === 'Unavailable') return <p className="ov-empty" role="alert">Não foi possível agrupar as despesas deste mês.</p>
  return <>
    <div className="ov-distribution__total"><span className="ov-meta">Total gasto</span>
      <strong className={`ov-mono${isNegative(distribution.total) ? ' ov-negative' : ''}`}>{money(distribution.total)}</strong></div>
    {distribution.categories.length === 0 ? <p className="ov-empty">Nenhuma despesa efetivada em {monthLabel}.</p> : <><Stack rows={distribution.categories} /><Rows rows={distribution.categories} />
      {distribution.adjustments !== '0.00' && <p className="ov-distribution__adjust"><span>Devoluções e estornos</span><span className="ov-mono ov-negative">{money(distribution.adjustments)}</span></p>}</>}
    <Notes distribution={distribution} />
  </>
}

export function OverviewExpenseDistribution({ workspaceId, competence, scope, monthLabel, scopeText, distribution, loading, error, retry }: {
  workspaceId: string; competence: string; scope: PlanActualScopeChoice; monthLabel: string; scopeText: string; distribution: ExpenseDistribution | null; loading: boolean; error: unknown; retry: () => void
}) {
  const [open, setOpen] = useState(false)
  const trigger = useRef<HTMLButtonElement>(null)
  const close = useCallback(() => { setOpen(false); trigger.current?.focus() }, [])
  return <section className="ov-card ov-distribution" aria-labelledby="ov-distribution-title">
    <header className="ov-card__head"><div><h3 id="ov-distribution-title">Despesas por categoria</h3><p className="ov-meta">{scopeText ? `${scopeText} · ` : ''}Somente gastos já efetivados</p></div>
      {distribution && distribution.state !== 'Unavailable' && distribution.categories.length > 0
        && <button ref={trigger} type="button" className="ov-link" aria-haspopup="dialog" onClick={() => setOpen(true)}>Ver todas ›</button>}</header>
    {error ? <OperationalError error={error} onRetry={retry} retryLabel="Recarregar" />
      : !distribution ? loading ? <OperationalLoading>Carregando as despesas…</OperationalLoading> : <p className="ov-empty">As despesas por categoria não estão disponíveis.</p>
        : <Body distribution={distribution} monthLabel={monthLabel} />}
    {open && <AllCategories workspaceId={workspaceId} competence={competence} scope={scope} monthLabel={monthLabel} onClose={close} />}
  </section>
}

/** Distribuição completa da mesma competência e escopo, só quando pedida. Diálogo modal: foco no fechar, Esc fecha e o foco volta ao "Ver todas". */
function AllCategories({ workspaceId, competence, scope, monthLabel, onClose }: { workspaceId: string; competence: string; scope: PlanActualScopeChoice; monthLabel: string; onClose: () => void }) {
  const { load, retry } = useLoad(`${workspaceId}|${competence}|${scope.kind}:${scope.accountId ?? ''}`,
    useCallback((signal: AbortSignal) => getExpenseDistribution(workspaceId, competence, scope, signal), [workspaceId, competence, scope]))
  const closeButton = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    closeButton.current?.focus()
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])
  return <div className="ov-dialog" onClick={event => { if (event.target === event.currentTarget) onClose() }}>
    <div className="ov-dialog__panel" role="dialog" aria-modal="true" aria-labelledby="ov-all-categories-title">
      <header className="ov-card__head"><div><h3 id="ov-all-categories-title">Todas as categorias</h3><p className="ov-meta">{monthLabel} · somente gastos já efetivados</p></div>
        <button ref={closeButton} type="button" className="ov-link" onClick={onClose}>Fechar</button></header>
      {load.state === 'loaded' ? <Body distribution={load.data} monthLabel={monthLabel} />
        : load.state === 'error' ? <OperationalError error={load.error} onRetry={retry} retryLabel="Recarregar" /> : <OperationalLoading>Carregando todas as categorias…</OperationalLoading>}
    </div>
  </div>
}
