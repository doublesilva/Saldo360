import type { DecisionAccountCoverage, FinancialDecision } from '../../api/overview'
import { formatDate } from '../../money'
import type { OverviewNavigation, OverviewSection } from './Overview'
import { caveat, constraintText, coverageMessage, coverageTag, marginMessage, marginNote, marginReason, natureText, notApplied, periodText, recoveryNotApplicable, recoveryStateText, unavailableReason } from './decisionText'
import { dateTime, isNegative, monthTitle, money } from './overviewText'

// T11.1: segurança financeira da Visão Geral. Tudo vem do servidor (compromissos, cobertura, margem, meta): aqui só se apresenta, com texto que não depende de cor.

type Go = (section: OverviewSection, context?: OverviewNavigation) => void
const day = (iso: string) => formatDate(iso).slice(0, 5)

function Tag({ tone, children }: { tone: 'ok' | 'warning' | 'danger' | 'neutral'; children: string }) {
  return <span className={`ov-tag${tone === 'neutral' ? '' : ` ov-tag--${tone}`}`}>{children}</span>
}

function Caveats({ codes }: { codes: string[] }) {
  return codes.length === 0 ? null : <ul className="ov-decision__caveats">{codes.map(code => <li key={code}>{caveat(code)}</li>)}</ul>
}

export function OverviewDecisionSection({ decision, onNavigate }: { decision: FinancialDecision; onNavigate: Go }) {
  const { context, commitments, coverage, safeMargin, recovery } = decision
  const closed = context.period === 'Closed'
  const consolidated = context.scopeKind === 'all'
  const inCompetenceLabel = closed ? 'Ainda em aberto hoje, com vencimento no mês' : context.period === 'Future' ? 'Vencem na competência' : 'A vencer até o fim da competência'
  const scopeText = consolidated ? 'Todas as contas (consolidado)' : `${context.accountName ?? 'Conta'}${context.accountScope ? ` · ${context.accountScope}` : ''}`
  const agenda = () => onNavigate('calendar', { month: context.competence })

  return <section className="ov-decision" aria-labelledby="ov-decision-title">
    <header className="ov-decision__head"><h2 id="ov-decision-title">Segurança financeira</h2></header>

    <dl className="ov-decision__context" aria-label="Contexto da análise">
      <div><dt>Competência</dt><dd>{monthTitle(context.competence)} <Tag tone="neutral">{periodText[context.period]}</Tag>
        {context.competence !== context.currentCompetence && <span className="ov-meta"> Vigente: {monthTitle(context.currentCompetence)}</span>}</dd></div>
      <div><dt>Conta</dt><dd>{scopeText}</dd></div>
      <div><dt>Posição bancária</dt><dd>{context.bankPositionDate ? `em ${formatDate(context.bankPositionDate)}` : 'sem posição informada pelo banco'}</dd></div>
      <div><dt>Última sincronização</dt><dd>{context.lastSyncedAt ? dateTime(context.lastSyncedAt) : 'sem sincronização'}</dd></div>
    </dl>
    {!decision.snapshotConsistent && <p className="ov-notice ov-notice--warning" role="status">{caveat('PositionsInconsistent')}</p>}

    <div className="ov-decision__cards" role="group" aria-label="Indicadores de segurança financeira">
      <article className="ov-card ov-indicator" aria-label="Posição bancária observada">
        <h3>Saldo bancário observado</h3>
        <p className="ov-indicator__value" data-unavailable={context.bankBalance === null || undefined}>{context.bankBalance === null ? 'Sem posição do banco' : money(context.bankBalance)}</p>
        <dl className="ov-cash__parts"><div><dt>Cheque especial utilizado</dt><dd data-unavailable={context.overdraftUsed === null || undefined}>{context.overdraftUsed === null ? 'Indisponível' : money(context.overdraftUsed)}</dd></div></dl>
        <button type="button" onClick={() => onNavigate('accounts')}>Ver contas</button>
      </article>

      <article className="ov-card ov-indicator" aria-label="Compromissos restantes">
        <h3>Compromissos restantes</h3>
        <p className="ov-indicator__value">{money(commitments.total)}</p>
        <p className="ov-indicator__reference">{closed ? 'Ainda em aberto hoje, com vencimento no mês' : 'Ainda falta pagar'}{commitments.reliability === 'Preliminary' ? ' · estimativa preliminar' : ''}</p>
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
        <button type="button" onClick={agenda}>Ver na Agenda</button>
      </article>

      <article className={`ov-card ov-indicator ov-decision__coverage ov-decision__coverage--${coverage.state}`} aria-label="Cobertura dos compromissos">
        <h3>Cobertura dos compromissos <Tag tone={coverageTag[coverage.state].tone}>{coverageTag[coverage.state].label}</Tag></h3>
        <p className="ov-indicator__value ov-decision__message" role="status">{coverageMessage(coverage)}{coverage.reliability === 'Preliminary' ? ' (preliminar)' : ''}</p>
        {coverage.state === 'Unavailable'
          ? <p className="ov-meta">{unavailableReason(coverage.unavailableReason) ?? 'Faltam dados essenciais.'}</p>
          : <dl className="ov-cash__parts">
            {coverage.accounts.length === 1 && <div><dt>Fechamento projetado</dt><dd className={isNegative(coverage.projectedClosing) ? 'ov-negative' : undefined}>{money(coverage.projectedClosing)}</dd></div>}
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
        <button type="button" onClick={agenda}>Ver evolução na Agenda</button>
      </article>

      <article className="ov-card ov-indicator ov-decision__margin" aria-label="Margem segura para gastar">
        <h3>Margem segura para gastar {safeMargin.state !== 'Unavailable' && <Tag tone={safeMargin.state === 'Calculated' ? 'ok' : 'warning'}>{safeMargin.state === 'Calculated' ? 'Calculada' : 'Preliminar'}</Tag>}</h3>
        {safeMargin.state === 'Unavailable'
          ? <><p className="ov-indicator__value ov-decision__message" data-unavailable role="status">{marginMessage(safeMargin)}</p>
            <ul className="ov-decision__caveats">{safeMargin.reasons.map(code => <li key={code}>{marginReason(code)}</li>)}</ul></>
          : <>
            <p className="ov-indicator__value">{money(safeMargin.amount)}</p>
            <p className="ov-indicator__reference">à vista, hoje ({formatDate(safeMargin.referenceDate)}), sem comprometer os pagamentos nem a meta</p>
            {safeMargin.bindingConstraint && <p className="ov-meta">Limitada por: {constraintText[safeMargin.bindingConstraint] ?? safeMargin.bindingConstraint}.</p>}
            <details><summary>Restrições consideradas</summary>
              <ul className="ov-decision__list">{safeMargin.constraints.map(item => <li key={item.kind}>
                <span>{constraintText[item.kind] ?? item.kind}{item.binding ? ' (a mais restritiva)' : ''}</span>
                <strong>{item.applied ? money(item.slack) : notApplied(item.reason)}</strong></li>)}</ul></details>
            {safeMargin.reasons.length > 0 && <ul className="ov-decision__caveats">{safeMargin.reasons.map(code => <li key={code}>{marginReason(code)}</li>)}</ul>}
          </>}
        {safeMargin.notes.map(code => <p key={code} className="ov-meta">{marginNote[code] ?? code}</p>)}
        <button type="button" onClick={() => onNavigate('accounts')}>Ver contas</button>
      </article>

      <article className="ov-card ov-indicator ov-decision__recovery" aria-label="Meta de recuperação">
        <h3>Meta de recuperação <Tag tone={recovery.state === 'Fixed' ? 'ok' : recovery.state === 'NotApplicable' ? 'neutral' : 'warning'}>{recoveryStateText[recovery.state]}</Tag></h3>
        {recovery.state === 'NotApplicable' || recovery.target === null && recovery.openingUsed === null
          ? <p className="ov-meta">{recoveryNotApplicable[recovery.reason ?? ''] ?? 'Sem meta de recuperação nesta competência.'}</p>
          : <dl className="ov-cash__parts">
            <div><dt>Cheque especial na abertura</dt><dd>{money(recovery.openingUsed)}</dd></div>
            <div><dt>Meta original</dt><dd data-unavailable={recovery.target === null || undefined}>{recovery.target === null ? 'Indisponível' : money(recovery.target)}</dd></div>
            <div><dt>Recuperação efetiva até hoje</dt><dd>{money(recovery.effectiveRecovery)}</dd></div>
            <div><dt>Recuperação projetada</dt><dd>{money(recovery.projectedRecovery)}</dd></div>
            <div><dt>Diferença para a meta</dt><dd className={isNegative(recovery.gapToTarget) ? 'ov-negative' : undefined}>{money(recovery.gapToTarget)}{recovery.gapToTarget === null ? '' : isNegative(recovery.gapToTarget) ? ' · abaixo da meta' : ' · dentro da meta'}</dd></div>
            <div><dt>Utilização esperada no fechamento</dt><dd>{money(recovery.expectedClosingUsed)}</dd></div>
          </dl>}
        {recovery.state === 'Fixed' && recovery.targetFixedAt && <p className="ov-meta">{recovery.legacyTarget ? 'Meta histórica registrada em ' : 'Fixada em '}{dateTime(recovery.targetFixedAt)}. Não é recalculada.</p>}
        <button type="button" onClick={() => onNavigate('planning', { planningTab: 'projection' })}>Abrir a Projeção financeira</button>
      </article>
    </div>
  </section>
}
