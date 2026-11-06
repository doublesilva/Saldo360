import type { CSSProperties, ReactNode } from 'react'
import type { FinancialDecision } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import { formatDate } from '../../money'
import { OperationalError } from '../OperationalFeedback'
import type { BlockLoad, Go } from './Overview'
import { explain, isNegative, isPositiveAmount, money } from './overviewText'

// Dashboard Folio, cartão principal: o saldo bancário como informação principal e, ao lado, receitas, despesas e fechamento do mês. O cheque especial e a recuperação
// do mês ficam compactos dentro do bloco do saldo e só aparecem quando a conta tem limite. Nada é calculado aqui: só se escolhe o texto e a proporção da barra.

type OverviewState = { loading: boolean; error: unknown; retry: () => void }

/** Proporção visual (parte ÷ todo) feita pelo CSS a partir dos textos decimais do servidor; nenhum valor derivado é exibido. */
const meterStyle = (part: string, whole: string) => ({ '--ov-part': part, '--ov-whole': whole }) as CSSProperties

const Icon = ({ path }: { path: string }) => <svg className="ov-icon" viewBox="0 0 16 16" aria-hidden="true"><path d={path} /></svg>
const icons = { income: 'M12 4 4 12M4 6v6h6', expense: 'M4 12 12 4M6 4h6v6', closing: 'M3 4h10v9H3zM3 7h10M6 2v3M10 2v3' }

export function OverviewHero({ competence, plan, decision, overview, onNavigate, children }: {
  competence: string; plan: BlockLoad<PlanActualDashboard>; decision: FinancialDecision | null; overview: OverviewState; onNavigate: Go; children?: ReactNode
}) {
  const data = plan.data
  return <section className="ov-card ov-hero" aria-label="Saldo e resumo do mês">
    <div className="ov-hero__balance">
      <Balance plan={plan} decision={decision} overview={overview} onNavigate={onNavigate} />
      {data && <Overdraft plan={data} decision={decision} onOpen={() => onNavigate('planning', { planningTab: 'projection' })} />}
      {children}
    </div>
    <div className="ov-hero__kpis" role="group" aria-label="Resumo do mês">
      {plan.error ? <div className="ov-hero__error"><OperationalError error={plan.error} onRetry={plan.retry} retryLabel="Recarregar o resumo" /></div> : <>
        <Kpi label="Receitas do mês" icon={icons.income} tone="income" value={data?.income.realized}
          caption={data ? isPositiveAmount(data.income.pending) ? `Previsto a receber ${money(data.income.pending)}` : 'Receitas recebidas no mês' : null} />
        <Kpi label="Despesas do mês" icon={icons.expense} value={data?.expenses.consumption.realized}
          caption={data ? isPositiveAmount(data.expenses.consumption.pending) ? `Previsto a pagar ${money(data.expenses.consumption.pending)}` : 'Pagas e lançadas no cartão' : null} />
        <Closing plan={data} onAgenda={() => onNavigate('calendar', { month: competence })} />
      </>}
    </div>
  </section>
}

function Balance({ plan, decision, overview, onNavigate }: { plan: BlockLoad<PlanActualDashboard>; decision: FinancialDecision | null; overview: OverviewState; onNavigate: Go }) {
  const bank = decision?.context.bankBalance ?? null
  const situation = plan.data?.situation
  const calculated = situation ? situation.realizedBalance ?? situation.partialRealizedBalance : null
  // O saldo do banco tem prioridade; sem ele, o saldo calculado aparece com o nome certo (nunca como se fosse o do banco).
  const fromBank = bank !== null
  const waiting = !fromBank && (overview.loading || plan.loading)
  const value = fromBank ? bank : calculated
  return <div className="ov-balance">
    <p className="ov-label">{fromBank || waiting ? 'Saldo bancário' : 'Saldo calculado'}</p>
    <p className={`ov-balance__value${isNegative(value) ? ' ov-negative' : ''}`} data-unavailable={!waiting && value === null || undefined}>
      {waiting ? 'Carregando…' : value === null ? 'Indisponível' : money(value)}</p>
    <p className="ov-balance__note">
      {isNegative(value) && <span className="ov-tag ov-tag--danger">Saldo negativo</span>}
      {!fromBank && !waiting && situation && situation.realizedBalance === null && calculated !== null && <span className="ov-tag ov-tag--warning">Parcial</span>}
      <span>{fromBank
        ? decision?.context.bankPositionDate ? `Posição do banco em ${formatDate(decision.context.bankPositionDate)}` : 'Informado pelo banco'
        : waiting ? '' : 'O banco ainda não informou o saldo: calculado a partir dos lançamentos'}</span>
      <button type="button" className="ov-link" onClick={() => onNavigate('accounts')}>Ver contas ›</button>
    </p>
    {overview.error !== null && !fromBank && <OperationalError error={overview.error} onRetry={overview.retry} retryLabel="Recarregar a Visão Geral" />}
  </div>
}

/**
 * Cheque especial e recuperação do mês. Numa conta, a recuperação e a meta vêm da decisão do servidor; no consolidado a meta é acompanhada por conta e os valores são a
 * soma das contas (o servidor apura o limite utilizado conta a conta, sem compensar o saldo positivo de uma com a dívida de outra).
 */
function Overdraft({ plan, decision, onOpen }: { plan: PlanActualDashboard; decision: FinancialDecision | null; onOpen: () => void }) {
  const overdraft = plan.overdraft
  if (!overdraft.eligible) return null
  const used = overdraft.usedLatest, limit = overdraft.contractedLimit
  const recovery = decision?.recovery ?? null
  const perAccount = recovery !== null && ['Provisional', 'Fixed', 'Pending', 'NotRegistered'].includes(recovery.state)
  const consolidated = plan.scope.kind === 'all' || recovery?.reason === 'ConsolidatedScope'
  const recovered = perAccount ? recovery.effectiveRecovery : consolidated ? overdraft.effectiveRecovery : null
  const projected = perAccount ? recovery.projectedRecovery : consolidated ? overdraft.projectedRecovery : null
  const hasTarget = perAccount && (recovery.state === 'Provisional' || recovery.state === 'Fixed') && recovery.target !== null
  const showRecovery = perAccount || consolidated && (recovered !== null || projected !== null)
  return <div className="ov-overdraft" aria-label="Cheque especial">
    <div className="ov-overdraft__head">
      <span className="ov-label">Limite utilizado</span>
      <span className="ov-mono">{money(used)} <small>de {limit === null ? 'limite não informado' : money(limit)}</small></span>
    </div>
    {used !== null && limit !== null && isPositiveAmount(limit) && <div className="ov-meter ov-meter--danger" role="meter" aria-label="Limite do cheque especial utilizado"
      aria-valuetext={`${money(used)} de ${money(limit)}`} style={meterStyle(used, limit)}><span /></div>}
    {showRecovery && <dl className="ov-overdraft__recovery">
      <div><dt>Recuperado no mês</dt><dd className={isNegative(recovered) ? 'ov-negative' : undefined}>{money(recovered)}</dd></div>
      <div><dt>{recovery?.state === 'Provisional' ? 'Meta provisória' : 'Meta do mês'}</dt>
        <dd data-unavailable={!hasTarget || undefined}>{hasTarget ? money(recovery.target) : consolidated ? 'Por conta' : 'Meta não configurada'}</dd></div>
      <div><dt>Projetado</dt><dd data-unavailable={projected === null || undefined}>{projected === null ? 'Ainda não é possível calcular' : money(projected)}</dd></div>
    </dl>}
    <button type="button" className="ov-link" onClick={onOpen}>Recuperação no Planejamento ›</button>
  </div>
}

function Kpi({ label, icon, value, caption, tone }: { label: string; icon: string; value: string | undefined; caption: string | null; tone?: 'income' }) {
  return <article className="ov-kpi" aria-label={label}>
    <h3 className="ov-label"><Icon path={icon} />{label}</h3>
    <p className={`ov-kpi__value${tone ? ` ov-kpi__value--${tone}` : ''}`} data-unavailable={value === undefined || undefined}>{value === undefined ? 'Carregando…' : money(value)}</p>
    {caption && <p className="ov-kpi__caption">{caption}</p>}
  </article>
}

function Closing({ plan, onAgenda }: { plan: PlanActualDashboard | null; onAgenda: () => void }) {
  const past = plan?.periodKind === 'Past'
  const label = past ? 'Saldo no fechamento' : 'Fechamento projetado'
  const closing = plan?.situation.projectedClosing ?? null
  const caption = !plan ? null : closing === null ? explain(plan.situation.reasons)[0] ?? 'Ainda não é possível calcular.' : past ? 'Saldo realizado no último dia do mês' : 'Projeção: não é o saldo do banco'
  return <article className="ov-kpi" aria-label={label}>
    <h3 className="ov-label"><Icon path={icons.closing} />{label}</h3>
    <p className={`ov-kpi__value${isNegative(closing) ? ' ov-negative' : ''}`} data-unavailable={plan !== null && closing === null || undefined}>
      {!plan ? 'Carregando…' : closing === null ? 'Ainda não é possível calcular' : money(closing)}</p>
    {caption && <p className="ov-kpi__caption">{caption}{isNegative(closing) && <> · <span className="ov-tag ov-tag--danger">Saldo negativo</span></>}</p>}
    {plan && !past && <button type="button" className="ov-link" onClick={onAgenda}>Ver na Agenda ›</button>}
  </article>
}
