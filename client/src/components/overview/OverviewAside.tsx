import type { OverviewAccounts, OverviewCardPayments, OverviewCommitment, OverviewCommitments, OverviewGoals, OverviewRecent } from '../../api/overview'
import { formatDate } from '../../money'
import { todayInSaoPaulo } from '../../dates'
import { barWidth, percentNow, dayDistance, exceedsHundred, isNegative, isPositiveAmount, money, monthTitle, percentText, unavailableBalanceReasons } from './overviewText'

const Unavailable = ({ children }: { children: string }) => <p className="ov-empty" role="alert">{children}</p>

/** Contas: nome e saldo de cada uma. Saldo indisponível é dito como tal (nunca zero); a principal e as contas incluídas nos indicadores são identificadas. */
export function OverviewAccountsBlock({ accounts, scopeKind, onOpen }: { accounts: OverviewAccounts; scopeKind: string; onOpen: () => void }) {
  return <section className="ov-card" aria-labelledby="ov-accounts-title">
    <header><div><h3 id="ov-accounts-title">Contas</h3><p className="ov-meta">Saldo em {formatDate(accounts.asOf)}{scopeKind !== 'all' ? ' · a marcada entra nos indicadores' : ''}</p></div><button type="button" onClick={onOpen}>Abrir contas</button></header>
    {accounts.state === 'Unavailable' ? <Unavailable>Não foi possível carregar as contas.</Unavailable>
      : accounts.items.length === 0 ? <div className="ov-empty"><p>Nenhuma conta cadastrada.</p><button type="button" onClick={onOpen}>Cadastrar conta</button></div>
        : <ul className="ov-list">{accounts.items.map(account => <li key={account.accountId} className={account.inScope && scopeKind !== 'all' ? 'ov-list__item--selected' : undefined}>
          <button type="button" className="ov-row" onClick={onOpen} aria-label={`${account.name}: ${account.balanceAvailable ? money(account.balance) : 'saldo indisponível'}. Abrir contas`}>
            <span className="ov-row__main"><strong>{account.name}</strong>
              <small>{account.scope}{account.isPrimary ? ' · Principal' : ''}{account.inScope && scopeKind !== 'all' ? ' · Selecionada' : ''}</small></span>
            {account.balanceAvailable
              ? <span className={`ov-amount${isNegative(account.balance) ? ' ov-amount--negative' : ''}`}>{money(account.balance)}{isNegative(account.balance) && <small> negativo</small>}</span>
              : <span className="ov-amount ov-amount--unavailable">Saldo indisponível<small>{unavailableBalanceReasons[account.unavailableReason ?? ''] ?? unavailableBalanceReasons.Unavailable}</small></span>}
          </button></li>)}</ul>}
  </section>
}

function CommitmentRow({ item, onOpen }: { item: OverviewCommitment; onOpen: (item: OverviewCommitment) => void }) {
  return <li><button type="button" className="ov-row" onClick={() => onOpen(item)} aria-label={`${item.description}, ${formatDate(item.date)}, ${money(item.amount)}. Ver na Agenda`}>
    <span className="ov-row__main"><strong>{item.description}</strong><small>{formatDate(item.date)} · {dayDistance(item.daysFromToday)}{item.accountName ? ` · ${item.accountName}` : ''}</small></span>
    <span className="ov-amount">{money(item.amount)}</span></button></li>
}

/** Próximos compromissos: despesas pendentes da Agenda, vencidas separadas das que vencem no horizonte informado. */
export function OverviewCommitmentsBlock({ commitments, onOpenItem, onOpenAgenda, onOpenCards }: { commitments: OverviewCommitments; onOpenItem: (item: OverviewCommitment) => void; onOpenAgenda: () => void; onOpenCards?: () => void }) {
  const { overdue, upcoming } = commitments
  const cards = commitments.cardPayments ?? null
  const none = overdue.count === 0 && upcoming.count === 0 && !(cards && cards.state === 'Complete' && (cards.items.length > 0 || cards.overdueCards > 0))
  return <section className="ov-card ov-commitments" aria-labelledby="ov-commitments-title">
    <header><div><h3 id="ov-commitments-title">Próximos compromissos</h3>
      <p className="ov-meta">Despesas pendentes da Agenda · vencidas e as dos próximos {commitments.horizonDays} dias (até {formatDate(commitments.horizonThrough)})</p></div>
      <button type="button" onClick={onOpenAgenda}>Abrir Agenda</button></header>
    {commitments.state === 'Unavailable' ? <Unavailable>Não foi possível carregar os compromissos da Agenda.</Unavailable>
      : none ? <p className="ov-empty">Nenhuma despesa pendente na Agenda neste horizonte.</p>
        : <>
          {overdue.count > 0 && <div className="ov-group ov-group--overdue"><h4>Vencidos <span className="ov-tag ov-tag--danger">{overdue.count} · {money(overdue.total)}</span></h4>
            <ul className="ov-list">{overdue.items.map(item => <CommitmentRow key={`${item.source}:${item.id}`} item={item} onOpen={onOpenItem} />)}</ul>
            {overdue.count > overdue.items.length && <p className="ov-meta">+ {overdue.count - overdue.items.length} na Agenda.</p>}</div>}
          {upcoming.count > 0 && <div className="ov-group"><h4>Próximos {commitments.horizonDays} dias <span className="ov-tag">{upcoming.count} · {money(upcoming.total)}</span></h4>
            <ul className="ov-list">{upcoming.items.map(item => <CommitmentRow key={`${item.source}:${item.id}`} item={item} onOpen={onOpenItem} />)}</ul>
            {upcoming.count > upcoming.items.length && <p className="ov-meta">+ {upcoming.count - upcoming.items.length} na Agenda.</p>}</div>}
        </>}
    <CardPaymentsGroup cards={cards} onOpenCards={onOpenCards} />
    <p className="ov-meta ov-meta--note">As compras do cartão não aparecem aqui como saídas: o que sai do caixa é o pagamento da fatura, listado em "Faturas de cartão".</p>
  </section>
}

const cardStatusText = { Confirmed: 'Confirmada pelo banco', Provisional: 'Estimada (provisória)', AwaitingBankBill: 'Aguardando a fatura do banco' } as const
const cardPaymentText: Record<string, string> = { Unpaid: 'não paga', Partial: 'paga em parte', Paid: 'paga', Overpaid: 'paga a mais' }

/**
 * Faturas de cartão como obrigação de caixa pelo vencimento: o que ainda falta pagar, e se o valor é confirmado pelo banco ou estimado. Depois de um pagamento
 * só o restante aparece (o pagamento não é somado de novo). O consumo das compras continua no Orçamento.
 */
function CardPaymentsGroup({ cards, onOpenCards }: { cards: OverviewCardPayments | null; onOpenCards?: () => void }) {
  if (!cards) return null
  if (cards.state === 'Unavailable') return <p className="ov-empty" role="alert">Não foi possível carregar as faturas de cartão.</p>
  const hasOverdue = cards.overdueCards > 0 && isPositiveAmount(cards.overdueRemaining)
  if (cards.items.length === 0 && !hasOverdue) return null
  return <div className="ov-group ov-group--cards" aria-label="Faturas de cartão">
    <h4>Faturas de cartão <span className="ov-tag">{cards.items.length} · falta pagar {money(cards.total)}</span></h4>
    {hasOverdue && <p className="ov-notice ov-notice--warning" role="status">Há {money(cards.overdueRemaining)} a pagar em fatura(s) já vencida(s) de {cards.overdueCards} cartão(ões).</p>}
    <ul className="ov-list">{cards.items.map(item => <li key={`${item.cardId}:${item.competence}`}>
      <button type="button" className="ov-row" onClick={onOpenCards} disabled={!onOpenCards} aria-label={`Fatura ${item.cardName}, vence em ${formatDate(item.due)}, falta pagar ${money(item.remaining)}, ${cardStatusText[item.status]}. Ver em Cartões`}>
        <span className="ov-row__main"><strong>{item.cardName}</strong><small>Vence em {formatDate(item.due)} · {dayDistance(item.daysFromToday)} · {cardStatusText[item.status]}{isPositiveAmount(item.paid) ? ` · ${cardPaymentText[item.paymentState] ?? item.paymentState}: já pago ${money(item.paid)} de ${money(item.obligation)}` : ''}</small></span>
        <span className="ov-amount">{money(item.remaining)}</span></button></li>)}</ul>
  </div>
}

/** Metas existentes: nome, valor acumulado, alvo e progresso. Sem novos cálculos. */
export function OverviewGoalsBlock({ goals, onOpen, onOpenGoal }: { goals: OverviewGoals; onOpen: () => void; onOpenGoal?: (goalId: string) => void }) {
  const today = todayInSaoPaulo()
  return <section className="ov-card" aria-labelledby="ov-goals-title">
    <header><div><h3 id="ov-goals-title">Metas</h3>{goals.state === 'Complete' && goals.activeCount > 0 && <p className="ov-meta">{goals.activeCount} ativa(s){goals.completedCount > 0 ? ` · ${goals.completedCount} concluída(s)` : ''}</p>}</div><button type="button" onClick={onOpen}>Ver metas</button></header>
    {goals.state === 'Unavailable' ? <Unavailable>Não foi possível carregar as metas.</Unavailable>
      : goals.items.length === 0 ? <div className="ov-empty"><p>Nenhuma meta ativa.</p><button type="button" onClick={onOpen}>Criar meta</button></div>
        : <ul className="ov-goals">{goals.items.map(goal => <li key={goal.goalId}>
          <div className="ov-limit__head">{onOpenGoal ? <button type="button" className="button--link" aria-label={`Abrir o objetivo ${goal.name}`} onClick={() => onOpenGoal(goal.goalId)}><strong>{goal.name}</strong></button> : <strong>{goal.name}</strong>}
            <span className={`ov-tag${goal.funded ? ' ov-tag--ok' : ''}`}>{exceedsHundred(goal.progressPercent) ? `Alocado acima do alvo (${percentText(goal.progressPercent)})` : goal.funded ? 'Alvo atingido' : percentText(goal.progressPercent)}</span></div>
          <div className="ov-bar-track ov-bar-track--goal" role="progressbar" aria-label={`Progresso de ${goal.name}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(goal.progressPercent)}><span style={{ width: barWidth(goal.progressPercent) }} /></div>
          <p className="ov-limit__values"><span>{money(goal.allocated)} de {money(goal.target)}{goal.funded ? '' : ` · faltam ${money(goal.remaining)}`}</span>
            {goal.targetDate ? (goal.targetDate < today && !goal.funded ? <span className="ov-tag ov-tag--warning">Prazo vencido em {formatDate(goal.targetDate)}</span> : <span>até {formatDate(goal.targetDate)}</span>) : <span>Sem prazo definido</span>}</p></li>)}</ul>}
    <p className="ov-meta ov-meta--note">Valores alocados a objetivos são uma reserva dentro do saldo das contas: não aumentam o saldo nem são um limite de orçamento.</p>
  </section>
}

/** Últimos lançamentos efetivos do mês e do escopo escolhidos. Pagamento de fatura fica de fora: as compras do cartão já são o consumo. */
export function OverviewRecentBlock({ recent, competence, scopeText, onOpenAll }: { recent: OverviewRecent; competence: string; scopeText: string; onOpenAll: () => void }) {
  return <section className="ov-card" aria-labelledby="ov-recent-title">
    <header><div><h3 id="ov-recent-title">Últimos lançamentos</h3>
      <p className="ov-meta">{monthTitle(competence)} (até {formatDate(recent.through)}) · {scopeText} · sem pagamentos de fatura</p></div><button type="button" onClick={onOpenAll}>Ver todos</button></header>
    {recent.state === 'Unavailable' ? <Unavailable>Não foi possível carregar os lançamentos.</Unavailable>
      : recent.items.length === 0 ? <div className="ov-empty"><p>Nenhum lançamento efetivo neste período.</p><button type="button" onClick={onOpenAll}>Novo lançamento</button></div>
        : <ul className="ov-list">{recent.items.map(item => <li key={item.id}><div className="ov-row ov-row--static">
          <span className="ov-row__main"><strong>{item.description}</strong>
            <small>{formatDate(item.date)} · {item.categoryName ?? 'Sem categoria'}{item.awaitingClassification && <> · <span className="ov-tag ov-tag--warning">Aguardando classificação</span></>} · {item.accountName}</small></span>
          <span className={`ov-amount ov-amount--${item.type === 'Income' ? 'income' : 'expense'}`}><span className="visually-hidden">{item.type === 'Income' ? 'Receita ' : 'Despesa '}</span>{item.type === 'Income' ? '+' : '−'} {money(item.amount)}</span></div></li>)}</ul>}
    {recent.state === 'Complete' && recent.totalCount > recent.items.length && <p className="ov-meta">Mostrando {recent.items.length} de {recent.totalCount} lançamentos do período.</p>}
  </section>
}
