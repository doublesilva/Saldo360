import type { ReactNode } from 'react'
import type { DecisionAccountCoverage, FinancialDecision } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import { formatDate } from '../../money'
import type { OverviewNavigation, OverviewSection } from './Overview'
import { Disclosure } from './Disclosure'
import { caveat, constraintText, coverageMessage, coverageQuestion, coverageTag, marginNote, marginReason, natureText, notApplied, periodText, unavailableReason } from './decisionText'
import { dateTime, explain, isNegative, money, monthTitle, periodLabels } from './overviewText'

// T11.1 / T12.2: "Minha situação financeira". Tudo vem do servidor (compromissos, cobertura, margem, saldos): aqui só se apresenta, com texto que não depende de cor.
// Quatro indicadores e uma linha de margem segura; saldos mínimos, recebimentos, contas e ressalvas ficam em expansão.

type Go = (section: OverviewSection, context?: OverviewNavigation) => void
const day = (iso: string) => formatDate(iso).slice(0, 5)

function Tag({ tone, children }: { tone: 'ok' | 'warning' | 'danger' | 'neutral'; children: string }) {
  return <span className={`ov-tag${tone === 'neutral' ? '' : ` ov-tag--${tone}`}`}>{children}</span>
}

function Caveats({ codes }: { codes: string[] }) {
  return codes.length === 0 ? null : <ul className="ov-decision__caveats">{codes.map(code => <li key={code}>{caveat(code)}</li>)}</ul>
}

type PlanState = { data: PlanActualDashboard | null; loading: boolean; failed: boolean }

export type DecisionStatus = 'loading' | 'unavailable' | 'ready'

export function OverviewSituationSection({ competence, decision, decisionStatus, plan, scopeText, coverageOpen, onCoverageOpenChange, children, onNavigate }: {
  competence: string; decision: FinancialDecision | null; decisionStatus: DecisionStatus; plan: PlanState; scopeText: string; coverageOpen: boolean; onCoverageOpenChange: (open: boolean) => void; children?: ReactNode; onNavigate: Go
}) {
  const agenda = () => onNavigate('calendar', { month: competence })
  const periodKind = plan.data?.periodKind ?? null
  return <section className="ov-situation" aria-labelledby="ov-situation-title">
    <header className="ov-area__head">
      <h2 id="ov-situation-title">Minha situação financeira</h2>
      <p className="ov-situation__context" aria-label="Contexto da análise">
        <span>{monthTitle(competence)}{decision ? <> <Tag tone="neutral">{periodText[decision.context.period]}</Tag></> : periodKind ? <> <Tag tone="neutral">{periodLabels[periodKind]}</Tag></> : null}</span>
        <span>{scopeText || 'Carregando conta…'}</span>
        {decision && <span>{decision.context.bankPositionDate ? `Posição do banco em ${formatDate(decision.context.bankPositionDate)}` : 'Sem posição informada pelo banco'}</span>}
      </p>
    </header>
    {decision && !decision.snapshotConsistent && <p className="ov-notice ov-notice--warning" role="status">{caveat('PositionsInconsistent')}</p>}
    {decision ? <Indicators decision={decision} plan={plan} onNavigate={onNavigate} agenda={agenda} /> : <FallbackIndicators plan={plan} status={decisionStatus} />}
    {decision && <SafeMargin decision={decision} />}
    {children}
    {decision && <Disclosure id="coverage" title="Detalhes de cobertura, compromissos e margem segura" hint="Saldos mínimos, recebimentos esperados, contas e ressalvas" open={coverageOpen} onOpenChange={onCoverageOpenChange}>
      <CoverageDetails decision={decision} onNavigate={onNavigate} agenda={agenda} />
    </Disclosure>}
  </section>
}

function Indicators({ decision, plan, onNavigate, agenda }: { decision: FinancialDecision; plan: PlanState; onNavigate: Go; agenda: () => void }) {
  const { context, commitments, coverage } = decision
  const closed = context.period === 'Closed'
  const past = plan.data?.periodKind === 'Past'
  const closing = plan.data?.situation.projectedClosing ?? null
  const closingReason = plan.data ? explain(plan.data.situation.reasons)[0] ?? 'Faltam saldo inicial ou histórico para calcular.' : null
  const risk = coverage.state === 'Unavailable' ? null : coverage.firstNegativeDate ? `Saldo negativo a partir de ${formatDate(coverage.firstNegativeDate)}.` : null
  return <div className="ov-situation__cards" role="group" aria-label="Indicadores da situação financeira">
    <article className={`ov-card ov-indicator${isNegative(context.bankBalance) ? ' ov-indicator--negative' : ''}`} aria-label="Saldo bancário observado">
      <h3>Saldo bancário observado</h3>
      <p className="ov-indicator__value" data-unavailable={context.bankBalance === null || undefined}>{context.bankBalance === null ? 'Sem posição do banco' : money(context.bankBalance)}</p>
      {isNegative(context.bankBalance) && <p className="ov-tags"><span className="ov-tag ov-tag--danger">Saldo negativo</span></p>}
      <p className="ov-indicator__reference">{context.bankPositionDate ? `Informado pelo banco em ${formatDate(context.bankPositionDate)}` : 'O banco ainda não informou o saldo'}</p>
      <button type="button" onClick={() => onNavigate('accounts')}>Ver contas</button>
    </article>

    <article className="ov-card ov-indicator" aria-label="Compromissos restantes">
      <h3>Compromissos restantes</h3>
      <p className="ov-indicator__value">{money(commitments.total)}</p>
      <p className="ov-indicator__reference">{closed ? 'Ainda em aberto hoje, com vencimento no mês' : 'Ainda falta pagar'}{commitments.reliability === 'Preliminary' ? ' · estimativa preliminar' : ''}</p>
      <button type="button" onClick={agenda}>Ver na Agenda</button>
    </article>

    <article className={`ov-card ov-indicator${isNegative(closing) ? ' ov-indicator--negative' : ''}`} aria-label={past ? 'Saldo no fechamento' : 'Fechamento projetado'}>
      <h3>{past ? 'Saldo no fechamento' : 'Fechamento projetado'}</h3>
      {plan.data
        ? <>
          <p className="ov-indicator__value" data-unavailable={closing === null || undefined}>{money(closing)}</p>
          {isNegative(closing) && <p className="ov-tags"><span className="ov-tag ov-tag--danger">Saldo negativo</span></p>}
          <p className="ov-indicator__reference">{closing === null ? closingReason : past ? 'Valor realizado no último dia do mês' : 'Projetado: não é o saldo do banco'}</p>
        </>
        : <p className="ov-indicator__value" data-unavailable role="status">{plan.failed ? 'Ainda não é possível calcular' : 'Carregando…'}</p>}
    </article>

    <article className={`ov-card ov-indicator ov-decision__coverage ov-decision__coverage--${coverage.state}`} aria-label={coverageQuestion}>
      <h3>{coverageQuestion}</h3>
      <p className="ov-indicator__value ov-decision__message" role="status">{coverageMessage(coverage)}{coverage.reliability === 'Preliminary' ? ' (preliminar)' : ''}</p>
      <p className="ov-tags"><Tag tone={coverageTag[coverage.state].tone}>{coverageTag[coverage.state].label}</Tag></p>
      {coverage.state === 'Unavailable' && <p className="ov-meta">{unavailableReason(coverage.unavailableReason) ?? 'Faltam dados essenciais.'}</p>}
      {risk && <p className="ov-meta">Primeiro risco: {risk}</p>}
    </article>
  </div>
}

/** Sem a análise do servidor, só o saldo calculado a partir de plan-actual, com o nome certo: não é o saldo informado pelo banco. */
function FallbackIndicators({ plan, status }: { plan: PlanState; status: DecisionStatus }) {
  const situation = plan.data?.situation
  const balance = situation ? situation.realizedBalance ?? situation.partialRealizedBalance : null
  return <div className="ov-situation__cards" role="group" aria-label="Indicadores da situação financeira">
    <article className="ov-card ov-indicator" aria-label="Saldo calculado">
      <h3>Saldo calculado</h3>
      <p className="ov-indicator__value" data-unavailable={balance === null || undefined}>{plan.data ? money(balance) : plan.failed ? 'Ainda não é possível calcular' : 'Carregando…'}</p>
      <p className="ov-indicator__reference">Calculado pelo Saldo360 a partir dos lançamentos; não é o saldo informado pelo banco.</p>
    </article>
    <article className="ov-card ov-indicator" aria-label="Análise de cobertura indisponível"><h3>{coverageQuestion}</h3>
      <p className="ov-indicator__value" data-unavailable role="status">{status === 'loading' ? 'Carregando…' : 'Ainda não é possível calcular'}</p>{status !== 'loading' && <p className="ov-meta">A análise de segurança financeira não está disponível agora.</p>}</article>
  </div>
}

function SafeMargin({ decision }: { decision: FinancialDecision }) {
  const { safeMargin } = decision
  return <p className="ov-situation__margin" aria-label="Margem segura para gastar à vista">
    <strong>Margem segura para gastar à vista</strong>{' '}
    {safeMargin.state === 'Unavailable'
      ? <span role="status" data-unavailable>ainda não é possível calcular{safeMargin.reasons[0] ? `: ${marginReason(safeMargin.reasons[0])}` : '.'}</span>
      : <><span className="ov-situation__margin-value">{money(safeMargin.amount)}</span>{safeMargin.state === 'Preliminary' ? <> <Tag tone="warning">Preliminar</Tag></> : null}
        <span className="ov-meta"> hoje ({formatDate(safeMargin.referenceDate)}), sem comprometer os pagamentos nem a meta de recuperação. Não é saldo disponível nem crédito do banco.</span></>}
  </p>
}

function CoverageDetails({ decision, onNavigate, agenda }: { decision: FinancialDecision; onNavigate: Go; agenda: () => void }) {
  const { context, commitments, coverage, safeMargin } = decision
  const closed = context.period === 'Closed'
  const inCompetenceLabel = closed ? 'Ainda em aberto hoje, com vencimento no mês' : context.period === 'Future' ? 'Vencem na competência' : 'A vencer até o fim da competência'
  return <div className="ov-situation__details">
    <section aria-label="Contexto e posição bancária">
      <h4>Posição bancária</h4>
      <dl className="ov-cash__parts">
        <div><dt>Última sincronização</dt><dd>{context.lastSyncedAt ? dateTime(context.lastSyncedAt) : 'sem sincronização'}</dd></div>
        {context.competence !== context.currentCompetence && <div><dt>Competência vigente</dt><dd>{monthTitle(context.currentCompetence)}</dd></div>}
      </dl>
    </section>

    <section aria-label="Compromissos restantes">
      <h4>Compromissos restantes</h4>
      <dl className="ov-cash__parts">
        {!closed && <div><dt>Vencidos e não liquidados</dt><dd>{money(commitments.overdue)}</dd></div>}
        {context.period === 'Future' && <div><dt>Vencem antes da competência</dt><dd>{money(commitments.beforeCompetence)}</dd></div>}
        <div><dt>{inCompetenceLabel}</dt><dd>{money(commitments.inCompetence)}</dd></div>
        {closed && commitments.paidInMonth !== null && <div><dt>Débitos efetivos no mês</dt><dd>{money(commitments.paidInMonth)}</dd></div>}
      </dl>
      {commitments.byNature.length > 0 && <details><summary>Por natureza</summary>
        <dl className="ov-cash__parts">{commitments.byNature.map(item => <div key={item.nature}><dt>{natureText[item.nature] ?? item.nature}</dt><dd>{money(item.total)}</dd></div>)}</dl></details>}
      {commitments.byDueDate.length > 0 && <details><summary>Por vencimento</summary>
        <dl className="ov-cash__parts">{commitments.byDueDate.map(item => <div key={item.date}><dt>{formatDate(item.date)}</dt><dd>{money(item.amount)}</dd></div>)}</dl></details>}
      <Caveats codes={commitments.caveats} />
    </section>

    <section aria-label="Cobertura dos compromissos">
      <h4>Cobertura <Tag tone={coverageTag[coverage.state].tone}>{coverageTag[coverage.state].label}</Tag></h4>
      {coverage.state === 'Unavailable'
        ? <p className="ov-meta">{unavailableReason(coverage.unavailableReason) ?? 'Faltam dados essenciais.'}</p>
        : <dl className="ov-cash__parts">
          {coverage.accounts.length === 1 && <div><dt>Fechamento projetado da conta</dt><dd className={isNegative(coverage.projectedClosing) ? 'ov-negative' : undefined}>{money(coverage.projectedClosing)}</dd></div>}
          <div><dt>{coverage.accounts.length > 1 ? 'Menor saldo (pior conta)' : 'Saldo mínimo projetado'}</dt><dd className={isNegative(coverage.minimumBalance) ? 'ov-negative' : undefined}>{money(coverage.minimumBalance)}</dd></div>
          {coverage.firstNegativeDate && <div><dt>Saldo negativo a partir de</dt><dd>{formatDate(coverage.firstNegativeDate)}</dd></div>}
          {coverage.firstBeyondLimitDate && <div><dt>Primeira insuficiência</dt><dd>{formatDate(coverage.firstBeyondLimitDate)}</dd></div>}
          {coverage.deficit !== null && <div><dt>Déficit</dt><dd className="ov-negative">{money(coverage.deficit)}</dd></div>}
          <div><dt>Obrigações consideradas</dt><dd>{money(coverage.obligationsConsidered)}</dd></div>
        </dl>}
      {coverage.expectedInflows.length > 0 && <details><summary>Recebimentos esperados</summary>
        <ul className="ov-decision__list">{coverage.expectedInflows.map((item, index) => <li key={`${item.date}-${item.kind}-${item.certainty}-${index}`}>
          <span>{day(item.date)} · {item.kind === 'Transfer' ? 'Transferência' : 'Receita'}{item.certainty === 'Inferred' ? ' (estimada, não garantida)' : ''}</span><strong>{money(item.amount)}</strong></li>)}</ul></details>}
      {coverage.accounts.length > 1 && <details open><summary>Risco por conta</summary>
        <ul className="ov-decision__list">{coverage.accounts.map((account: DecisionAccountCoverage) => <li key={account.accountId}>
          <span>{account.name} · {account.scope}</span><span><Tag tone={coverageTag[account.state].tone}>{coverageTag[account.state].label}</Tag>
            {account.firstBeyondLimitDate ? ` ${day(account.firstBeyondLimitDate)}` : ''}</span></li>)}</ul>
        <p className="ov-meta">Os saldos de contas diferentes não se compensam.</p></details>}
      <Caveats codes={coverage.caveats} />
    </section>

    <section aria-label="Margem segura">
      <h4>Margem segura para gastar à vista</h4>
      {safeMargin.state === 'Unavailable'
        ? <ul className="ov-decision__caveats">{safeMargin.reasons.map(code => <li key={code}>{marginReason(code)}</li>)}</ul>
        : <>
          {safeMargin.bindingConstraint && <p className="ov-meta">Limitada por: {constraintText[safeMargin.bindingConstraint] ?? safeMargin.bindingConstraint}.</p>}
          <ul className="ov-decision__list">{safeMargin.constraints.map(item => <li key={item.kind}>
            <span>{constraintText[item.kind] ?? item.kind}{item.binding ? ' (a mais restritiva)' : ''}</span>
            <strong>{item.applied ? money(item.slack) : notApplied(item.reason)}</strong></li>)}</ul>
          {safeMargin.reasons.length > 0 && <ul className="ov-decision__caveats">{safeMargin.reasons.map(code => <li key={code}>{marginReason(code)}</li>)}</ul>}
        </>}
      {safeMargin.notes.map(code => <p key={code} className="ov-meta">{marginNote[code] ?? code}</p>)}
    </section>
    <div className="ov-situation__links"><button type="button" onClick={agenda}>Ver evolução na Agenda</button><button type="button" onClick={() => onNavigate('accounts')}>Ver contas</button></div>
  </div>
}
