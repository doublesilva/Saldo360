import type { ReactNode } from 'react'
import type { FinancialDecision, MonthlyAmounts, MonthlyComponent, MonthlyResult } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import { formatDate } from '../../money'
import { OperationalError } from '../OperationalFeedback'
import type { Go } from './Overview'
import { LoadingNote, Skeleton } from './OverviewSkeleton'
import { bridgeReasonText, componentText, explain, isNegative, isPositiveAmount, money, periodLabels, resultReasonText, resultStatusText } from './overviewText'

// T14.3 — Seções A (Resultado do mês) e B (Composição do fluxo de caixa). Nada é calculado aqui: o servidor entrega entradas, saídas, resultados de caixa e orçamentário e a ponte do
// saldo; este módulo escolhe rótulos, estados e a navegação. Realizado e projetado ficam sempre em linhas rotuladas e distintas, e cor nunca é o único sinal (ícone + texto).

export interface ResultBlock { loading: boolean; error: unknown; retry: () => void }

const highlightLabel = { Past: 'Resultado de caixa do mês (realizado)', Current: 'Resultado de caixa projetado para o fechamento', Future: 'Resultado de caixa projetado' } as const
const statusSymbol = { Positive: '▲', Negative: '▼', Neutral: '=', Unavailable: '—' } as const
const statusTone = { Positive: 'positive', Negative: 'negative', Neutral: 'neutral', Unavailable: 'unavailable' } as const

export function OverviewResult({ competence, result, decision, plan, block, children }: {
  competence: string; result: MonthlyResult | null; decision: FinancialDecision | null; plan: PlanActualDashboard | null; block: ResultBlock; children?: ReactNode
}) {
  const kind = result?.periodKind ?? plan?.periodKind ?? null
  return <section className="ov-card ov-result" aria-labelledby="ov-result-title" aria-busy={block.loading || undefined}>
    <header className="ov-card__head">
      <div><h3 id="ov-result-title">Resultado do mês</h3><p className="ov-meta">Dinheiro que entrou e saiu das contas · {competence.split('-').reverse().join('/')}</p></div>
      {kind && <span className="ov-tag">{periodLabels[kind]}</span>}
    </header>
    {block.error ? <OperationalError error={block.error} onRetry={block.retry} retryLabel="Recarregar o resultado" />
      : block.loading ? <ResultSkeleton />
        : !result ? <p className="ov-empty" data-unavailable>O resultado do mês não está disponível agora. Nenhum valor foi estimado no lugar.</p>
          : <ResultBody result={result} decision={decision} plan={plan} />}
    {children}
  </section>
}

function ResultSkeleton() {
  return <div className="ov-result__loading">
    <LoadingNote>Carregando o resultado do mês</LoadingNote>
    <Skeleton width="min(14rem, 70%)" height="2.5rem" />
    <div className="ov-result__kpis">{[0, 1, 2, 3].map(index => <div key={index} className="ov-kpi"><Skeleton width="60%" height=".9rem" /><Skeleton width="80%" height="1.4rem" /><Skeleton width="50%" height=".8rem" /></div>)}</div>
  </div>
}

function ResultBody({ result, decision, plan }: { result: MonthlyResult; decision: FinancialDecision | null; plan: PlanActualDashboard | null }) {
  const past = result.periodKind === 'Past', future = result.periodKind === 'Future'
  const main = result.highlight === 'Realized' ? result.result.realized : result.result.projected
  const bank = decision?.context.bankBalance ?? null
  const closing = plan ? plan.situation.projectedClosing : undefined
  const reasons = [...result.reasons.map(reason => resultReasonText[reason]).filter(Boolean)]
  return <>
    <div className={`ov-result__main ov-result__main--${statusTone[result.status]}`}>
      <p className="ov-label">{highlightLabel[result.periodKind]}</p>
      <p className={`ov-result__value${isNegative(main) ? ' ov-negative' : ''}`} data-unavailable={main === null || undefined}>{main === null ? 'Indisponível' : money(main)}</p>
      <p className="ov-result__status"><span className={`ov-status ov-status--${statusTone[result.status]}`}><span aria-hidden="true">{statusSymbol[result.status]}</span> {resultStatusText[result.status]}</span>
        {result.state === 'Partial' && <span className="ov-tag ov-tag--warning">Parcial</span>}</p>
      {result.periodKind === 'Current' && <dl className="ov-result__split">
        <div><dt>Realizado até hoje</dt><dd className={`ov-mono${isNegative(result.result.realized) ? ' ov-negative' : ''}`}>{money(result.result.realized)}</dd></div>
        <div><dt>Ainda previsto no mês</dt><dd className={`ov-mono${isNegative(result.result.pending) ? ' ov-negative' : ''}`}>{money(result.result.pending)}</dd></div>
      </dl>}
      {future && <p className="ov-note">Mês futuro: só há projeção, nada foi realizado.</p>}
    </div>

    <div className="ov-result__kpis" role="group" aria-label="Entradas, saídas e saldos do mês">
      <Flow title="Entradas de caixa" amounts={result.inflows} pendingLabel="A receber" past={past} future={future} />
      <Flow title="Saídas de caixa" amounts={result.outflows} pendingLabel="A pagar" past={past} future={future} />
      <article className="ov-kpi" aria-label="Saldo bancário observado">
        <h4 className="ov-label">Saldo bancário observado</h4>
        <p className={`ov-kpi__value${isNegative(bank) ? ' ov-negative' : ''}`} data-unavailable={bank === null || undefined}>{bank === null ? 'Indisponível' : money(bank)}</p>
        <p className="ov-kpi__caption">{bank === null ? 'O banco ainda não informou o saldo.' : decision?.context.bankPositionDate ? `Posição do banco em ${formatDate(decision.context.bankPositionDate)}` : 'Informado pelo banco'}
          {isNegative(bank) && <> · <span className="ov-tag ov-tag--danger">Saldo negativo</span></>}</p>
      </article>
      <article className="ov-kpi" aria-label={past ? 'Saldo no fechamento' : 'Saldo projetado no fechamento'}>
        <h4 className="ov-label">{past ? 'Saldo no fechamento' : 'Saldo projetado no fechamento'}</h4>
        {closing === undefined ? <Skeleton width="70%" height="1.4rem" />
          : <p className={`ov-kpi__value${isNegative(closing) ? ' ov-negative' : ''}`} data-unavailable={closing === null || undefined}>{closing === null ? 'Indisponível' : money(closing)}</p>}
        <p className="ov-kpi__caption">{closing === null && plan ? explain(plan.situation.reasons)[0] ?? 'Ainda não é possível calcular.' : past ? 'Saldo realizado no último dia do mês' : 'Projeção: não é o saldo do banco'}
          {isNegative(closing) && <> · <span className="ov-tag ov-tag--danger">Saldo negativo</span></>}</p>
      </article>
    </div>

    <p className="ov-result__budget" aria-label="Resultado orçamentário">
      <span>Resultado orçamentário {past ? '(realizado)' : '(projetado)'}: receitas menos consumo da competência</span>
      <strong className={`ov-mono${isNegative(past ? result.budget.realized : result.budget.projected) ? ' ov-negative' : ''}`}>{money(past ? result.budget.realized : result.budget.projected)}</strong>
      <small>Não é o resultado de caixa: compras no cartão contam no mês da fatura e o pagamento da fatura não é novo consumo.</small>
    </p>

    <Bridge result={result} past={past} />
    {reasons.length > 0 && <ul className="ov-note-list">{reasons.map(text => <li key={text} className="ov-note">{text}</li>)}</ul>}
  </>
}

function Flow({ title, amounts, pendingLabel, past, future }: { title: string; amounts: MonthlyAmounts; pendingLabel: string; past: boolean; future: boolean }) {
  return <article className="ov-kpi" aria-label={title}>
    <h4 className="ov-label">{title}</h4>
    <dl className="ov-flow">
      {!future && <div><dt>Realizado</dt><dd className="ov-mono">{money(amounts.realized)}</dd></div>}
      {(!past || isPositiveAmount(amounts.pending)) && <div><dt>{pendingLabel}</dt><dd className="ov-mono ov-muted">{money(amounts.pending)}</dd></div>}
      {!past && <div className="ov-flow__total"><dt>Previsto no mês</dt><dd className="ov-mono">{money(amounts.projected)}</dd></div>}
    </dl>
  </article>
}

/** Do saldo observado ao fechamento projetado. Se os dados não fecham a conta, a diferença aparece como está, com a causa, e nunca é escondida por um ajuste. */
function Bridge({ result, past }: { result: MonthlyResult; past: boolean }) {
  const bridge = result.bridge
  if (past) return null
  return <details className="ov-details ov-bridge">
    <summary>Do saldo observado ao fechamento projetado{bridge.state === 'Partial' && <span className="ov-tag ov-tag--warning">Parcial</span>}</summary>
    {bridge.state === 'Unavailable' ? <p className="ov-note">{bridge.reasons.map(reason => bridgeReasonText[reason]).filter(Boolean).join(' ') || 'Ponte indisponível.'}</p>
      : <>
        <dl className="ov-bridge__rows">
          <div><dt>Saldo observado{bridge.observedDate ? ` em ${formatDate(bridge.observedDate)}` : ''}</dt><dd className="ov-mono">{money(bridge.observedBalance)}</dd></div>
          <div><dt>+ Entradas previstas depois da referência</dt><dd className="ov-mono">{money(bridge.inflowsAfter)}</dd></div>
          <div><dt>− Saídas previstas depois da referência</dt><dd className="ov-mono">{money(bridge.outflowsAfter)}</dd></div>
          {bridge.unreconciled !== null && <div className="ov-bridge__gap"><dt>Diferença não explicada pelos dados</dt><dd className="ov-mono">{money(bridge.unreconciled)}</dd></div>}
          <div className="ov-flow__total"><dt>Saldo projetado no fechamento</dt><dd className="ov-mono">{money(bridge.projectedClosing)}</dd></div>
        </dl>
        {bridge.reasons.length > 0 && <p className="ov-note">{bridge.reasons.map(reason => bridgeReasonText[reason]).filter(Boolean).join(' ')}</p>}
      </>}
  </details>
}

const lastDay = (competence: string) => {
  const [year, month] = competence.split('-').map(Number)
  return `${competence}-${String(new Date(year, month, 0).getDate()).padStart(2, '0')}`
}

/** B — Composição: cada total se abre nas partes que o formam, com a origem e o caminho para conferir. As partes não repetem o que já está em outro total. */
export function OverviewComposition({ competence, result, scopeAccountId, consolidated, block, onNavigate }: { competence: string; result: MonthlyResult | null; scopeAccountId: string | null; consolidated: boolean; block: ResultBlock; onNavigate: Go }) {
  const open = (target: MonthlyComponent['target']) => () => target === 'Calendar' ? onNavigate('calendar', { month: competence })
    : target === 'Cards' ? onNavigate('cards') : onNavigate('transactions', { transactions: { from: `${competence}-01`, to: lastDay(competence), accountId: scopeAccountId } })
  const targetLabel = { Calendar: 'Ver na Agenda', Cards: 'Ver em Cartões', Entries: 'Ver nos Lançamentos' } as const
  return <section className="ov-card ov-composition" aria-labelledby="ov-composition-title" aria-busy={block.loading || undefined}>
    <header className="ov-card__head"><div><h3 id="ov-composition-title">Composição do fluxo de caixa</h3><p className="ov-meta">De onde vêm as entradas e para onde vão as saídas. Cada parte aparece uma única vez.</p></div></header>
    {block.error ? <OperationalError error={block.error} onRetry={block.retry} retryLabel="Recarregar a composição" />
      : block.loading ? <div className="ov-composition__grid"><LoadingNote>Carregando a composição do fluxo de caixa</LoadingNote><Skeleton width="100%" height="3rem" /><Skeleton width="100%" height="3rem" /></div>
        : !result ? <p className="ov-empty" data-unavailable>A composição não está disponível agora.</p>
          : <div className="ov-composition__grid">
            {(['Inflow', 'Outflow'] as const).map(side => {
              const parts = result.components.filter(component => component.side === side)
              const total = side === 'Inflow' ? result.inflows.projected : result.outflows.projected
              const title = side === 'Inflow' ? 'Entradas' : 'Saídas'
              return <details key={side} className="ov-details ov-composition__side">
                <summary><span>{title} · {result.periodKind === 'Past' ? 'realizadas' : 'previstas no mês'}</span><strong className="ov-mono">{money(total)}</strong></summary>
                <ul className="ov-parts">{parts.map(part => <li key={part.code}>
                  <div><span>{componentText[part.code] ?? part.code}</span><small className="ov-muted">{part.status === 'Realized' ? 'Realizado' : 'Previsto'}</small></div>
                  <span className="ov-mono">{money(part.amount)}</span>
                  <button type="button" className="ov-link" onClick={open(part.target)}>{targetLabel[part.target]} ›</button>
                </li>)}</ul>
                {parts.length === 0 && <p className="ov-note">Nenhuma parte neste mês.</p>}
              </details>
            })}
            {consolidated && <p className="ov-note ov-composition__note">Transferências entre contas do consolidado são neutras e não aparecem como entrada nem saída.</p>}
          </div>}
  </section>
}
