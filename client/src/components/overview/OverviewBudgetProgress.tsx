import type { BudgetControl, BudgetControlGroup } from '../../api/overview'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { groupTitle, originText } from './budgetControlText'
import { barWidth, isPositiveAmount, money, monthTitle, percentNow, percentText } from './overviewText'

// Dashboard Folio: progresso do orçamento recorrente do mês. A meta é a efetiva do resolvedor único (padrão herdado, personalizada no mês ou registro histórico); os grupos
// cartões e fora dos cartões são exclusivos. Percentuais, margem e excesso vêm do servidor; o detalhe (categorias, fixas, composição) fica na página Orçamento.

export function OverviewBudgetProgress({ control, loading, error, retry, onOpenBudget, onShowAll }: {
  control: BudgetControl | null; loading: boolean; error: unknown; retry: () => void; onOpenBudget: () => void; onShowAll: () => void
}) {
  return <section className="ov-card ov-budget" aria-labelledby="ov-budget-title">
    <header className="ov-card__head"><h3 id="ov-budget-title">Orçamento do mês</h3>
      <button type="button" className="ov-link" onClick={onOpenBudget}>Ver orçamento ›</button></header>
    {error ? <OperationalError error={error} onRetry={retry} retryLabel="Recarregar" />
      : !control ? loading ? <OperationalLoading>Carregando o orçamento…</OperationalLoading> : <p className="ov-empty">O consumo do orçamento não está disponível.</p>
        : <Body control={control} onOpenBudget={onOpenBudget} onShowAll={onShowAll} />}
  </section>
}

function Body({ control, onOpenBudget, onShowAll }: { control: BudgetControl; onOpenBudget: () => void; onShowAll: () => void }) {
  const { total } = control
  const groups = [control.cards, control.outsideCards].filter(group => group.state !== 'NoCards')
  if (!control.targetsAttributable) return <>
    <p className="ov-budget__headline"><strong>Meta global</strong></p>
    <p className="ov-meta">A meta mensal vale para todas as contas juntas.</p>
    <ul className="ov-budget__rows">{groups.map(group => <li key={group.key}><div className="ov-budget__row"><span>{groupTitle[group.key]}</span><span className="ov-mono">{money(group.realized)}</span></div></li>)}</ul>
    <button type="button" className="ov-link" onClick={onShowAll}>Ver todas as contas ›</button>
  </>
  if (!total.targetDefined) return <>
    <p className="ov-budget__headline" data-unavailable><strong>Meta não configurada</strong></p>
    <p className="ov-meta">Defina uma meta mensal para acompanhar o consumo do mês.</p>
    <button type="button" onClick={onOpenBudget}>Configurar orçamento</button>
  </>
  const origin = originText(control.totalTargetOrigin, control.totalTargetFrom, monthTitle)
  const over = total.excess !== null && isPositiveAmount(total.excess)
  return <>
    <p className="ov-budget__headline"><strong>{percentText(total.realizedPercent) ?? '—'}</strong> <span className="ov-meta">usado · meta {money(total.target)}</span></p>
    {origin && <p className="ov-meta ov-budget__origin">{origin}</p>}
    <ul className="ov-budget__rows">{groups.map(group => <GroupRow key={group.key} group={group} />)}</ul>
    <p className={`ov-budget__foot${over ? ' ov-negative' : ''}`}>
      {over ? `Excesso projetado de ${money(total.excess)}` : total.margin !== null ? `Margem projetada de ${money(total.margin)}` : null}
      {total.projectedPercent !== null && <span className="ov-meta"> · {percentText(total.projectedPercent)} projetado</span>}</p>
  </>
}

function GroupRow({ group }: { group: BudgetControlGroup }) {
  const title = groupTitle[group.key]
  const over = group.excess !== null && isPositiveAmount(group.excess)
  return <li className={over ? 'is-over' : undefined}>
    <div className="ov-budget__row"><span>{title}</span>
      <span className="ov-mono">{money(group.realized)}{group.target !== null && <small> / {money(group.target)}</small>}</span></div>
    {group.target !== null && <div className="ov-track" role="progressbar" aria-label={`Consumo de ${title}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(group.realizedPercent)}
      aria-valuetext={`${percentText(group.realizedPercent) ?? 'sem percentual'} usado, ${percentText(group.projectedPercent) ?? 'sem percentual'} projetado`}>
      <span className="ov-track__projected" style={{ width: barWidth(group.projectedPercent) }} />
      <span className="ov-track__done" style={{ width: barWidth(group.realizedPercent) }} /></div>}
    {group.target === null && <p className="ov-meta">Meta não configurada</p>}
  </li>
}
