import type { OverviewCashAccountIssue, OverviewCashFlow, OverviewRecovery, RecoveryBand } from '../../api/overview'
import { formatDate } from '../../money'
import type { OverviewNavigation } from './Overview'
import { barWidth, dateTime, isNegative, money, percentNow, percentText } from './overviewText'

// Fluxo de caixa da Visão Geral. É CAIXA (movimentações efetivas): nada é somado aqui, o servidor entrega abertura, entradas, saídas, fechamento, posição do banco e conciliação.
// Orçamento por competência fica em outra seção e nunca explica o saldo bancário.

const reasonText: Record<string, string> = {
  OpeningBalanceNotConfigured: 'sem saldo inicial configurado', BeforeOpeningBalanceDate: 'período anterior à data do saldo inicial', BeforeCalculableStart: 'período anterior ao início calculável',
  NoBankPosition: 'sem posição informada pelo banco', CutoffMismatch: 'as contas têm datas de corte diferentes no banco', AccountsNotComparable: 'histórico incompleto ou movimentos em revisão',
  BankDataUnavailable: 'dados do banco indisponíveis', InsufficientBankReference: 'referência bancária insuficiente', IncompleteHistory: 'histórico incompleto', MovementsInReview: 'movimentos aguardando revisão', CalculatedUnavailable: 'saldo calculado indisponível',
}
const why = (reason: string) => reasonText[reason] ?? reason
const day = (iso: string) => formatDate(iso).slice(0, 5)
const issues = (list: OverviewCashAccountIssue[]) => list.map(item => `${item.name} (${why(item.reason)})`).join('; ')

type Go = (section: 'transactions', context: OverviewNavigation) => void

export function OverviewCashFlowSection({ flow, scopeAccountId, onNavigate }: { flow: OverviewCashFlow; scopeAccountId: string | null; onNavigate: Go }) {
  const open = () => onNavigate('transactions', { transactions: { from: flow.from, to: flow.through, accountId: scopeAccountId } })
  const noOpening = flow.opening === null
  return <section className="ov-cash" aria-labelledby="ov-cash-title">
    <header className="ov-cash__head"><div><h2 id="ov-cash-title">Fluxo de caixa · movimentações efetivas</h2>
      <p className="ov-meta">Inclui transferências recebidas e enviadas na perspectiva selecionada. Lançamentos ainda sem categoria também entram.</p></div></header>
    {!flow.snapshotConsistent && <p className="ov-notice ov-notice--warning" role="status">Os dados mudaram durante a leitura. Atualize para ver um retrato consistente.</p>}
    <div className="ov-cash__cards" role="group" aria-label="Fluxo de caixa do período">
      <article className="ov-card ov-indicator" aria-label="Saldo de abertura">
        <h3>Saldo de abertura · {day(flow.from)}</h3>
        <p className="ov-indicator__value" data-unavailable={noOpening || undefined}>{noOpening ? 'Não apurado' : money(flow.opening)}</p>
        <p className="ov-indicator__reference">{noOpening ? issues(flow.openingMissing) || 'Sem contas no recorte.' : 'Saldo no início do primeiro dia do período'}</p>
        {flow.recovery && <RecoveryTarget recovery={flow.recovery} />}
      </article>
      <article className="ov-card ov-indicator ov-indicator--income" aria-label="Entradas de caixa">
        <h3>Entradas de caixa</h3>
        <p className="ov-indicator__value">{money(flow.inflows.total)}</p>
        <p className="ov-indicator__reference">Receitas externas + devoluções + transferências recebidas</p>
        <dl className="ov-cash__parts"><div><dt>Créditos efetivos</dt><dd>{money(flow.inflows.credits)}</dd></div>
          {flow.inflows.refunds !== null && flow.inflows.refunds !== '0.00' && <div><dt>dos quais devoluções e estornos</dt><dd>{money(flow.inflows.refunds)}</dd></div>}
          <div><dt>Transferências recebidas</dt><dd>{money(flow.inflows.transfersReceived)}</dd></div></dl>
        <button type="button" onClick={open}>Ver lançamentos</button>
      </article>
      <article className="ov-card ov-indicator ov-indicator--expense" aria-label="Saídas de caixa">
        <h3>Saídas de caixa</h3>
        <p className="ov-indicator__value">{money(flow.outflows.total)}</p>
        <p className="ov-indicator__reference">Pagamentos + débitos + transferências enviadas</p>
        <dl className="ov-cash__parts"><div><dt>Débitos efetivos</dt><dd>{money(flow.outflows.debits)}</dd></div>
          <div><dt>Transferências enviadas</dt><dd>{money(flow.outflows.transfersSent)}</dd></div></dl>
        <button type="button" onClick={open}>Ver lançamentos</button>
      </article>
      <article className={`ov-card ov-indicator${isNegative(flow.closing) ? ' ov-indicator--negative' : ''}`} aria-label={flow.recovery && flow.inProgress ? 'Saldo parcial' : 'Saldo de fechamento'}>
        <h3>{flow.recovery && flow.inProgress ? `Saldo parcial · ${day(flow.recovery.currentAsOf)}` : `Saldo de fechamento · ${day(flow.through)}`}</h3>
        <p className="ov-indicator__value" data-unavailable={flow.closing === null || undefined}>{flow.closing === null ? 'Não apurado' : money(flow.closing)}</p>
        <p className="ov-indicator__reference">{flow.closing === null ? 'Depende do saldo de abertura.' : flow.inProgress ? 'Mês em andamento: abertura + entradas − saídas até agora' : 'Abertura + entradas − saídas'}</p>
        {flow.recovery && <RecoveryProgress recovery={flow.recovery} inProgress={flow.inProgress} />}
      </article>
    </div>
  </section>
}

const bandLabel: Record<RecoveryBand, string> = {
  Exceeded: 'Acima da meta', Met: 'Meta atingida (80% a 100%)', Attention: 'Atenção (acima de 50% e abaixo de 80%)', Critical: 'Crítico (até 50%)', NoTarget: 'Sem meta',
}
const pendingText: Record<string, string> = {
  ProjectionUnavailable: 'projeção indisponível', CardCoverageIncomplete: 'faturas de cartão ainda não fechadas',
}

/** Meta de recuperação do mês no card de abertura: valor, estado (Provisória | Fixada) e data de fixação, todos do servidor. */
function RecoveryTarget({ recovery: r }: { recovery: OverviewRecovery }) {
  const fixedAt = dateTime(r.targetFixedAt)
  return <dl className="ov-cash__parts ov-recovery" aria-label="Meta de recuperação do cheque especial">
    <div><dt>Cheque especial usado na abertura</dt><dd>{money(r.openingUsed)}</dd></div>
    <div><dt>Meta de recuperação do mês</dt><dd data-unavailable={r.target === null || undefined}>{r.target === null ? 'Pendente' : money(r.target)}</dd></div>
    <div><dt>Estado da meta</dt><dd><span className={`ov-tag${r.targetState === 'Fixed' ? ' ov-tag--ok' : ' ov-tag--warning'}`}>
      {r.targetState === 'Fixed' ? 'Fixada' : r.targetState === 'Provisional' ? 'Provisória' : r.targetState === 'Pending' ? 'Pendente' : 'Não registrada'}</span></dd></div>
    {r.targetState === 'Fixed' && fixedAt && <div><dt>{r.legacyTarget ? 'Registrada anteriormente em' : 'Fixada em'}</dt><dd>{fixedAt}{r.legacyTarget ? ' (meta histórica)' : ''}</dd></div>}
    {r.targetState === 'Provisional' && <div><dt>Será fixada em</dt><dd>{formatDate(r.fixDate)}</dd></div>}
    {r.targetState === 'Pending' && <div><dt>Pendência</dt><dd>{r.pendingReason ? pendingText[r.pendingReason] ?? r.pendingReason : 'dados insuficientes'}</dd></div>}
  </dl>
}

/** Recuperação parcial/final no card de fechamento: valores, percentual vs. meta, faixa em texto e barra limitada a 0–100%. */
function RecoveryProgress({ recovery: r, inProgress }: { recovery: OverviewRecovery; inProgress: boolean }) {
  const percent = inProgress ? r.projectedAchievementPercent : r.finalAchievementPercent
  const text = percentText(percent)
  const label = `${inProgress ? 'Recuperação prevista' : 'Recuperação final'} ${text ?? 'sem percentual'}: ${bandLabel[r.band]}`
  return <div className={`ov-recovery ov-recovery--${r.band}`}>
    <dl className="ov-cash__parts">
      {inProgress ? <>
        <div><dt>Saldo efetivo em {formatDate(r.currentAsOf)}</dt><dd>{money(r.currentBalance)}</dd></div>
        <div><dt>Recuperação em andamento</dt><dd>{money(r.recoveryCurrent)}</dd></div>
        <div><dt>Recuperação prevista no encerramento</dt><dd data-unavailable={r.projectedRecovery === null || undefined}>{money(r.projectedRecovery)}</dd></div>
      </> : <div><dt>Recuperação final</dt><dd>{money(r.finalRecovery)}</dd></div>}
      <div><dt>{inProgress ? 'Meta vigente' : 'Meta fixada'}</dt><dd>{r.target === null ? 'Sem meta' : money(r.target)}</dd></div>
    </dl>
    {text !== null ? <>
      <p className="ov-recovery__head"><strong>◎ Recuperação do saldo</strong><strong className="ov-recovery__percent">{text}</strong></p>
      <div className="ov-bar-track ov-recovery__track" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(percent)} aria-valuetext={label}>
        <span style={{ width: barWidth(percent) }} /></div>
      <p className="ov-recovery__values"><span>{money(inProgress ? r.projectedRecovery : r.finalRecovery)} / {money(r.target)}</span><span>{r.band === 'Exceeded' ? 'Acima da meta' : bandLabel[r.band]}</span></p>
    </> : <p className="ov-meta">{r.targetState === 'Pending' || r.targetState === 'NotRegistered' ? 'Meta ainda não disponível: sem percentual.' : 'Sem meta: sem percentual.'}</p>}
  </div>
}

/** Orçamento por competência: seção independente, nunca usada para conciliar o caixa. */
export function OverviewCompetenceSection({ flow }: { flow: OverviewCashFlow }) {
  const c = flow.byCompetence
  return <section className="ov-card ov-cash__competence" aria-labelledby="ov-competence-title">
    <h2 id="ov-competence-title">Orçamento · receitas e despesas por competência <span className="ov-tag">Não explica, sozinho, o saldo bancário</span></h2>
    <dl className="ov-cash__parts"><div><dt>Receitas classificadas</dt><dd>{money(c.classifiedIncome)}</dd></div><div><dt>Despesas por competência</dt><dd>{money(c.expenses)}</dd></div><div><dt>Diferença</dt><dd>{money(c.difference)}</dd></div></dl>
    <p className="ov-meta">Cartão entra no mês da fatura. Pagamento de fatura, transferências e movimentos ainda sem classificação seguem as regras do caixa acima.</p>
  </section>
}
