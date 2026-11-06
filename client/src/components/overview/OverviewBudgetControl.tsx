import type { BudgetControl, BudgetControlGroup, BudgetGroupStatus } from '../../api/overview'
import type { OverviewNavigation, OverviewSection } from './Overview'
import { Disclosure } from './Disclosure'
import { budgetDestination } from './priorityItems'
import { actionText, groupStateText, groupTitle, impactText, noteText, originText, pressureText, qualityText, reasonText, referenceText, statusText } from './budgetControlText'
import { barWidth, isPositiveAmount, money, monthTitle, percentNow, percentText } from './overviewText'

// T11.2 / T12.2: "Meu orçamento". Um resumo (meta efetiva, gasto, comprometido, estimado, total previsto e margem) e duas barras (cartões e fora dos cartões).
// Tudo vem do servidor: o React apresenta valores, estados e destinos, sem calcular.

type Go = (section: OverviewSection, context?: OverviewNavigation) => void

function Tag({ status }: { status: BudgetGroupStatus }) {
  return <span className={`ov-tag${status === 'Exceeded' ? ' ov-tag--danger' : status === 'WithinBudget' ? ' ov-tag--ok' : ''}`}>{statusText[status]}</span>
}

function Meter({ label, percent }: { label: string; percent: string | null }) {
  return percent === null ? null : <div className="ov-bar-track" role="progressbar" aria-label={`Total previsto de ${label}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(percent)}
    aria-valuetext={percentText(percent) ?? ''}><span style={{ width: barWidth(percent) }} /></div>
}

function GroupBar({ group }: { group: BudgetControlGroup }) {
  const title = groupTitle[group.key]
  const note = groupStateText[group.state]
  const over = group.excess !== null && isPositiveAmount(group.excess)
  return <li className={`ov-budget-group${over ? ' ov-budget-group--over' : ''}`} aria-label={title}>
    <div className="ov-limit__head"><strong>{title}</strong>{group.state === 'TargetNotAttributable' ? <span className="ov-tag">Meta global</span> : <Tag status={group.status} />}</div>
    <Meter label={title} percent={group.projectedPercent} />
    <p className="ov-limit__values"><span>Total previsto {money(group.projected)}{group.target === null ? '' : ` de ${money(group.target)}`}</span>
      <span className={over ? 'ov-negative' : undefined}>{group.target === null ? (group.state === 'TargetNotAttributable' ? 'Meta global' : 'Meta não configurada') : over ? `Excesso de ${money(group.excess)}` : group.margin !== null ? `Margem de ${money(group.margin)}` : null}</span></p>
    {note && group.state !== 'TargetNotDefined' && <p className="ov-meta" role="status">{note}.</p>}
    {group.excessRealized !== null && isPositiveAmount(group.excessRealized) && <p className="ov-meta">Já gasto acima da meta em {money(group.excessRealized)}: o valor é preservado.</p>}
    {group.reasons.length > 0 && <p className="ov-meta">Dados incompletos: {group.reasons.map(reason => reasonText[reason] ?? reason).join('; ')}.</p>}
  </li>
}

export function OverviewBudgetSummarySection({ control, competence, pressuresOpen, onPressuresOpenChange, onNavigate }: {
  control: BudgetControl; competence: string; pressuresOpen: boolean; onPressuresOpenChange: (open: boolean) => void; onNavigate: Go
}) {
  const { total } = control
  const origin = originText(control.targetsAttributable ? control.totalTargetOrigin : null, control.totalTargetFrom, monthTitle)
  const over = total.excess !== null && isPositiveAmount(total.excess)
  const go = (to: ReturnType<typeof budgetDestination>) => onNavigate(to.section, to.context)
  const noTarget = !total.targetDefined
  return <section className="ov-budgetctl" aria-labelledby="ov-budgetctl-title">
    <header className="ov-area__head"><h2 id="ov-budgetctl-title">Meu orçamento</h2>
      <p className="ov-meta">Por competência: compras de cartão entram pela fatura; o pagamento da fatura afeta o caixa, mas não aumenta o gasto. Transferências não são despesas.</p></header>
    {control.notes.map(code => <p key={code} className="ov-notice ov-notice--info" role="status">{noteText[code] ?? code}</p>)}

    <article className="ov-card ov-budget-summary" aria-label="Orçamento mensal">
      <div className="ov-budget-summary__head">
        <div><h3>Meta mensal efetiva</h3>
          <p className="ov-indicator__value" data-unavailable={total.target === null || undefined}>{total.target === null ? (control.targetsAttributable ? 'Meta não configurada' : 'Meta global') : money(total.target)}</p>
          {origin && <p className="ov-meta"><span className="ov-tag">{origin}</span></p>}
          {!control.targetsAttributable && <p className="ov-meta" role="status">Esta conta isolada não recebe a meta global do orçamento.</p>}
          {control.targetsAttributable && noTarget && <p className="ov-meta" role="status">O orçamento total não foi definido.</p>}</div>
        {control.targetsAttributable ? <Tag status={total.status} /> : <span className="ov-tag">Meta global</span>}
      </div>
      <Meter label="orçamento mensal" percent={total.projectedPercent} />
      <dl className="ov-cash__parts">
        <div><dt>Já gasto</dt><dd>{money(total.realized)}</dd></div>
        <div><dt>Despesas futuras já conhecidas</dt><dd>{money(total.committed)}</dd></div>
        <div><dt>Estimativas complementares</dt><dd>{money(total.estimated)}</dd></div>
        <div className="ov-cash__parts--total"><dt>Total previsto de gastos</dt><dd>{money(total.projected)}</dd></div>
        {over ? <div><dt>Excesso sobre a meta</dt><dd className="ov-negative">{money(total.excess)}</dd></div>
          : total.margin !== null ? <div><dt>Margem em relação à meta</dt><dd>{money(total.margin)}</dd></div> : null}
      </dl>
      <p className="ov-meta ov-meta--note">Margem do orçamento é uma referência de planejamento, não dinheiro disponível na conta.</p>
      <ul className="ov-budget-groups" aria-label="Cartões e fora dos cartões"><GroupBar group={control.cards} /><GroupBar group={control.outsideCards} /></ul>
      <div className="form__actions"><button type="button" onClick={() => go(budgetDestination('OpenBudget', null, competence))}>{noTarget && control.targetsAttributable ? 'Definir metas' : 'Abrir orçamento completo'}</button></div>
      <p className="ov-meta ov-meta--note">Metas de categorias e de despesas fixas são visões complementares dos mesmos gastos: ficam no orçamento completo e não são somadas a este total.</p>
    </article>

    <Disclosure id="pressures" title="O que está pressionando meu orçamento?" hint="Principais fatores, do maior para o menor" open={pressuresOpen} onOpenChange={onPressuresOpenChange}>
      {control.pressures.length === 0 ? <p className="ov-empty">Nenhum fator de pressão identificado com os dados disponíveis.</p>
        : <ol className="ov-pressure__list">{control.pressures.map(item => <li key={`${item.kind}-${item.subject ?? ''}`} className="ov-pressure">
          <div className="ov-pressure__head"><strong>{item.rank}. {pressureText(item)}</strong>
            <span>{item.amount === null ? 'Ainda não é possível calcular' : money(item.amount)}</span></div>
          <p className="ov-meta">{item.referenceAmount !== null && referenceText[item.referenceKind] ? `Referência: ${referenceText[item.referenceKind]} de ${money(item.referenceAmount)}. ` : ''}
            {impactText[item.impact]} · {qualityText[item.quality]}</p>
          <button type="button" className="button--link" onClick={() => go(budgetDestination(item.action, item.actionContext, competence))}>{actionText[item.action]}</button>
        </li>)}</ol>}
      <p className="ov-meta ov-meta--note">Os desvios por categoria, tipo e grupo olham os mesmos gastos por ângulos diferentes e não se somam.</p>
    </Disclosure>
  </section>
}
