import type { OverviewAccounts, OverviewCommitments, OverviewGoals, OverviewRecent } from '../../api/overview'
import { formatDate } from '../../money'
import { todayInSaoPaulo } from '../../dates'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { barWidth, exceedsHundred, isNegative, isPositiveAmount, money, percentNow, percentText, unavailableBalanceReasons } from './overviewText'

// Dashboard Folio: blocos compactos de apoio (contas, próximos compromissos, metas e últimos lançamentos). Só listas e totais que o servidor já calculou.

type Block = { loading: boolean; error: unknown; retry: () => void }
const MONTHS = ['jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez']
const accountTypes: Record<string, string> = { Checking: 'Conta corrente', Savings: 'Poupança', Investment: 'Investimento', Cash: 'Dinheiro', Wallet: 'Carteira digital' }

function State({ block, what }: { block: Block; what: string }) {
  return block.error ? <OperationalError error={block.error} onRetry={block.retry} retryLabel="Recarregar" />
    : block.loading ? <OperationalLoading>{`Carregando ${what}…`}</OperationalLoading> : <p className="ov-empty">{`Não foi possível carregar ${what}.`}</p>
}

function DateChip({ date, overdue }: { date: string; overdue?: boolean }) {
  return <span className={`ov-date${overdue ? ' ov-date--overdue' : ''}`} aria-hidden="true"><small>{MONTHS[Number(date.slice(5, 7)) - 1]}</small><strong>{date.slice(8, 10)}</strong></span>
}

export function OverviewAccounts({ accounts, onOpen, ...block }: Block & { accounts: OverviewAccounts | null; onOpen: () => void }) {
  return <section className="ov-card ov-accounts" aria-labelledby="ov-accounts-title">
    <header className="ov-card__head"><h3 id="ov-accounts-title">Contas {accounts && <small>{accounts.items.length} conta(s)</small>}</h3>
      <button type="button" className="ov-link" onClick={onOpen}>Ver contas ›</button></header>
    {!accounts ? <State block={block} what="as contas" />
      : accounts.items.length === 0 ? <p className="ov-empty">Nenhuma conta cadastrada.</p>
        : <ul className="ov-rows">{accounts.items.map(account => <li key={account.accountId} className={account.inScope ? 'is-in-scope' : undefined}>
          <span className="ov-avatar" aria-hidden="true">{account.name.slice(0, 1).toUpperCase()}</span>
          <span className="ov-rows__main"><strong>{account.name}</strong><small>{account.scope} · {accountTypes[account.type] ?? account.type}{account.isPrimary ? ' · principal' : ''}</small></span>
          {account.balanceAvailable
            ? <span className={`ov-mono${isNegative(account.balance) ? ' ov-negative' : ''}`}>{money(account.balance)}</span>
            : <span className="ov-meta" data-unavailable>{unavailableBalanceReasons[account.unavailableReason ?? 'Unavailable'] ?? 'Indisponível'}</span>}
        </li>)}</ul>}
    {accounts?.state === 'Partial' && <p className="ov-note" role="status">Parte dos saldos não pôde ser lida agora.</p>}
  </section>
}

const cardStatus = { Confirmed: 'confirmada pelo banco', Provisional: 'estimada', AwaitingBankBill: 'aguardando o banco' } as const

/** Próximos compromissos: vencidos primeiro e, depois, despesas da Agenda e faturas de cartão por data. Totais do servidor; nada é somado aqui. */
export function OverviewUpcoming({ commitments, onOpenAgenda, onOpenCards, ...block }: Block & { commitments: OverviewCommitments | null; onOpenAgenda: (month?: string) => void; onOpenCards: () => void }) {
  if (!commitments) return <section className="ov-card" aria-labelledby="ov-upcoming-title"><header className="ov-card__head"><h3 id="ov-upcoming-title">Próximos compromissos</h3></header><State block={block} what="os compromissos" /></section>
  const cards = commitments.cardPayments?.state === 'Complete' ? commitments.cardPayments : null
  type Row = { key: string; date: string; title: string; detail: string; amount: string; overdue: boolean; open: () => void }
  const overdue: Row[] = commitments.overdue.items.map(item => ({ key: `o:${item.source}:${item.id}`, date: item.date, title: item.description, detail: `Vencido · ${item.accountName ?? 'sem conta'}`, amount: item.amount, overdue: true, open: () => onOpenAgenda(item.date.slice(0, 7)) }))
  const upcoming: Row[] = [
    ...commitments.upcoming.items.map(item => ({ key: `u:${item.source}:${item.id}`, date: item.date, title: item.description, detail: item.accountName ?? 'Agenda', amount: item.amount, overdue: false, open: () => onOpenAgenda(item.date.slice(0, 7)) })),
    ...(cards?.items ?? []).map(item => ({ key: `c:${item.cardId}:${item.competence}`, date: item.due, title: `Fatura ${item.cardName}`, detail: `Cartão · ${cardStatus[item.status]}`, amount: item.remaining, overdue: false, open: onOpenCards })),
  ].sort((a, b) => a.date.localeCompare(b.date))
  const rows = [...overdue, ...upcoming].slice(0, 5)
  return <section className="ov-card ov-upcoming" aria-labelledby="ov-upcoming-title">
    <header className="ov-card__head"><h3 id="ov-upcoming-title">Próximos compromissos <small>{commitments.horizonDays} dias</small></h3>
      <button type="button" className="ov-link" onClick={() => onOpenAgenda()}>Agenda ›</button></header>
    {commitments.state === 'Unavailable' ? <p className="ov-empty" role="alert">Não foi possível carregar os compromissos da Agenda.</p>
      : rows.length === 0 ? <p className="ov-empty">Nenhum compromisso pendente até {formatDate(commitments.horizonThrough)}.</p>
        : <ul className="ov-rows">{rows.map(row => <li key={row.key}>
          <button type="button" className="ov-rows__button" onClick={row.open} aria-label={`${row.title}, ${formatDate(row.date)}, ${money(row.amount)}${row.overdue ? ', vencido' : ''}`}>
            <DateChip date={row.date} overdue={row.overdue} />
            <span className="ov-rows__main"><strong>{row.title}</strong><small>{row.detail}</small></span>
            <span className="ov-mono">{money(row.amount)}</span></button></li>)}</ul>}
    <footer className="ov-card__foot ov-upcoming__totals">
      <dl>
        <div><dt>Agenda até {formatDate(commitments.horizonThrough)} ({commitments.upcoming.count})</dt><dd className="ov-mono">{money(commitments.upcoming.total)}</dd></div>
        {cards && cards.items.length > 0 && <div><dt>Faturas de cartão ({cards.items.length})</dt><dd className="ov-mono">{money(cards.total)}</dd></div>}
      </dl>
    </footer>
    {commitments.overdue.count > 0 && <p className="ov-note ov-note--danger">{commitments.overdue.count} vencido(s) · {money(commitments.overdue.total)}</p>}
    {cards && cards.overdueCards > 0 && isPositiveAmount(cards.overdueRemaining) && <p className="ov-note ov-note--danger">Faturas vencidas: falta pagar {money(cards.overdueRemaining)}</p>}
  </section>
}

export function OverviewGoals({ goals, onOpen, onOpenGoal }: { goals: OverviewGoals; onOpen: () => void; onOpenGoal: (goalId: string) => void }) {
  const today = todayInSaoPaulo()
  return <section className="ov-card ov-goals" aria-labelledby="ov-goals-title">
    <header className="ov-card__head"><h3 id="ov-goals-title">Metas</h3><button type="button" className="ov-link" onClick={onOpen}>Ver metas ›</button></header>
    <ul className="ov-goals__list">{goals.items.map(goal => <li key={goal.goalId}>
      <div className="ov-budget__row"><button type="button" className="ov-link ov-goals__name" onClick={() => onOpenGoal(goal.goalId)}>{goal.name}</button>
        <span className="ov-goals__pct">{exceedsHundred(goal.progressPercent) || goal.funded ? 'Alvo atingido' : percentText(goal.progressPercent)}</span></div>
      <div className="ov-track" role="progressbar" aria-label={`Progresso de ${goal.name}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(goal.progressPercent)}>
        <span className="ov-track__done" style={{ width: barWidth(goal.progressPercent) }} /></div>
      <div className="ov-budget__row ov-meta"><span className="ov-mono">{money(goal.allocated)} / {money(goal.target)}</span>
        <span className={goal.targetDate && goal.targetDate < today && !goal.funded ? 'ov-negative' : undefined}>{goal.targetDate ? formatDate(goal.targetDate) : 'Sem prazo'}</span></div>
    </li>)}</ul>
  </section>
}

/** Últimos lançamentos efetivos do mês e do escopo (sem pagamentos de fatura: as compras do cartão já são o consumo). */
export function OverviewRecent({ recent, onOpenAll, ...block }: Block & { recent: OverviewRecent | null; onOpenAll: () => void }) {
  return <section className="ov-card ov-recent" aria-labelledby="ov-recent-title">
    <header className="ov-card__head"><h3 id="ov-recent-title">Últimos lançamentos {recent && recent.state === 'Complete' && <small>{recent.totalCount} no mês</small>}</h3>
      <button type="button" className="ov-link" onClick={onOpenAll}>Ver todos ›</button></header>
    {!recent ? <State block={block} what="os lançamentos" />
      : recent.state === 'Unavailable' ? <p className="ov-empty" role="alert">Não foi possível carregar os lançamentos.</p>
        : recent.items.length === 0 ? <div className="ov-empty"><p>Nenhum lançamento efetivo neste período.</p><button type="button" onClick={onOpenAll}>Novo lançamento</button></div>
          : <table className="ov-recent__table">
            <caption className="visually-hidden">Últimos lançamentos de {formatDate(recent.from)} a {formatDate(recent.through)}</caption>
            <thead><tr><th scope="col">Data</th><th scope="col">Descrição</th><th scope="col" className="ov-wide">Categoria</th><th scope="col" className="ov-wide">Conta</th><th scope="col" className="ov-num">Valor</th></tr></thead>
            <tbody>{recent.items.map(item => <tr key={item.id}>
              <td className="ov-mono ov-muted">{formatDate(item.date).slice(0, 5)}</td>
              <td><strong>{item.description}</strong><small className="ov-narrow">{item.categoryName ?? 'Sem categoria'} · {item.accountName}</small>
                {item.awaitingClassification && <span className="ov-tag ov-tag--warning">Aguardando classificação</span>}</td>
              <td className="ov-wide">{item.categoryName ?? 'Sem categoria'}</td>
              <td className="ov-wide">{item.accountName}</td>
              <td className={`ov-num ov-mono${item.type === 'Income' ? ' ov-positive' : ''}`}><span className="visually-hidden">{item.type === 'Income' ? 'Receita ' : 'Despesa '}</span>{item.type === 'Income' ? '+' : '−'} {money(item.amount)}</td>
            </tr>)}</tbody>
          </table>}
  </section>
}
