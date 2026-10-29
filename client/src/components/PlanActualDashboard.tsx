import { useCallback, useEffect, useState } from 'react'
import { getPlanActual, saveCardTarget, type OverdraftAccount, type PlanActualDashboard as PlanActualData, type PlanActualScopeChoice, type PlanCards, type PlanIndicator, type PlanOverdraft, type PlanSituation, type PlanSync } from '../api/planActual'
import { syncConnection } from '../api/pluggyPoc'
import { syncStatusLabels, type SyncStatus } from '../api/pluggySyncStatus'
import { toUserMessage } from '../api/apiErrors'
import { formatBrlDecimal, formatDate } from '../money'
import { formatMonthYear } from '../dates'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import '../PlanActual.css'

type Section = 'accounts' | 'cards' | 'calendar' | 'budgets'
const money = (value: string | null) => value === null ? 'Indisponível' : formatBrlDecimal(value)
const pct = (value: string | null) => value === null ? null : `${value.replace('.', ',')}%`
const dateTime = (iso: string | null) => iso ? `${formatDate(iso.slice(0, 10))} ${new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: 'America/Sao_Paulo' })}` : '—'

const reasonText: Record<string, string> = {
  MovementsInReview: 'Há movimentações em revisão: elas ainda não entram nos totais.',
  PartialPeriod: 'O histórico importado não cobre o mês inteiro.',
  CardCoverageIncomplete: 'Parte das obrigações dos cartões ainda é estimativa ou aguarda a fatura do banco.',
  DebtCoverageIncomplete: 'Há parcelas de dívida sem conta responsável.',
  PlanNotAttributable: 'O orçamento por categoria vale para todas as contas: escolha “Todas as contas” para compará-lo.',
  PartialPlan: 'Nem todas as contas pagadoras têm meta: o total da meta é parcial.',
  MissingPayerAccount: 'Há cartões sem conta pagadora.',
  InsufficientHistory: 'Histórico insuficiente: não há observação do saldo no início do mês para comparar.',
  NoBankObservation: 'Sem observação de saldo do banco.',
  OverdraftNotDerivableFromBalance: 'O saldo observado não reproduz a utilização do limite: o histórico não é comparável.',
  ProjectionUnavailable: 'Sem saldo projetado confiável para projetar a recuperação.',
  ProjectionAdjustedByDifference: 'A projeção foi corrigida pela diferença atual entre o saldo do banco e o calculado.',
  FuturePeriod: 'Mês futuro: só há projeção, não há recuperação efetiva.',
  'Cards:WeakSchedule': 'Cartões: o ciclo da fatura ainda não foi confirmado pelo histórico do banco.',
  'Cards:AwaitingBankBill': 'Cartões: aguardando a fatura do banco.',
  'Cards:UnprojectedInstallments': 'Cartões: há parcelas sem mês definido.',
  'Cards:AmbiguousInstallments': 'Cartões: há parcelamentos ambíguos.',
  'Cards:SyncAttention': 'Cartões: a conexão pede atenção.',
  'Cards:NoBankHistory': 'Cartões: sem faturas do banco.',
  'Cards:KnownWithoutDueDate': 'Cartões: obrigação conhecida sem vencimento.',
}
const componentLabels: Record<string, string> = {
  BankExpenses: 'Despesas bancárias', CardPurchases: 'Compras de cartão (mês da fatura)', CardInvoicePayments: 'Pagamentos de fatura', PendingPlanned: 'Previsões e dívidas pendentes',
  PendingCardInvoices: 'Faturas de cartão a pagar', PendingBank: 'Pendentes no banco (ainda não efetivadas)', PendingCardPurchases: 'Compras de cartão pendentes', EstimatedCardInstallments: 'Parcelas estimadas com evidência',
}
const periodLabels = { Past: 'Mês encerrado', Current: 'Mês corrente', Future: 'Mês futuro' } as const
const warnings = (reasons: string[]) => reasons.filter(reason => reasonText[reason] && reason !== 'PlanNotAttributable')

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: PlanActualData }

/**
 * Planejado × Projetado × Realizado (T10.1). O escopo (conta principal, todas as contas ou uma conta) é trocado sem recarregar a página e o mês
 * escolhido nos filtros do Dashboard é preservado. Tudo que aparece vem calculado pela API; o navegador só apresenta.
 */
export function PlanActualDashboard({ workspaceId, competence, refreshKey = 0, onNavigate }: { workspaceId: string; competence: string; refreshKey?: number; onNavigate?: (section: Section) => void }) {
  const [scope, setScope] = useState<PlanActualScopeChoice>({ kind: 'primary' })
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${competence}|${scope.kind}|${scope.accountId ?? ''}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  // A API é a fonte da verdade do escopo: guardamos a última lista de contas para o seletor não piscar durante o carregamento.
  const [lastScope, setLastScope] = useState<PlanActualData['scope'] | null>(null)
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getPlanActual(workspaceId, competence, scope, controller.signal)
      .then(data => { if (!controller.signal.aborted) { setLoaded({ key, state: 'loaded', data }); setLastScope(data.scope) } })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, competence, scope, key])

  const refresh = useCallback(() => setReload(value => value + 1), [])
  // A API é a fonte da verdade do escopo: guardamos a última lista de contas para o seletor não piscar durante o carregamento.
  const choices = visible.state === 'loaded' ? visible.data.scope : lastScope

  return <section className="plan-actual" aria-labelledby="plan-actual-title">
    <div className="plan-actual__header">
      <div>
        <h4 id="plan-actual-title">Planejado × Projetado × Realizado</h4>
        <p className="meta">{formatMonthYear(`${competence}-01`)}{visible.state === 'loaded' ? ` · ${periodLabels[visible.data.periodKind]}` : ''}</p>
      </div>
      <ScopeSelector scope={scope} choices={choices} onChange={setScope} />
    </div>
    {visible.state === 'loading' && <OperationalLoading>Carregando o planejado × realizado…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={refresh} />}
    {visible.state === 'loaded' && <PlanActualBody workspaceId={workspaceId} data={visible.data} competence={competence} onNavigate={onNavigate} onChanged={refresh} />}
  </section>
}

function ScopeSelector({ scope, choices, onChange }: { scope: PlanActualScopeChoice; choices: PlanActualData['scope'] | null; onChange: (scope: PlanActualScopeChoice) => void }) {
  const value = scope.kind === 'account' ? `account:${scope.accountId}` : scope.kind
  const primaryName = choices?.primaryAccountName
  return <label className="plan-actual__scope">Conta
    <select aria-label="Escopo do painel" value={value} onChange={event => {
      const next = event.target.value
      onChange(next.startsWith('account:') ? { kind: 'account', accountId: next.slice('account:'.length) } : { kind: next as 'primary' | 'all' })
    }}>
      <option value="primary">{primaryName ? `Conta principal · ${primaryName}` : 'Conta principal'}</option>
      <option value="all">Todas as contas</option>
      {(choices?.accounts ?? []).map(account => <option key={account.accountId} value={`account:${account.accountId}`}>{account.name} · {account.scope}{account.isActive ? '' : ' (inativa)'}</option>)}
    </select>
  </label>
}

function PlanActualBody({ workspaceId, data, competence, onNavigate, onChanged }: { workspaceId: string; data: PlanActualData; competence: string; onNavigate?: (section: Section) => void; onChanged: () => void }) {
  const [perspective, setPerspective] = useState<'Consumption' | 'CashFlow'>(data.expenses.primaryPerspective)
  const expenses = perspective === 'Consumption' ? data.expenses.consumption : data.expenses.cashFlow
  return <>
    {data.scope.primaryMissing && <p className="notice" role="status">Nenhuma conta principal definida: exibindo todas as contas. Defina a conta principal em Contas.</p>}
    {data.scope.kind === 'all' && <p className="meta">Consolidado de todas as contas, sem contar transferências internas como receita ou despesa. PF e PJ continuam identificáveis na lista de contas.</p>}
    {data.scope.kind === 'primary' && data.scope.accountName && <p className="meta">Conta principal: <strong>{data.scope.accountName}</strong>{data.scope.accountScope ? ` · ${data.scope.accountScope}` : ''}</p>}
    {!data.snapshotConsistent && <p className="notice notice--warning" role="status">Os dados mudaram durante a leitura. Atualize para ver um retrato consistente.</p>}
    <SyncStrip workspaceId={workspaceId} sync={data.sync} onChanged={onChanged} />
    <CoverageNotice data={data} onNavigate={onNavigate} />
    <div className="plan-actual__grid">
      <IndicatorCard title="Receitas" ariaLabel="Receitas: planejado, projetado e recebido" indicator={data.income} realizedLabel="Recebido" />
      <article className="card plan-card" aria-label="Despesas: planejado, projetado e efetivo">
        <header><h5>Despesas</h5>
          <div className="plan-card__toggle" role="radiogroup" aria-label="Perspectiva das despesas">
            <button type="button" role="radio" aria-checked={perspective === 'Consumption'} onClick={() => setPerspective('Consumption')}>Consumo</button>
            <button type="button" role="radio" aria-checked={perspective === 'CashFlow'} onClick={() => setPerspective('CashFlow')}>Fluxo de caixa</button>
          </div>
        </header>
        <p className="meta">{perspective === 'Consumption' ? 'Despesas incorridas: bancárias, mais compras de cartão pelo mês da fatura. Pagamento de fatura não entra aqui.' : 'Saídas das contas, incluindo pagamentos de fatura. Compras de cartão só aparecem quando a fatura é paga.'}</p>
        <IndicatorBody indicator={expenses} realizedLabel="Efetivo" />
      </article>
      {data.overdraft.eligible && <OverdraftCard overdraft={data.overdraft} carryover={data.situation.carryoverExpense} />}
      <CardsCard workspaceId={workspaceId} cards={data.cards} scope={data.scope} competence={competence} onChanged={onChanged} />
    </div>
    <SituationBlock situation={data.situation} />
  </>
}

function IndicatorCard({ title, ariaLabel, indicator, realizedLabel }: { title: string; ariaLabel: string; indicator: PlanIndicator; realizedLabel: string }) {
  return <article className="card plan-card" aria-label={ariaLabel}><header><h5>{title}</h5></header><IndicatorBody indicator={indicator} realizedLabel={realizedLabel} /></article>
}

function IndicatorBody({ indicator, realizedLabel }: { indicator: PlanIndicator; realizedLabel: string }) {
  const planned = indicator.plannedState === 'Defined'
  const percent = indicator.executionPercent === null ? null : Math.min(100, Math.max(0, Number.parseFloat(indicator.executionPercent)))
  const notes = warnings(indicator.reasons)
  return <>
    <p className="plan-card__main" aria-label={realizedLabel}><small>{realizedLabel}</small><strong>{money(indicator.realized)}</strong></p>
    <dl className="plan-card__facts">
      <div><dt>Planejado</dt><dd>{planned ? money(indicator.planned) : 'Não definido'}</dd></div>
      <div><dt>Projetado</dt><dd>{money(indicator.projected)}</dd></div>
      {planned && <div><dt>Diferença (real. − plan.)</dt><dd>{money(indicator.realizedDifference)}</dd></div>}
      {planned && <div><dt>Diferença (proj. − plan.)</dt><dd>{money(indicator.projectedDifference)}</dd></div>}
      {indicator.executionPercent !== null && <div><dt>Executado</dt><dd>{pct(indicator.executionPercent)}</dd></div>}
    </dl>
    {percent !== null && <div className="plan-card__bar" role="progressbar" aria-label="Percentual executado" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(percent)}><span style={{ width: `${percent}%` }} /></div>}
    {!planned && <p className="meta">Sem planejamento cadastrado{indicator.reasons.includes('PlanNotAttributable') ? '. ' + reasonText.PlanNotAttributable : '.'}</p>}
    {indicator.components.length > 0 && <details><summary>Como foi calculado</summary>
      <ul className="plan-card__components">{indicator.components.filter(component => component.amount !== '0.00').map(component => <li key={component.code}><span>{componentLabels[component.code] ?? component.code}</span><strong>{money(component.amount)}</strong></li>)}</ul>
      <p className="meta">Projetado = realizado + compromissos pendentes ({money(indicator.pending)}).</p>
    </details>}
    {notes.length > 0 && <ul className="plan-card__notes" aria-label="Ressalvas">{notes.map(reason => <li key={reason}>{reasonText[reason]}</li>)}</ul>}
  </>
}

function OverdraftCard({ overdraft, carryover }: { overdraft: PlanOverdraft; carryover: string }) {
  const effective = overdraft.effectiveRecovery
  const current = overdraft.periodKind === 'Current'
  const insufficient = effective === null
  const lines = overdraft.accounts
  const notes = [...new Set([...overdraft.reasons, ...lines.flatMap(account => account.reasons)])].filter(reason => reasonText[reason])
  const direction = effective === null ? null : Number.parseFloat(effective) > 0 ? 'Recuperação' : Number.parseFloat(effective) < 0 ? 'Aumento do endividamento' : 'Utilização estável'
  return <article className="card plan-card" aria-label="Recuperação do cheque especial">
    <header><h5>Recuperação do limite</h5></header>
    <p className="plan-card__main"><small>{current ? 'Recuperação efetiva até a última sincronização' : 'Recuperação efetiva no mês'}</small>
      <strong>{insufficient ? 'Histórico insuficiente' : money(effective)}</strong></p>
    {direction && <p className="meta">{direction}{overdraft.accounts.some(a => a.startApproximate) ? ' · início aproximado' : ''}</p>}
    <dl className="plan-card__facts">
      <div><dt>Utilizado hoje</dt><dd>{money(overdraft.usedLatest)}</dd></div>
      {overdraft.usedAtStart !== null && <div><dt>Utilizado no início</dt><dd>{money(overdraft.usedAtStart)}</dd></div>}
      {overdraft.periodKind !== 'Past' && <div><dt>{current ? 'Recuperação projetada até o fim do mês' : 'Recuperação projetada'}</dt><dd>{overdraft.projectedRecovery !== null ? money(overdraft.projectedRecovery) : overdraft.projectedRecoveryFromNow !== null ? `${money(overdraft.projectedRecoveryFromNow)} (a partir de hoje)` : 'Indisponível'}</dd></div>}
      {overdraft.projectedUsedAtEnd !== null && <div><dt>Utilização projetada no fim</dt><dd>{money(overdraft.projectedUsedAtEnd)}</dd></div>}
    </dl>
    {lines.some(account => account.fullyRecovered) && <p className="notice" role="status">Limite totalmente recuperado: nenhuma recuperação adicional é projetada.</p>}
    {overdraft.periodKind !== 'Past' && carryover !== '0.00' && (overdraft.projectedRecovery !== null || overdraft.projectedRecoveryFromNow !== null) && <p className="meta">A projeção inclui {money(carryover)} de compromissos de meses anteriores ainda em aberto (veja a situação financeira do mês).</p>}
    {lines.some(account => account.exceedsLimit) && <p className="notice notice--warning" role="status">A utilização projetada ultrapassa o limite contratado.</p>}
    {current && effective !== null && <p className="meta">A recuperação que ainda depende de receitas futuras não é tratada como meta descumprida.</p>}
    <p className="meta">O limite do cheque especial é crédito do banco: não compõe saldo nem patrimônio.</p>
    {lines.length > 1 && <details><summary>Por conta ({lines.length})</summary><ul className="plan-card__components">{lines.map(account => <OverdraftLine key={account.accountId} account={account} />)}</ul></details>}
    {notes.length > 0 && <ul className="plan-card__notes" aria-label="Ressalvas">{notes.map(reason => <li key={reason}>{reasonText[reason]}</li>)}</ul>}
  </article>
}

function OverdraftLine({ account }: { account: OverdraftAccount }) {
  return <li><span>{account.name}</span><strong>{account.effectiveRecovery === null ? 'Histórico insuficiente' : money(account.effectiveRecovery)}</strong></li>
}

function CardsCard({ workspaceId, cards, scope, competence, onChanged }: { workspaceId: string; cards: PlanCards; scope: PlanActualData['scope']; competence: string; onChanged: () => void }) {
  const [editing, setEditing] = useState(false)
  const [amount, setAmount] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const payer = scope.kind === 'all' ? null : scope.accountId
  const defined = cards.plannedState === 'Defined'
  const percent = cards.confirmedPercent === null ? null : Math.min(100, Math.max(0, Number.parseFloat(cards.confirmedPercent)))
  const notes = warnings(cards.reasons)
  const submit = async (value: string | null) => {
    if (!payer) return
    setBusy(true); setError(null)
    try { await saveCardTarget(workspaceId, payer, competence, value); setEditing(false); setAmount(''); onChanged() } catch (failure: unknown) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }
  if (!cards.applicable) return <article className="card plan-card" aria-label="Meta dos cartões"><header><h5>Cartões</h5></header><p className="empty-state">Nenhum cartão é pago por esta conta.</p></article>
  return <article className="card plan-card" aria-label="Meta dos cartões × consumo">
    <header><h5>Cartões</h5></header>
    <p className="plan-card__main"><small>Consumo confirmado</small><strong>{money(cards.confirmed)}</strong></p>
    <dl className="plan-card__facts">
      <div><dt>Meta mensal</dt><dd>{defined ? money(cards.target) : 'Não definido'}</dd></div>
      <div><dt>Compras pendentes conhecidas</dt><dd>{money(cards.pendingKnown)}</dd></div>
      {cards.estimated !== '0.00' && <div><dt>Parcelas estimadas</dt><dd>{money(cards.estimated)}</dd></div>}
      <div><dt>Projeção atualizada</dt><dd>{money(cards.projected)}</dd></div>
      {defined && <div><dt>Restante da meta</dt><dd>{money(cards.remaining)}</dd></div>}
      {defined && cards.excessProjected !== null && cards.excessProjected !== '0.00' && <div className="plan-card__excess"><dt>Excesso projetado</dt><dd>{money(cards.excessProjected)}</dd></div>}
      {cards.confirmedPercent !== null && <div><dt>Consumido (confirmado)</dt><dd>{pct(cards.confirmedPercent)}</dd></div>}
      {cards.projectedPercent !== null && <div><dt>Consumido (projetado)</dt><dd>{pct(cards.projectedPercent)}</dd></div>}
    </dl>
    {percent !== null && <div className="plan-card__bar" role="progressbar" aria-label="Percentual da meta consumido" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(percent)}><span style={{ width: `${percent}%` }} /></div>}
    {cards.financeCharges !== '0.00' && <p className="meta">Inclui {money(cards.financeCharges)} de encargos informados pelo banco.</p>}
    {!defined && <p className="meta">Sem meta definida para os cartões deste escopo.</p>}
    {cards.targetPartial && <p className="meta">{reasonText.PartialPlan}</p>}
    {payer ? (editing
      ? <form className="plan-card__form" onSubmit={event => { event.preventDefault(); void submit(amount.trim() === '' ? null : amount.trim().replace(',', '.')) }}>
        <label>Meta mensal a partir de {formatMonthYear(`${competence}-01`)}<input inputMode="decimal" aria-label="Meta mensal dos cartões" value={amount} onChange={event => setAmount(event.target.value)} placeholder="5000.00" /></label>
        <div className="plan-card__form-actions"><button type="submit" className="button--primary" disabled={busy}>{busy ? 'Salvando…' : 'Salvar meta'}</button><button type="button" disabled={busy} onClick={() => { setEditing(false); setError(null) }}>Cancelar</button>
          {defined && <button type="button" disabled={busy} onClick={() => void submit(null)}>Remover meta</button>}</div>
        {error && <p className="field-error" role="alert">{error}</p>}
      </form>
      : <button type="button" onClick={() => { setEditing(true); setAmount(defined && cards.target ? cards.target : '') }}>{defined ? 'Alterar meta' : 'Definir meta'}</button>)
      : <p className="meta">Selecione uma conta para definir a meta dos cartões que ela paga.</p>}
    {cards.cards.length > 0 && <details><summary>Por cartão ({cards.cards.length})</summary><ul className="plan-card__components">{cards.cards.map(card => <li key={card.cardId}><span>{card.name}</span><strong>{money(card.projected)}</strong></li>)}</ul></details>}
    {notes.length > 0 && <ul className="plan-card__notes" aria-label="Ressalvas">{notes.map(reason => <li key={reason}>{reasonText[reason]}</li>)}</ul>}
  </article>
}

function SituationBlock({ situation }: { situation: PlanSituation }) {
  const limited = situation.state !== 'Complete'
  return <section className="card plan-situation" aria-labelledby="plan-situation-title">
    <h5 id="plan-situation-title">Situação financeira do mês</h5>
    <dl className="plan-card__facts plan-situation__facts">
      <div><dt>Saldo realizado atual</dt><dd>{situation.realizedBalance !== null ? money(situation.realizedBalance) : situation.partialRealizedBalance !== null ? `Indisponível (parcial: ${money(situation.partialRealizedBalance)} em ${situation.consideredAccounts} conta(s))` : 'Indisponível'}</dd></div>
      <div><dt>Saldo projetado no fechamento</dt><dd>{money(situation.projectedClosing)}</dd></div>
      <div><dt>Menor saldo projetado</dt><dd>{situation.minimumProjected === null ? 'Indisponível' : `${money(situation.minimumProjected)}${situation.minimumProjectedDate ? ` em ${formatDate(situation.minimumProjectedDate)}` : ''}`}</dd></div>
      <div><dt>Obrigações pendentes</dt><dd>{money(situation.pendingExpense)}</dd></div>
      <div><dt>Receitas pendentes</dt><dd>{money(situation.pendingIncome)}</dd></div>
      {(situation.carryoverExpense !== '0.00' || situation.carryoverIncome !== '0.00') && <div><dt>Em aberto de meses anteriores</dt><dd>{money(situation.carryoverExpense)} a pagar{situation.carryoverIncome !== '0.00' ? ` · ${money(situation.carryoverIncome)} a receber` : ''}</dd></div>}
      <div><dt>Risco de saldo negativo</dt><dd>{situation.negativeRiskDate ? `a partir de ${formatDate(situation.negativeRiskDate)}` : 'Sem risco na projeção'}</dd></div>
      <div><dt>Atualização bancária</dt><dd>{dateTime(situation.latestBankUpdate)}</dd></div>
    </dl>
    {situation.reasons.filter(reason => reason.startsWith('PartialMonthFrom:')).map(reason => <p key={reason} className="meta" role="status">O saldo só é calculável a partir de {formatDate(reason.slice('PartialMonthFrom:'.length))}: o fechamento e o menor saldo consideram esse trecho do mês, mas não a abertura. Decida as movimentações em revisão para completar o histórico.</p>)}
    {situation.reasons.includes('CarryoverIncluded') && <p className="meta" role="status">O saldo projetado já considera os compromissos de meses anteriores ainda em aberto (vencidos e não pagos). Confira se algum já foi pago e não está registrado.</p>}
    {limited && <p className="meta" role="status">{situation.unavailableAccounts > 0 ? `${situation.unavailableAccounts} conta(s) sem saldo calculável: os valores acima são os disponíveis, não o total. ` : ''}{situation.reasons.some(reason => reason.startsWith('ProjectionUnavailable')) ? 'A projeção do saldo não está disponível para este período (configure o saldo inicial ou decida as movimentações em revisão).' : ''}</p>}
  </section>
}

function CoverageNotice({ data, onNavigate }: { data: PlanActualData; onNavigate?: (section: Section) => void }) {
  const { coverage } = data
  const lines = warnings(coverage.reasons)
  if (coverage.reviewCount === 0 && coverage.cardReviewCount === 0 && lines.length === 0) return null
  return <div className={`notice${coverage.reviewAffectsConfidence ? ' notice--warning' : ''}`} role="status" aria-label="Cobertura e revisão">
    {(coverage.reviewCount > 0 || coverage.cardReviewCount > 0) && <p>{coverage.reviewCount + coverage.cardReviewCount} movimentação(ões) em revisão{coverage.reviewInMonth > 0 ? `, ${coverage.reviewInMonth} neste mês` : ''}.{' '}
      {coverage.reviewAffectsConfidence ? 'Elas reduzem a confiança destes indicadores; o que já está confirmado continua visível.' : 'Não afetam o mês selecionado, mas limitam o histórico calculável.'}{' '}
      <button type="button" onClick={() => onNavigate?.('accounts')}>Revisar movimentações</button></p>}
    {lines.filter(reason => reason !== 'MovementsInReview').length > 0 && <ul>{lines.filter(reason => reason !== 'MovementsInReview').map(reason => <li key={reason}>{reasonText[reason]}</li>)}</ul>}
  </div>
}

function SyncStrip({ workspaceId, sync, onChanged }: { workspaceId: string; sync: PlanSync | null; onChanged: () => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  if (!sync) return null
  const run = async (connectionId: string) => {
    setBusy(connectionId); setError(null)
    try { await syncConnection(workspaceId, connectionId) } catch (failure: unknown) { setError(toUserMessage(failure)) } finally { setBusy(null); onChanged() }
  }
  const many = sync.connections.length > 1
  return <div className="plan-sync" aria-label="Sincronização bancária">
    <p className="meta">
      {sync.oldestSuccessAt === null ? 'Ainda sem sincronização bem-sucedida.'
        : sync.sameMoment || !many ? `Última sincronização bem-sucedida: ${dateTime(sync.latestSuccessAt)}.`
          : `Sincronização mais antiga: ${dateTime(sync.oldestSuccessAt)} · mais recente: ${dateTime(sync.latestSuccessAt)}.`}{' '}
      <span className={`badge badge--sync-${sync.state.toLowerCase()}`}>{syncStatusLabels[sync.state as SyncStatus] ?? sync.state}</span>{' '}
      {sync.autoEnabled ? (sync.nextAttemptAt ? `Próxima tentativa prevista: ${dateTime(sync.nextAttemptAt)}.` : '') : 'Automática desligada.'}
    </p>
    {sync.stale && <p className="notice notice--warning" role="status">Atualização desatualizada: os dados do banco podem estar atrasados.</p>}
    {many && !sync.sameMoment && <ul className="plan-sync__list">{sync.connections.map(connection => <li key={connection.connectionId}>{connection.label ?? 'Conexão'}: {dateTime(connection.lastSuccessAt)}</li>)}</ul>}
    <div className="plan-sync__actions">{sync.connections.map(connection => <button key={connection.connectionId} type="button" disabled={busy !== null || connection.status === 'Running'} onClick={() => void run(connection.connectionId)}>
      {busy === connection.connectionId ? 'Sincronizando…' : many ? `Sincronizar agora · ${connection.label ?? 'conexão'}` : 'Sincronizar agora'}</button>)}</div>
    {error && <p className="field-error" role="alert">{error}</p>}
  </div>
}
