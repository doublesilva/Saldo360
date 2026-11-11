import type { BudgetControl, FinancialDecision, MonthlyResult } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import { formatDate } from '../../money'
import { OperationalError } from '../OperationalFeedback'
import type { Go } from './Overview'
import { LoadingNote, Skeleton } from './OverviewSkeleton'
import type { ResultBlock } from './OverviewResult'
import { barWidth, explain, isNegative, money, percentNow, percentText, periodLabels } from './overviewText'

// T14.3 — topo da Visão Geral: os dois saldos em destaque, o que entrou como receita e o consumo do mês, e o orçamento PF com a barra. É só leitura: todo valor, percentual e
// "restam" vem do servidor. O consumo é a perspectiva orçamentária (inclui compras no cartão) e por isso não é chamado de saída de caixa.

export function OverviewSummary({ competence, result, decision, plan, control, block, onNavigate }: {
  competence: string; result: MonthlyResult | null; decision: FinancialDecision | null; plan: PlanActualDashboard | null; control: BudgetControl | null; block: ResultBlock; onNavigate: Go
}) {
  const kind = result?.periodKind ?? plan?.periodKind ?? null
  const past = kind === 'Past'
  const bank = decision?.context.bankBalance ?? null
  const closing = plan ? plan.situation.projectedClosing : undefined
  const loading = block.loading
  return <section className="ov-card ov-summary" aria-labelledby="ov-summary-title" aria-busy={loading || undefined}>
    <header className="ov-card__head">
      <div><h3 id="ov-summary-title">Resumo do mês</h3><p className="ov-meta">Resultado financeiro mensal · {competence.split('-').reverse().join('/')}</p></div>
      {kind && <span className="ov-tag">{periodLabels[kind]}</span>}
    </header>
    {block.error ? <OperationalError error={block.error} onRetry={block.retry} retryLabel="Recarregar o resumo" /> : <>
      <div className="ov-summary__balances">
        <Figure label="Saldo bancário observado" loading={loading} value={bank} negativeTag
          caption={bank === null ? 'O banco ainda não informou o saldo.' : decision?.context.bankPositionDate ? `Posição bancária de ${formatDate(decision.context.bankPositionDate)}` : 'Informado pelo banco'} />
        <Figure label={past ? 'Saldo no fechamento' : 'Saldo projetado no fim do mês'} loading={loading || closing === undefined} value={closing ?? null} negativeTag
          caption={closing === null && plan ? explain(plan.situation.reasons)[0] ?? 'Ainda não é possível calcular.' : past ? 'Saldo realizado no último dia do mês' : 'Projeção, não saldo bancário'} />
      </div>
      <div className="ov-summary__flows">
        <Figure label="Receitas recebidas" loading={loading} value={result ? result.budget.income : null} tone="income" boxed caption="Realizado no mês" />
        <Figure label="Consumo realizado" loading={loading} value={result ? result.budget.consumption : null} boxed caption="Inclui compras registradas no cartão" />
      </div>
      <Budget control={control} loading={loading} onOpen={() => onNavigate('budgets', { competence })} />
    </>}
  </section>
}

function Figure({ label, value, caption, loading, tone, boxed, negativeTag }: { label: string; value: string | null; caption: string; loading: boolean; tone?: 'income'; boxed?: boolean; negativeTag?: boolean }) {
  return <article className={`ov-figure${boxed ? ' ov-figure--boxed' : ''}`} aria-label={label}>
    <h4 className="ov-label">{label}</h4>
    {loading ? <><LoadingNote>{`Carregando ${label.toLowerCase()}`}</LoadingNote><Skeleton width="min(11rem, 80%)" height="2rem" /><Skeleton width="60%" height=".8rem" /></>
      : <>
        <p className={`ov-figure__value${isNegative(value) ? ' ov-negative' : tone ? ' ov-positive' : ''}`} data-unavailable={value === null || undefined}>{value === null ? 'Indisponível' : money(value)}</p>
        <p className="ov-figure__caption">{caption}{negativeTag && isNegative(value) && <> · <span className="ov-tag ov-tag--danger">Saldo negativo</span></>}</p>
      </>}
  </article>
}

function Budget({ control, loading, onOpen }: { control: BudgetControl | null; loading: boolean; onOpen: () => void }) {
  const total = control?.total ?? null
  const defined = total !== null && total.targetDefined && total.target !== null
  const exceeded = defined && total.status === 'Exceeded'
  return <div className="ov-summary__budget" role="group" aria-label="Orçamento PF do mês">
    <div className="ov-summary__budget-head">
      <h4>Orçamento PF do mês</h4>
      <span className="ov-meta">{loading ? '' : total === null ? 'Indisponível' : defined ? `${percentText(total.projectedPercent) ?? '—'} da meta` : 'Meta não configurada'}</span>
    </div>
    {loading ? <Skeleton width="100%" height=".5rem" />
      : total === null ? <p className="ov-empty" data-unavailable>O orçamento não está disponível agora.</p>
        : <>
          {defined && <div className={`ov-track ov-track--thick${exceeded ? ' ov-track--over' : ''}`} role="progressbar" aria-label="Total previsto do orçamento PF" aria-valuemin={0} aria-valuemax={100}
            aria-valuenow={percentNow(total.projectedPercent)} aria-valuetext={`${percentText(total.projectedPercent) ?? 'sem percentual'} da meta`}><span className="ov-track__done" style={{ width: barWidth(total.projectedPercent) }} /></div>}
          <div className="ov-summary__budget-foot">
            <strong className="ov-mono">{money(total.projected)}{defined && <> / {money(total.target)}</>}</strong>
            <span className={exceeded ? 'ov-negative' : 'ov-muted'}>{!defined ? '' : exceeded ? `${money(total.excess)} acima da meta` : `Restam ${money(total.margin)}`}</span>
          </div>
          <p className="ov-meta">Considera todas as contas PF, independentemente da conta escolhida no topo.</p>
          <button type="button" className="ov-link" onClick={onOpen}>Ver orçamento ›</button>
        </>}
  </div>
}
