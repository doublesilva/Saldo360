import type { BudgetAction, BudgetControl, BudgetControlGroup, BudgetControlTotal, BudgetGroupStatus } from '../../api/overview'
import type { BudgetGroup } from '../budget/budgetText'
import type { OverviewNavigation, OverviewSection } from './Overview'
import { actionText, groupStateText, groupTitle, impactText, noteText, pressureText, qualityText, reasonText, recommendationText, referenceText, statusText } from './budgetControlText'
import { barWidth, isPositiveAmount, money, percentNow, percentText } from './overviewText'

// T11.2: consumo do orçamento, principais pressões e ações. Tudo vem do servidor: o React apresenta valores, estados e destinos, sem calcular.

type Go = (section: OverviewSection, context?: OverviewNavigation) => void

function Tag({ status }: { status: BudgetGroupStatus }) {
  return <span className={`ov-tag${status === 'Exceeded' ? ' ov-tag--danger' : status === 'WithinBudget' ? ' ov-tag--ok' : ''}`}>{statusText[status]}</span>
}

function Meter({ label, percent }: { label: string; percent: string | null }) {
  return percent === null ? null : <div className="ov-bar-track" role="progressbar" aria-label={`Consumo projetado de ${label}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(percent)}
    aria-valuetext={percentText(percent) ?? ''}><span style={{ width: barWidth(percent) }} /></div>
}

function Lines({ item, cards }: { item: BudgetControlGroup | BudgetControlTotal; cards?: boolean }) {
  const target = item.target
  return <dl className="ov-cash__parts">
    <div><dt>Orçamento definido</dt><dd data-unavailable={target === null || undefined}>{target === null ? 'Não definido' : money(target)}</dd></div>
    <div><dt>Despesas realizadas</dt><dd>{money(item.realized)}</dd></div>
    <div><dt>Comprometidas, não realizadas</dt><dd>{money(item.committed)}</dd></div>
    <div><dt>{cards ? 'Estimadas (parcelas e provisão alimentar)' : 'Estimadas complementares'}</dt><dd>{money(item.estimated)}</dd></div>
    <div><dt>Consumo total projetado</dt><dd>{money(item.projected)}</dd></div>
    {item.excess !== null && isPositiveAmount(item.excess) ? <div><dt>Excesso sobre o orçamento</dt><dd className="ov-negative">{money(item.excess)}</dd></div>
      : item.margin !== null ? <div><dt>Margem até o orçamento</dt><dd>{money(item.margin)}</dd></div> : null}
  </dl>
}

function GroupCard({ group }: { group: BudgetControlGroup }) {
  const title = groupTitle[group.key]
  const note = groupStateText[group.state]
  return <article className="ov-card ov-indicator" aria-label={title}>
    <h3>{title} <Tag status={group.status} /></h3>
    {note && <p className="ov-meta" role="status">{note}.</p>}
    <Meter label={title} percent={group.projectedPercent} />
    <Lines item={group} cards={group.key === 'Cards'} />
    {group.excessRealized !== null && isPositiveAmount(group.excessRealized) && <p className="ov-meta">Já realizado acima da meta em {money(group.excessRealized)}: o valor é preservado.</p>}
    {group.reasons.length > 0 && <p className="ov-meta">Dados incompletos: {group.reasons.map(reason => reasonText[reason] ?? reason).join('; ')}.</p>}
  </article>
}

export function OverviewBudgetControlSection({ control, competence, onNavigate }: { control: BudgetControl; competence: string; onNavigate: Go }) {
  const { total } = control
  const go = (action: BudgetAction, context: string | null) => {
    switch (action) {
      case 'OpenCards': return onNavigate('cards')
      case 'OpenBudget': return onNavigate('budgets', { competence, budgetGroup: (context ?? undefined) as BudgetGroup | undefined })
      case 'OpenAgenda': return onNavigate('calendar', { month: competence })
      case 'OpenPlanning': return onNavigate('planning', { planningTab: 'projection' })
      case 'OpenReview': return onNavigate('transactions', { review: { kind: 'category' } })
      case 'OpenAccounts': return onNavigate('accounts')
    }
  }
  return <section className="ov-budgetctl" aria-labelledby="ov-budgetctl-title">
    <header><h2 id="ov-budgetctl-title">Consumo do orçamento</h2>
      <p className="ov-meta">Por competência: compras de cartão entram pela fatura; o pagamento da fatura afeta o caixa, mas não aumenta o consumo. Transferências não são despesas.</p></header>
    {control.notes.map(code => <p key={code} className="ov-notice ov-notice--info" role="status">{noteText[code] ?? code}</p>)}

    <div className="ov-budgetctl__cards" role="group" aria-label="Consumo do orçamento por grupo">
      <article className="ov-card ov-indicator" aria-label="Orçamento mensal">
        <h3>Orçamento mensal <Tag status={total.status} /></h3>
        {!total.targetDefined && control.targetsAttributable && <p className="ov-meta" role="status">O orçamento total não foi definido.</p>}
        <Meter label="orçamento mensal" percent={total.projectedPercent} />
        <Lines item={total} />
        <button type="button" onClick={() => go('OpenBudget', null)}>{total.targetDefined ? 'Abrir orçamento' : 'Definir metas'}</button>
      </article>
      <GroupCard group={control.cards} />
      <GroupCard group={control.outsideCards} />
    </div>
    <p className="ov-meta ov-meta--note">Metas de categorias e de despesas fixas são visões complementares dos mesmos gastos: não são somadas a este total.</p>

    <div className="ov-budgetctl__pressures">
      <section className="ov-card" aria-labelledby="ov-pressure-title">
        <h3 id="ov-pressure-title">O que está pressionando meu orçamento?</h3>
        {control.pressures.length === 0 ? <p className="ov-empty">Nenhum fator de pressão identificado com os dados disponíveis.</p>
          : <ol className="ov-pressure__list">{control.pressures.map(item => <li key={`${item.kind}-${item.subject ?? ''}`} className="ov-pressure">
            <div className="ov-pressure__head"><strong>{item.rank}. {pressureText(item)}</strong>
              <span>{item.amount === null ? 'Valor não estimável' : money(item.amount)}</span></div>
            <p className="ov-meta">{item.referenceAmount !== null && referenceText[item.referenceKind] ? `Referência: ${referenceText[item.referenceKind]} de ${money(item.referenceAmount)}. ` : ''}
              {impactText[item.impact]} · {qualityText[item.quality]}</p>
            <button type="button" className="button--link" onClick={() => go(item.action, item.actionContext)}>{actionText[item.action]}</button>
          </li>)}</ol>}
        <p className="ov-meta ov-meta--note">Os desvios por categoria, tipo e grupo olham os mesmos gastos por ângulos diferentes e não se somam.</p>
      </section>

      <section className="ov-card" aria-labelledby="ov-actions-title">
        <h3 id="ov-actions-title">Ações recomendadas</h3>
        {control.recommendations.length === 0 ? <p className="ov-empty">Nenhuma ação necessária com os dados disponíveis.</p>
          : <ul className="ov-pressure__list">{control.recommendations.map(item => <li key={item.code} className="ov-pressure">
            <p>{recommendationText[item.code] ?? item.code}</p>
            <button type="button" className="button--link" onClick={() => go(item.action, item.actionContext)}>{actionText[item.action]}</button>
          </li>)}</ul>}
      </section>
    </div>
  </section>
}
