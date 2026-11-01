import type { OverviewCashAccountIssue, OverviewCashFlow } from '../../api/overview'
import { formatDate } from '../../money'
import type { OverviewNavigation } from './Overview'
import { dateTime, isNegative, money } from './overviewText'

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
  const open = (type?: 'Transfer') => onNavigate('transactions', { transactions: { from: flow.from, to: flow.through, accountId: scopeAccountId, ...(type ? { type } : {}) } })
  const noOpening = flow.opening === null
  const notReconcilable = noOpening ? `Não é possível conciliar: ${issues(flow.openingMissing) || 'sem contas com saldo conhecido'}.` : null
  return <section className="ov-cash" aria-labelledby="ov-cash-title">
    <header className="ov-cash__head"><div><h2 id="ov-cash-title">Fluxo de caixa · movimentações efetivas</h2>
      <p className="ov-meta">Inclui transferências recebidas e enviadas na perspectiva selecionada. Lançamentos ainda sem categoria também entram.</p></div></header>
    {!flow.snapshotConsistent && <p className="ov-notice ov-notice--warning" role="status">Os dados mudaram durante a leitura. Atualize para ver um retrato consistente.</p>}
    <div className="ov-cash__cards" role="group" aria-label="Fluxo de caixa do período">
      <article className="ov-card ov-indicator" aria-label="Saldo de abertura">
        <h3>Saldo de abertura · {day(flow.from)}</h3>
        <p className="ov-indicator__value" data-unavailable={noOpening || undefined}>{noOpening ? 'Não apurado' : money(flow.opening)}</p>
        <p className="ov-indicator__reference">{noOpening ? issues(flow.openingMissing) || 'Sem contas no recorte.' : 'Saldo no início do primeiro dia do período'}</p>
      </article>
      <article className="ov-card ov-indicator ov-indicator--income" aria-label="Entradas de caixa">
        <h3>Entradas de caixa</h3>
        <p className="ov-indicator__value">{money(flow.inflows.total)}</p>
        <p className="ov-indicator__reference">Receitas externas + devoluções + transferências recebidas</p>
        <dl className="ov-cash__parts"><div><dt>Créditos efetivos</dt><dd>{money(flow.inflows.credits)}</dd></div>
          {flow.inflows.refunds !== null && flow.inflows.refunds !== '0.00' && <div><dt>dos quais devoluções e estornos</dt><dd>{money(flow.inflows.refunds)}</dd></div>}
          <div><dt>Transferências recebidas</dt><dd>{money(flow.inflows.transfersReceived)}</dd></div></dl>
        <button type="button" onClick={() => open()}>Ver lançamentos</button>
      </article>
      <article className="ov-card ov-indicator ov-indicator--expense" aria-label="Saídas de caixa">
        <h3>Saídas de caixa</h3>
        <p className="ov-indicator__value">{money(flow.outflows.total)}</p>
        <p className="ov-indicator__reference">Pagamentos + débitos + transferências enviadas</p>
        <dl className="ov-cash__parts"><div><dt>Débitos efetivos</dt><dd>{money(flow.outflows.debits)}</dd></div>
          <div><dt>Transferências enviadas</dt><dd>{money(flow.outflows.transfersSent)}</dd></div></dl>
        <button type="button" onClick={() => open()}>Ver lançamentos</button>
      </article>
      <article className={`ov-card ov-indicator${isNegative(flow.closing) ? ' ov-indicator--negative' : ''}`} aria-label="Saldo de fechamento">
        <h3>Saldo de fechamento · {day(flow.through)}</h3>
        <p className="ov-indicator__value" data-unavailable={flow.closing === null || undefined}>{flow.closing === null ? 'Não apurado' : money(flow.closing)}</p>
        <p className="ov-indicator__reference">{flow.closing === null ? 'Depende do saldo de abertura.' : flow.inProgress ? 'Mês em andamento: abertura + entradas − saídas até agora' : 'Abertura + entradas − saídas'}</p>
      </article>
    </div>

    <BankPosition flow={flow} />

    <div className="ov-cash__why">
      <h2 className="ov-cash__title">De onde veio o saldo? <ReconBadge flow={flow} /></h2>
      <div className="ov-cash__why-grid">
        <section className="ov-card" aria-labelledby="ov-bridge-title">
          <h3 id="ov-bridge-title">Ponte de saldo</h3>
          <div className="ov-table-wrap"><table className="ov-table"><caption className="sr-only">Ponte de saldo do período</caption><tbody>
            <tr><th scope="row">Saldo de abertura · {day(flow.from)}</th><td>{noOpening ? 'Não apurado' : money(flow.opening)}</td></tr>
            <tr><th scope="row">+ Recebimentos e outros créditos efetivos</th><td>{money(flow.inflows.credits)}</td></tr>
            <tr><th scope="row">+ Transferência recebida</th><td>{money(flow.inflows.transfersReceived)}</td></tr>
            <tr><th scope="row">− Pagamentos e outros débitos efetivos</th><td>{money(flow.outflows.debits)}</td></tr>
            <tr><th scope="row">− Transferência enviada</th><td>{money(flow.outflows.transfersSent)}</td></tr>
            <tr><th scope="row">= Saldo de fechamento · {day(flow.through)}</th><td><strong>{flow.closing === null ? 'Não apurado' : money(flow.closing)}</strong></td></tr>
          </tbody></table></div>
          {notReconcilable && <p className="ov-meta" role="status">{notReconcilable}</p>}
          <Reconciliation flow={flow} />
          <p className="ov-meta">Só movimentações efetivas. Receitas e despesas por competência (orçamento) não entram nesta ponte; a fatura do cartão afeta o caixa quando é paga, sem duplicar a despesa do orçamento.</p>
        </section>
        <section className="ov-card" aria-labelledby="ov-ident-title">
          <h3 id="ov-ident-title">Transferência identificada</h3>
          {flow.transfers.length === 0 ? <p className="ov-empty">Nenhuma transferência efetiva neste período.</p>
            : <ul className="ov-list">{flow.transfers.map(item => <li key={item.id}><span className="ov-row">
              <span className="ov-row__main"><strong>{item.source} → {item.destination}</strong><small>{formatDate(item.date)} · {item.description}</small>
                <small>Efeito nesta perspectiva: {item.relation === 'Internal' ? 'neutro (as duas contas estão no conjunto)' : item.relation === 'Inbound' ? 'entrada' : 'saída'} · {money(item.effect)}</small></span>
              <span className="ov-amount">{money(item.amount)}</span></span></li>)}</ul>}
          <p className="ov-meta">Continua do tipo Transferência: não vira receita nem despesa e não gera lançamentos duplicados. O efeito líquido entre as duas contas é zero{flow.internalMoved !== '0.00' ? ` (${money(flow.internalMoved)} movimentados dentro deste conjunto)` : ''}.</p>
          <button type="button" onClick={() => open('Transfer')}>Ver transferências</button>
        </section>
      </div>
    </div>
  </section>
}

function BankPosition({ flow }: { flow: OverviewCashFlow }) {
  const bank = flow.bankPosition
  const at = dateTime(bank.observedAt)
  return <section className="ov-card ov-cash__bank" aria-label="Posição bancária mais recente">
    <h3>Posição bancária mais recente</h3>
    {bank.total !== null ? <><p className="ov-indicator__value">{money(bank.total)}</p><p className="ov-indicator__reference">{at ? `Banco atualizado em ${at}` : 'Data da atualização indisponível'}</p></>
      : <><p className="ov-indicator__value" data-unavailable>Indisponível</p>
        <p className="ov-indicator__reference">{bank.state === 'Unavailable' ? 'Nenhuma conta do recorte informou saldo ao banco.' : `Faltam: ${issues(bank.missing)}.`}</p></>}
    {bank.accounts.length > 1 && <ul className="ov-list">{bank.accounts.map(account => <li key={account.accountId}><span className="ov-row"><span className="ov-row__main"><strong>{account.name}</strong><small>Atualizado em {dateTime(account.observedAt)}</small></span><span className="ov-amount">{money(account.amount)}</span></span></li>)}</ul>}
    <p className="ov-meta">Saldo de fechamento da competência, saldo bancário atual e saldo projetado são medidas diferentes; o limite do cheque especial é crédito do banco e não compõe o saldo.</p>
  </section>
}

function ReconBadge({ flow }: { flow: OverviewCashFlow }) {
  const status = flow.reconciliation.status
  return <span className={`ov-tag ${status === 'Verified' ? 'ov-tag--ok' : status === 'Divergent' ? 'ov-tag--danger' : 'ov-tag--warning'}`}>
    {status === 'Verified' ? 'Reconciliação verificada' : status === 'Divergent' ? 'Reconciliação com diferença' : 'Reconciliação não verificada'}</span>
}

function Reconciliation({ flow }: { flow: OverviewCashFlow }) {
  const r = flow.reconciliation
  if (r.status === 'NotVerifiable') return <p className="ov-meta" role="status">Conciliação com o banco: não verificada{r.reasons.length > 0 ? ` (${r.reasons.map(why).join('; ')})` : ''}. {issues(r.accounts)}</p>
  return <dl className="ov-meta"><div><dt>Saldo do banco{r.cutoffDate ? ` em ${formatDate(r.cutoffDate)}` : ''}</dt><dd>{money(r.bank)}</dd></div>
    <div><dt>Saldo calculado na mesma data</dt><dd>{money(r.calculated)}</dd></div>
    <div><dt>Diferença de conciliação</dt><dd>{money(r.difference)}</dd></div></dl>
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
