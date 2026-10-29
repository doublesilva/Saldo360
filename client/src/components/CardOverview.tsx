import { useEffect, useState } from 'react'
import {
  basisLabels, coverageText, cycleLabels, getInvoiceOverview, paymentStateLabels, syncLabels,
  type CardInvoiceView, type CardOverviewItem, type InvoiceOverview,
} from '../api/cardProjection'
import { formatBrlDecimal, formatDate } from '../money'
import { billReconciliationLabels } from '../pluggyFormat'
import { todayInSaoPaulo } from '../dates'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import '../CardProjection.css'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: InvoiceOverview }

/** Visão mensal dos cartões: cada fatura mostra fechamento, pagamento e conciliação como dimensões separadas. Só exibe o que o backend calculou. */
export function CardOverview({ workspaceId, refreshKey = 0, onOpenCard }: { workspaceId: string; refreshKey?: number; onOpenCard?: (cardId: string) => void }) {
  const [competence, setCompetence] = useState(() => todayInSaoPaulo().slice(0, 7))
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${competence}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getInvoiceOverview(workspaceId, competence, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, competence, key])

  return <section className="card-overview" aria-labelledby="card-overview-title">
    <div className="section-heading">
      <div><h3 id="card-overview-title">Visão dos cartões</h3><p className="meta">Faturas do mês por cartão. Fatura fechada vale o total do banco; aberta e futura são projeções.</p></div>
      <label>Competência<input type="month" value={competence} onChange={event => { if (event.target.value) setCompetence(event.target.value) }} /></label>
    </div>
    {visible.state === 'loading' && <OperationalLoading>Carregando cartões…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {visible.state === 'loaded' && <Loaded data={visible.data} onOpenCard={onOpenCard} />}
  </section>
}

function Loaded({ data, onOpenCard }: { data: InvoiceOverview; onOpenCard?: (cardId: string) => void }) {
  return <>
    <dl className="credit-card-limit" aria-label="Resumo da competência">
      <div><dt>Obrigação da competência</dt><dd>{formatBrlDecimal(data.obligation)}</dd></div>
      <div><dt>Pagamentos registrados</dt><dd>{formatBrlDecimal(data.paid)}</dd></div>
      <div><dt>Saldo a pagar</dt><dd>{formatBrlDecimal(data.remaining)}</dd></div>
    </dl>
    <CoverageNotice coverage={data.coverage} />
    {data.cards.length === 0 && <p className="meta">Nenhum cartão cadastrado.</p>}
    <div className="card-overview__grid">{data.cards.map(card => <CardSummary key={card.cardId} card={card} onOpenCard={onOpenCard} />)}</div>
  </>
}

export function CoverageNotice({ coverage }: { coverage: { complete: boolean; reasons: string[] } }) {
  if (coverage.complete) return null
  return <div className="notice notice--warning" role="status">
    <strong>Dados incompletos — os valores abaixo mostram o que é conhecido.</strong>
    <ul>{coverageText(coverage.reasons).map(text => <li key={text}>{text}</li>)}</ul>
  </div>
}

function CardSummary({ card, onOpenCard }: { card: CardOverviewItem; onOpenCard?: (cardId: string) => void }) {
  return <article className="card-overview__card" aria-label={card.name}>
    <header>
      <h4>{card.name}</h4>
      {card.scope && <span className={`badge badge--${card.scope.toLowerCase()}`}>Cartão {card.scope}</span>}
      <span className="badge">{card.origin === 'Pluggy' ? 'Origem: Pluggy' : 'Origem: Manual'}</span>
    </header>
    <p className="meta">{card.issuer}{card.lastFour ? ` · •••• ${card.lastFour}` : ''} · {syncLabels[card.sync.status] ?? card.sync.status}{card.sync.lastSyncedAt ? ` em ${formatDate(card.sync.lastSyncedAt.slice(0, 10))}` : ''}</p>
    <dl className="card-overview__facts">
      <div><dt>Conta pagadora</dt><dd>{card.payer ? `${card.payer.name}${card.payer.scope ? ` (${card.payer.scope})` : ''}` : 'Não definida'}</dd></div>
      <div><dt>Limite disponível</dt><dd>{formatBrlDecimal(card.availableLimit)} <small>({card.limitSource === 'Bank' ? 'informado pelo banco' : 'calculado'}; não é saldo)</small></dd></div>
      <div><dt>Fatura atual {card.currentInvoice?.provisional ? '(provisória)' : ''}</dt><dd>{card.currentInvoice ? formatBrlDecimal(card.currentInvoice.obligation) : '—'}</dd></div>
      <div><dt>Próximo vencimento</dt><dd>{card.nextDue ? `${formatDate(card.nextDue.due)} · ${formatBrlDecimal(card.nextDue.remaining)}` : 'Nada a vencer'}</dd></div>
      {card.overdueRemaining !== '0.00' && <div><dt>Em atraso</dt><dd>{formatBrlDecimal(card.overdueRemaining)}</dd></div>}
    </dl>
    <CoverageNotice coverage={card.coverage} />
    {card.invoices.length === 0 ? <p className="meta">Sem fatura nesta competência.</p> : card.invoices.map(invoice => <InvoiceRow key={`${invoice.competence}-${invoice.closing}`} invoice={invoice} />)}
    {onOpenCard && <button type="button" onClick={() => onOpenCard(card.cardId)}>Ver compras e parcelas</button>}
  </article>
}

export function InvoiceRow({ invoice }: { invoice: CardInvoiceView }) {
  return <section className="card-invoice" aria-label={`Fatura ${invoice.competence}`}>
    <div className="card-invoice__dims" aria-label="Estado da fatura">
      <span className={`badge badge--cycle-${invoice.cycle.toLowerCase()}`} title="Fechamento">{cycleLabels[invoice.cycle]}</span>
      <span className={`badge badge--pay-${invoice.paymentState.toLowerCase()}`} title="Pagamento">{paymentStateLabels[invoice.paymentState]}</span>
      <span className="badge" title="Conciliação">{billReconciliationLabels[invoice.reconciliation] ?? invoice.reconciliation}</span>
      {invoice.provisional && <span className="badge badge--estimated">Provisória</span>}
    </div>
    <p className="meta">Fecha {formatDate(invoice.closing)} · vence {formatDate(invoice.due)} · {basisLabels[invoice.basis]}</p>
    <dl className="invoice-totals card-invoice__totals">
      <span><dt>Total bancário</dt><dd>{invoice.bankTotal ? formatBrlDecimal(invoice.bankTotal) : '—'}</dd></span>
      <span><dt>Total projetado pelo Saldo360</dt><dd>{formatBrlDecimal(invoice.projectedTotal)}</dd></span>
      <span><dt>Pagamentos confirmados</dt><dd>{formatBrlDecimal(invoice.paid)}</dd></span>
      <span><dt>Saldo restante</dt><dd>{formatBrlDecimal(invoice.remaining)}</dd></span>
    </dl>
    {invoice.difference && invoice.difference !== '0.00' && <p className="meta">Diferença de conciliação (banco − importado): {formatBrlDecimal(invoice.difference)}. O total do banco permanece; nada é compensado.</p>}
    {invoice.phase !== 'Closed' && <p className="meta">Composição: importado {formatBrlDecimal(invoice.components.composition)} · pendente {formatBrlDecimal(invoice.components.pendingKnown)} · parcelas informadas {formatBrlDecimal(invoice.components.knownInstallments)} · <span className="estimated">estimadas {formatBrlDecimal(invoice.components.estimated)}</span></p>}
    {invoice.unprojectedInstallments > 0 && <p className="meta" role="status">{invoice.unprojectedInstallments} parcela(s) restante(s) sem evidência para projetar: o total pode estar subestimado.</p>}
  </section>
}
