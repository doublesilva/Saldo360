import type { OverviewBudget as BudgetData } from '../../api/overview'
import type { PlanCards } from '../../api/planActual'
import { barWidth, percentNow, budgetStatusLabels, money, monthTitle, percentText } from './overviewText'

const VISIBLE_TARGETS = 6

/**
 * Orçamento do mês: metas por categoria atualmente cadastradas, cada uma com o seu consumo. A meta dos cartões é uma perspectiva própria sobre os mesmos
 * gastos: aparece à parte e nada aqui é somado num total global (as perspectivas se sobrepõem).
 */
export function OverviewBudget({ competence, budget, cards, onOpenBudget }: { competence: string; budget: BudgetData; cards: PlanCards | null; onOpenBudget: () => void }) {
  const targets = budget.targets
  const hidden = Math.max(0, targets.length - VISIBLE_TARGETS)
  const showCards = cards !== null && cards.applicable && cards.plannedState === 'Defined' && cards.target !== null
  return <section className="ov-card ov-budget" aria-labelledby="ov-budget-title">
    <header><div><h3 id="ov-budget-title">Orçamento de {monthTitle(competence)}</h3>
      <p className="ov-meta">Metas por categoria · realizado e comprometido do mês</p></div>
      <button type="button" onClick={onOpenBudget}>{budget.hasBudget ? 'Abrir orçamento' : 'Definir orçamento'}</button></header>
    {budget.state === 'Unavailable' ? <p className="ov-empty" role="alert">Não foi possível carregar o orçamento deste mês. Tente atualizar a página.</p>
      : !budget.hasBudget && !showCards ? <p className="ov-empty">Nenhum orçamento definido para {monthTitle(competence)}.</p>
        : <>
          {budget.hasBudget && budget.exceededCount > 0 && <p className="ov-notice ov-notice--warning" role="status">{budget.exceededCount} meta(s) excedida(s) neste mês.</p>}
          {budget.hasBudget && <ul className="ov-budget__list">{targets.slice(0, VISIBLE_TARGETS).map(target => {
            const exceeded = target.status === 'Exceeded'
            return <li key={target.targetId} className={`ov-limit ov-limit--${target.status}`}>
              <div className="ov-limit__head"><strong>{target.categoryName}</strong><span className={`ov-tag${exceeded ? ' ov-tag--danger' : target.status === 'WithinBudget' ? '' : ' ov-tag--warning'}`}>{budgetStatusLabels[target.status]}</span></div>
              <div className="ov-bar-track" role="progressbar" aria-label={`Consumo de ${target.categoryName}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(target.percent)} aria-valuetext={`${percentText(target.percent) ?? 'sem percentual'} do limite`}>
                <span style={{ width: barWidth(target.percent) }} /></div>
              <p className="ov-limit__values"><span>{money(target.projected)} de {money(target.planned)}{target.percent !== null && <> · {percentText(target.percent)}</>}</span>
                <span>{exceeded ? `Excedido em ${money(target.exceeded)}` : `Disponível ${money(target.available)}`}</span></p>
              <p className="ov-limit__detail">Realizado {money(target.realized)}{target.committed !== '0.00' && <> · comprometido {money(target.committed)}</>}</p>
            </li>
          })}</ul>}
          {hidden > 0 && <p className="ov-meta">+ {hidden} meta(s) no orçamento completo.</p>}
          {showCards && cards && <div className="ov-limit ov-limit--cards">
            <div className="ov-limit__head"><strong>Cartões</strong><span className="ov-tag">Meta própria</span></div>
            <div className="ov-bar-track" role="progressbar" aria-label="Consumo da meta dos cartões" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(cards.confirmedPercent)} aria-valuetext={`${percentText(cards.confirmedPercent) ?? 'sem percentual'} da meta`}>
              <span style={{ width: barWidth(cards.confirmedPercent) }} /></div>
            <p className="ov-limit__values"><span>{money(cards.confirmed)} de {money(cards.target)}{cards.confirmedPercent !== null && <> · {percentText(cards.confirmedPercent)}</>}</span>
              <span>{cards.excessProjected && cards.excessProjected !== '0.00' ? `Projeção excede em ${money(cards.excessProjected)}` : `Projetado ${money(cards.projected)}`}</span></p>
          </div>}
          <p className="ov-meta ov-meta--note">A meta dos cartões e as metas por categoria olham para os mesmos gastos: seus valores não são somados.</p>
        </>}
  </section>
}
