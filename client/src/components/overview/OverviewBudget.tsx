import type { OverviewBudget as BudgetData, OverviewBudgetTarget } from '../../api/overview'
import type { BudgetGroup } from '../budget/budgetText'
import { barWidth, percentNow, budgetStatusLabels, money, monthTitle, percentText } from './overviewText'

const VISIBLE_CATEGORIES = 4

const reasonLabels: Record<string, string> = {
  EstimatedCompetence: 'competência de cartão estimada',
  UndefinedNature: 'há despesas sem tipo definido',
  NoCards: 'nenhum cartão cadastrado',
}

/**
 * Orçamento do mês (R03): despesas fixas, todos os cartões e as metas por categoria, cada uma com limite, consumido e disponível. As perspectivas contam os
 * mesmos gastos: aparecem à parte e nada aqui é somado num total do orçamento. Cada linha abre o orçamento naquele grupo.
 */
export function OverviewBudget({ competence, budget, onOpenBudget }: { competence: string; budget: BudgetData; onOpenBudget: (group?: BudgetGroup) => void }) {
  const { categories } = budget
  const hidden = Math.max(0, categories.targets.length - VISIBLE_CATEGORIES)
  const hasAny = budget.hasBudget && (budget.fixed !== null || budget.cards !== null || categories.count > 0)
  return <section className="ov-card ov-budget" aria-labelledby="ov-budget-title">
    <header><div><h3 id="ov-budget-title">Orçamento de {monthTitle(competence)}</h3>
      <p className="ov-meta">Limite, consumido e disponível de cada meta</p></div>
      <button type="button" onClick={() => onOpenBudget()}>{hasAny ? 'Abrir orçamento' : 'Definir metas'}</button></header>
    {budget.state === 'Unavailable' ? <p className="ov-empty" role="alert">Não foi possível carregar o orçamento deste mês. Tente atualizar a página.</p>
      : !hasAny ? <p className="ov-empty">Nenhuma meta definida para {monthTitle(competence)}.</p>
        : <>
          {categories.exceededCount > 0 && <p className="ov-notice ov-notice--warning" role="status">{categories.exceededCount} meta(s) de categoria excedida(s) neste mês.</p>}
          {budget.fixed && <Limit title="Despesas fixas" target={budget.fixed} onOpen={() => onOpenBudget('fixed')} />}
          {budget.cards && <Limit title="Todos os cartões" target={budget.cards} note={`Faturas com vencimento em ${monthTitle(competence)}`} onOpen={() => onOpenBudget('cards')} />}
          {categories.count > 0 && <div className="ov-budget__categories">
            <div className="ov-limit__head"><strong>Categorias</strong>
              <span className="ov-meta">{categories.count} meta(s) · consumido {money(categories.consumed)} de {money(categories.planned)}</span></div>
            <ul className="ov-budget__list">{categories.targets.slice(0, VISIBLE_CATEGORIES).map(target => <li key={target.targetId}><Limit title={target.name} target={target} onOpen={() => onOpenBudget('categories')} /></li>)}</ul>
            {hidden > 0 && <p className="ov-meta">+ {hidden} meta(s) no orçamento completo.</p>}
          </div>}
          <p className="ov-meta ov-meta--note">Fixas, cartões e categorias olham para os mesmos gastos por ângulos diferentes: seus valores não são somados.</p>
        </>}
  </section>
}

function Limit({ title, target, note, onOpen }: { title: string; target: OverviewBudgetTarget; note?: string; onOpen: () => void }) {
  const exceeded = target.status === 'Exceeded'
  const incomplete = target.reasons.map(reason => reasonLabels[reason]).filter(Boolean)
  return <div className={`ov-limit ov-limit--${target.status}`}>
    <div className="ov-limit__head"><strong>{title}</strong><span className={`ov-tag${exceeded ? ' ov-tag--danger' : target.status === 'WithinBudget' ? '' : ' ov-tag--warning'}`}>{budgetStatusLabels[target.status]}</span></div>
    {note && <p className="ov-meta">{note}</p>}
    <div className="ov-bar-track" role="progressbar" aria-label={`Consumo de ${title}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(target.percent)}
      aria-valuetext={percentText(target.percent) ?? 'sem percentual'}>
      <span style={{ width: barWidth(target.percent) }} /></div>
    <p className="ov-limit__values"><span>{money(target.consumed)} de {money(target.planned)}{target.percent !== null && <> · {percentText(target.percent)}</>}</span>
      <span>{exceeded ? `Excedido em ${money(target.exceeded)}` : `Disponível ${money(target.available)}`}</span></p>
    {target.pending !== '0.00' && <p className="ov-limit__detail">Previsto adicional {money(target.pending)} · disponível projetado {money(target.availableProjected)}</p>}
    {incomplete.length > 0 && <p className="ov-limit__detail" role="status">Dados incompletos: {incomplete.join('; ')}.</p>}
    <button type="button" className="button--link" onClick={onOpen}>Abrir composição</button>
  </div>
}
