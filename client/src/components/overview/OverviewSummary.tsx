import type { BudgetControl, FinancialDecision, MonthlyResult } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import { formatDate } from '../../money'
import { OperationalError } from '../OperationalFeedback'
import type { Go } from './Overview'
import { LoadingNote, Skeleton } from './OverviewSkeleton'
import type { ResultBlock } from './OverviewResult'
import { barWidth, effectiveScopeLabel, explain, isNegative, isZeroAmount, money, monthTitle, percentNow, percentText, periodLabels } from './overviewText'
import { CoverageLine, SourceLine } from './OverviewProvenance'

// T14.3 — topo da Visão Geral: os dois saldos em destaque, o que entrou como receita e o consumo do mês, e o orçamento PF com a barra. É só leitura: todo valor, percentual e
// "restam" vem do servidor. O consumo é a perspectiva orçamentária (inclui compras no cartão) e por isso não é chamado de saída de caixa.

export function OverviewSummary({ competence, result, decision, plan, control, block, queriedAt, onNavigate }: {
  queriedAt: number | null; competence: string; result: MonthlyResult | null; decision: FinancialDecision | null; plan: PlanActualDashboard | null; control: BudgetControl | null; block: ResultBlock; onNavigate: Go
}) {
  const kind = result?.periodKind ?? plan?.periodKind ?? null
  const past = kind === 'Past'
  const bank = decision?.context.bankBalance ?? null
  const closing = plan ? plan.situation.projectedClosing : undefined
  const loading = block.loading
  const ctx = decision?.context
  const scope = plan ? effectiveScopeLabel(plan.scope) : ctx ? effectiveScopeLabel({ kind: ctx.scopeKind, accountName: ctx.accountName }) : null
  const month = monthTitle(competence)
  const partial = result?.state === 'Partial' || plan?.coverage.complete === false
  const at = (...parts: (string | false | null)[]) => parts.filter(Boolean).join(' · ')
  return <section className="ov-card ov-summary" aria-labelledby="ov-summary-title" aria-busy={loading || undefined}>
    <header className="ov-card__head">
      <div><h3 id="ov-summary-title">Resumo do mês</h3><p className="ov-meta">{scope ? `${month} · ${scope}` : month}</p></div>
      {kind && <span className="ov-tag">{periodLabels[kind]}</span>}
    </header>
    {block.error ? <OperationalError error={block.error} onRetry={block.retry} retryLabel="Recarregar o resumo" /> : <>
      <div className="ov-summary__balances">
        <Figure label="Saldo bancário observado" loading={loading} value={bank} negativeTag
          caption={bank === null ? at(scope, 'O banco ainda não informou o saldo') : at(scope, ctx?.bankPositionDate ? `Posição bancária de ${formatDate(ctx.bankPositionDate)}` : 'Saldo bancário · data da posição indisponível', partial && 'Cobertura parcial')} />
        <Figure label={past ? 'Saldo no fechamento' : 'Saldo projetado no fim do mês'} loading={loading || closing === undefined} value={closing ?? null} negativeTag
          caption={closing === null && plan ? at(scope, `Fechamento de ${month}`, explain(plan.situation.reasons)[0] ?? 'Ainda não é possível calcular') : at(scope, `Fechamento de ${month}`, past ? 'Realizado' : 'Projeção', partial && 'Cobertura parcial')} />
      </div>
      <div className="ov-summary__flows">
        <Figure label="Receitas recebidas" loading={loading} value={result ? result.budget.income : null} tone="income" boxed caption={at(scope, month, 'Lançamentos realizados', partial && 'Parcial')} />
        <Figure label="Consumo realizado" loading={loading} value={result ? result.budget.consumption : null} boxed caption={at(scope, month, 'Realizado, inclui compras no cartão', partial && 'Parcial')} />
      </div>
      <CoverageLine partial={partial} reasons={[...(result?.reasons ?? []), ...(plan?.coverage.reasons ?? [])]}
        extra={plan && plan.situation.unavailableAccounts > 0 ? [`${plan.situation.unavailableAccounts === 1 ? 'Uma conta ficou' : `${plan.situation.unavailableAccounts} contas ficaram`} fora do saldo projetado.`] : []} />
      {!loading && <SourceLine source={decision?.context.source} updatedAt={decision?.context.lastSyncedAt} queriedAt={queriedAt} />}
      <Budget control={control} loading={loading} month={month} queriedAt={queriedAt} onOpen={() => onNavigate('budgets', { competence })} />
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

function Budget({ control, loading, month, queriedAt, onOpen }: { control: BudgetControl | null; loading: boolean; month: string; queriedAt: number | null; onOpen: () => void }) {
  const total = control?.total ?? null
  const defined = total !== null && total.targetDefined && total.target !== null
  const exceeded = defined && total.status === 'Exceeded'
  return <div className="ov-summary__budget" role="group" aria-label="Orçamento PF do mês">
    <div className="ov-summary__budget-head">
      <h4>Orçamento PF do mês</h4>
      <span className="ov-meta">{loading ? '' : total === null ? 'Indisponível' : defined ? (isZeroAmount(total.target) ? 'Meta definida em R$ 0,00' : `${percentText(total.projectedPercent) ?? '—'} da meta`) : 'Meta não configurada'}</span>
    </div>
    {loading ? <Skeleton width="100%" height=".5rem" />
      : total === null ? <p className="ov-empty" data-unavailable>O orçamento não está disponível agora.</p>
        : <>
          {defined && <div className={`ov-track ov-track--thick${exceeded ? ' ov-track--over' : ''}`} role="progressbar" aria-label="Total previsto do orçamento PF" aria-valuemin={0} aria-valuemax={100}
            aria-valuenow={percentNow(total.projectedPercent)} aria-valuetext={`${percentText(total.projectedPercent) ?? 'sem percentual'} da meta`}><span className="ov-track__done" style={{ width: barWidth(total.projectedPercent) }} /></div>}
          <div className="ov-summary__budget-foot">
            <strong className="ov-mono"><span className="ov-muted">Consumo projetado</span> {money(total.projected)}{defined && <> <span className="ov-muted">· Meta</span> {money(total.target)}</>}</strong>
            <span className={exceeded ? 'ov-negative' : 'ov-muted'}>{!defined ? '' : exceeded ? `${money(total.excess)} acima da meta` : `Restam ${money(total.margin)}`}</span>
          </div>
          <p className="ov-meta">{`Todas as contas PF · ${month} · ${defined ? 'Meta mensal' : 'Sem meta'} · Independente da conta escolhida no topo`}</p>
          <CoverageLine partial={control?.state === 'Partial'} reasons={[...(control?.cards.reasons ?? []), ...(control?.outsideCards.reasons ?? []), ...(control?.notes ?? [])]} />
          <SourceLine source={control?.source} updatedAt={control?.dataUpdatedAt} queriedAt={queriedAt} />
          <button type="button" className="ov-link" onClick={onOpen}>Ver orçamento ›</button>
        </>}
  </div>
}
