import type { CSSProperties } from 'react'
import type { FinancialDecision, MonthlyOverdraft } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import { formatDate } from '../../money'
import { OperationalError } from '../OperationalFeedback'
import type { BlockLoad, Go } from './Overview'
import { LoadingNote, Skeleton } from './OverviewSkeleton'
import { isNegative, isPositiveAmount, money, percentText, recoveryPendingText } from './overviewText'

// T14.3 — Seção C: saldo bancário e cheque especial num card próprio. O saldo observado é a posição do banco (com data); o limite disponível é crédito do banco e nunca é apresentado como saldo
// próprio. Limite disponível e utilização percentual vêm do servidor (`monthlyResult.overdraft`); aqui só se escolhe texto e proporção da barra.

type OverviewState = { loading: boolean; error: unknown; retry: () => void }
const meterStyle = (part: string, whole: string) => ({ '--ov-part': part, '--ov-whole': whole }) as CSSProperties

export function OverviewBalanceCard({ plan, decision, limit, overview, onNavigate }: {
  plan: BlockLoad<PlanActualDashboard>; decision: FinancialDecision | null; limit: MonthlyOverdraft | null; overview: OverviewState; onNavigate: Go
}) {
  return <section className="ov-card ov-balance-card" aria-label="Saldo e cheque especial" aria-busy={(plan.loading || overview.loading) || undefined}>
    <Balance plan={plan} decision={decision} overview={overview} onNavigate={onNavigate} />
    {plan.error ? <OperationalError error={plan.error} onRetry={plan.retry} retryLabel="Recarregar o cheque especial" />
      : plan.data ? <Overdraft plan={plan.data} decision={decision} limit={limit} onOpen={() => onNavigate('planning', { planningTab: 'projection' })} />
        : <div className="ov-overdraft"><LoadingNote>Carregando o cheque especial</LoadingNote><Skeleton width="100%" height="1rem" /><Skeleton width="70%" height="1rem" /></div>}
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
    <p className="ov-label">{fromBank || waiting ? 'Saldo bancário observado' : 'Saldo calculado'}</p>
    {waiting ? <><LoadingNote>Carregando o saldo</LoadingNote><Skeleton width="min(11rem, 60%)" height="2.25rem" /></>
      : <p className={`ov-balance__value${isNegative(value) ? ' ov-negative' : ''}`} data-unavailable={value === null || undefined}>{value === null ? 'Indisponível' : money(value)}</p>}
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
 * Cheque especial e recuperação do mês. Numa conta, a recuperação e a meta vêm da decisão do servidor; no consolidado a meta é acompanhada por conta e os valores são a soma das contas
 * (o servidor apura o limite utilizado conta a conta, sem compensar o saldo positivo de uma com a dívida de outra). Só aparece como favorável a redução efetiva da utilização.
 */
function Overdraft({ plan, decision, limit: summary, onOpen }: { plan: PlanActualDashboard; decision: FinancialDecision | null; limit: MonthlyOverdraft | null; onOpen: () => void }) {
  const overdraft = plan.overdraft
  if (!overdraft.eligible) return null
  const used = overdraft.usedLatest, limit = overdraft.contractedLimit
  const recovery = decision?.recovery ?? null
  const perAccount = recovery !== null && ['Provisional', 'Fixed', 'Pending', 'NotRegistered'].includes(recovery.state)
  const consolidated = plan.scope.kind === 'all' || recovery?.reason === 'ConsolidatedScope'
  const recovered = perAccount ? recovery.effectiveRecovery : consolidated ? overdraft.effectiveRecovery : null
  const projected = perAccount ? recovery.projectedRecovery : consolidated ? overdraft.projectedRecovery : null
  const hasTarget = perAccount && (recovery.state === 'Provisional' || recovery.state === 'Fixed') && recovery.target !== null
  const awaiting = perAccount && recovery.state === 'Pending'
  const showRecovery = perAccount || consolidated && (recovered !== null || projected !== null)
  const met = hasTarget && recovery.band !== null && (recovery.band === 'Met' || recovery.band === 'Exceeded')
  return <div className="ov-overdraft" aria-label="Cheque especial">
    <div className="ov-overdraft__head">
      <span className="ov-label">Limite utilizado</span>
      <span className="ov-mono">{money(used)} <small>de {limit === null ? 'limite não informado' : money(limit)}</small></span>
    </div>
    {used !== null && limit !== null && isPositiveAmount(limit) && <div className="ov-meter ov-meter--danger" role="meter" aria-label="Limite do cheque especial utilizado"
      aria-valuetext={`${money(used)} de ${money(limit)}`} style={meterStyle(used, limit)}><span /></div>}
    {summary && <dl className="ov-overdraft__limit">
      <div><dt>Limite disponível</dt><dd className="ov-mono">{money(summary.available)}</dd></div>
      <div><dt>Utilização</dt><dd className="ov-mono">{percentText(summary.utilizationPercent) ?? '—'}</dd></div>
      {summary.projectedUsed !== null && <div><dt>Utilizado no fechamento</dt><dd className="ov-mono">{money(summary.projectedUsed)}{summary.projectedPercent !== null && <small> ({percentText(summary.projectedPercent)})</small>}</dd></div>}
      {summary.projectedAvailable !== null && <div><dt>Disponível no fechamento</dt><dd className="ov-mono">{money(summary.projectedAvailable)}</dd></div>}
    </dl>}
    {summary && <p className="ov-note">Limite disponível é crédito do banco, não saldo próprio.{summary.exceedsLimit && <> <span className="ov-tag ov-tag--danger">Acima do limite contratado</span></>}</p>}
    {showRecovery && <dl className="ov-overdraft__recovery">
      <div><dt>{isNegative(recovered) ? 'Aumento da utilização' : 'Recuperado no mês'}</dt><dd className={isNegative(recovered) ? 'ov-negative' : undefined}>{money(isNegative(recovered) ? recovered!.replace('-', '') : recovered)}</dd></div>
      <div><dt>{recovery?.state === 'Provisional' ? 'Meta provisória' : 'Meta do mês'}</dt>
        <dd data-unavailable={!hasTarget || undefined}>{hasTarget ? money(recovery.target) : consolidated ? 'Por conta' : awaiting ? 'Aguardando dados para definir a meta' : 'Meta não configurada'}</dd></div>
      <div><dt>{isNegative(projected) ? 'Aumento projetado' : 'Projetado'}</dt><dd className={isNegative(projected) ? 'ov-negative' : undefined} data-unavailable={projected === null || undefined}>{projected === null ? 'Ainda não é possível calcular' : money(isNegative(projected) ? projected.replace('-', '') : projected)}</dd></div>
    </dl>}
    {awaiting && <p className="ov-note">Aguardando dados para definir a meta{recovery.reason ? `: ${recoveryPendingText[recovery.reason] ?? 'dados insuficientes'}` : ''}.</p>}
    {hasTarget && <p className="ov-note">{met ? <><span className="ov-tag ov-tag--ok">✓ Meta cumprida</span> pela projeção atual</> : <><span className="ov-tag ov-tag--warning">Abaixo da meta</span> pela projeção atual</>}</p>}
    <button type="button" className="ov-link" onClick={onOpen}>Recuperação no Planejamento ›</button>
  </div>
}
