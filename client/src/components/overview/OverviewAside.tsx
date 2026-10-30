import type { OverviewAccounts, OverviewCommitment, OverviewCommitments, OverviewGoals, OverviewRecent } from '../../api/overview'
import { formatDate } from '../../money'
import { barWidth, percentNow, dayDistance, isNegative, money, monthTitle, percentText, unavailableBalanceReasons } from './overviewText'

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
export function OverviewCommitmentsBlock({ commitments, onOpenItem, onOpenAgenda }: { commitments: OverviewCommitments; onOpenItem: (item: OverviewCommitment) => void; onOpenAgenda: () => void }) {
  const { overdue, upcoming } = commitments
  const none = overdue.count === 0 && upcoming.count === 0
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
    <p className="ov-meta ov-meta--note">Faturas de cartão não entram nesta lista: acompanhe-as em Cartões.</p>
  </section>
}

/** Metas existentes: nome, valor acumulado, alvo e progresso. Sem novos cálculos. */
export function OverviewGoalsBlock({ goals, onOpen }: { goals: OverviewGoals; onOpen: () => void }) {
  return <section className="ov-card" aria-labelledby="ov-goals-title">
    <header><div><h3 id="ov-goals-title">Metas</h3>{goals.state === 'Complete' && goals.activeCount > 0 && <p className="ov-meta">{goals.activeCount} ativa(s){goals.completedCount > 0 ? ` · ${goals.completedCount} concluída(s)` : ''}</p>}</div><button type="button" onClick={onOpen}>Ver metas</button></header>
    {goals.state === 'Unavailable' ? <Unavailable>Não foi possível carregar as metas.</Unavailable>
      : goals.items.length === 0 ? <div className="ov-empty"><p>Nenhuma meta ativa.</p><button type="button" onClick={onOpen}>Criar meta</button></div>
        : <ul className="ov-goals">{goals.items.map(goal => <li key={goal.goalId}>
          <div className="ov-limit__head"><strong>{goal.name}</strong><span className={`ov-tag${goal.funded ? ' ov-tag--ok' : ''}`}>{goal.funded ? 'Alvo atingido' : percentText(goal.progressPercent)}</span></div>
          <div className="ov-bar-track ov-bar-track--goal" role="progressbar" aria-label={`Progresso de ${goal.name}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(goal.progressPercent)}><span style={{ width: barWidth(goal.progressPercent) }} /></div>
          <p className="ov-limit__values"><span>{money(goal.allocated)} de {money(goal.target)}</span>{goal.targetDate && <span>até {formatDate(goal.targetDate)}</span>}</p></li>)}</ul>}
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
