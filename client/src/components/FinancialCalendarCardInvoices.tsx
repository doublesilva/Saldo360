import { useEffect, useState } from 'react'
import { getInvoiceOverview, paymentStateLabels, type CardInvoiceView, type CardOverviewItem } from '../api/cardProjection'
import { toUserMessage } from '../api/apiErrors'
import { formatBrlDecimal, formatDate } from '../money'
import { OperationalError, OperationalLoading } from './OperationalFeedback'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; message: string } | { key: string; state: 'loaded'; cards: CardOverviewItem[]; complete: boolean }

const statusText = (invoice: CardInvoiceView) => invoice.awaitingBankBill ? 'Aguardando a fatura do banco' : invoice.provisional ? 'Estimada (provisória)' : 'Confirmada pelo banco'

/**
 * Faturas de cartão do mês na Agenda, pelo VENCIMENTO: é a saída de caixa que entra no saldo projetado (uma por fatura). As compras individuais não aparecem
 * aqui como saídas: elas são consumo do Orçamento. Depois de um pagamento só o restante conta. Usa a mesma projeção de Cartões; nada é recalculado.
 */
export function FinancialCalendarCardInvoices({ workspaceId, month, accountId, selectedDate, refreshKey, onOpenCards }: {
  workspaceId: string; month: string; accountId: string; selectedDate: string; refreshKey: number; onOpenCards?: () => void
}) {
  const competence = month.slice(0, 7)
  const key = `${workspaceId}|${competence}|${refreshKey}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const [retry, setRetry] = useState(0)
  const visible: Load = loaded.key === `${key}|${retry}` ? loaded : { key: `${key}|${retry}`, state: 'loading' }

  useEffect(() => {
    const controller = new AbortController()
    const current = `${key}|${retry}`
    getInvoiceOverview(workspaceId, competence, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key: current, state: 'loaded', cards: data.cards, complete: data.coverage.complete }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key: current, state: 'error', message: toUserMessage(error) }) })
    return () => controller.abort()
  }, [workspaceId, competence, key, retry])

  const rows = visible.state === 'loaded'
    ? visible.cards.filter(card => !accountId || card.payer?.accountId === accountId)
      .flatMap(card => card.invoices.filter(invoice => invoice.due.slice(0, 7) === competence).map(invoice => ({ card, invoice })))
      .sort((a, b) => a.invoice.due.localeCompare(b.invoice.due) || a.card.name.localeCompare(b.card.name, 'pt-BR'))
    : []
  return <section className="calendar-card-invoices" aria-labelledby="calendar-card-invoices-title">
    <h4 id="calendar-card-invoices-title">Faturas de cartão do mês</h4>
    <p className="meta">Pagamento da fatura no vencimento: é o que sai do caixa e do saldo projetado, uma vez por fatura. As compras individuais são consumo do Orçamento e não são somadas de novo.</p>
    {visible.state === 'loading' && <OperationalLoading>Carregando as faturas…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.message} onRetry={() => setRetry(value => value + 1)} />}
    {visible.state === 'loaded' && !visible.complete && <p className="notice notice--warning" role="status">Parte das faturas ainda é estimada ou está incompleta; os valores marcados como estimados podem mudar quando o banco fechar a fatura.</p>}
    {visible.state === 'loaded' && rows.length === 0 && <p className="meta">Nenhuma fatura de cartão vence neste mês{accountId ? ' para os cartões pagos por esta conta' : ''}.</p>}
    {rows.length > 0 && <ul className="calendar-card-invoices__list">{rows.map(({ card, invoice }) => <li key={`${card.cardId}:${invoice.competence}`} className={invoice.due === selectedDate ? 'calendar-card-invoices__selected' : undefined}>
      <span><strong>{card.name}</strong> · vence em {formatDate(invoice.due)}{invoice.due === selectedDate ? ' (dia selecionado)' : ''}</span>
      <span className="meta">{statusText(invoice)} · {paymentStateLabels[invoice.paymentState]}{Number.parseFloat(invoice.paid) > 0 ? ` · já pago ${formatBrlDecimal(invoice.paid)} de ${formatBrlDecimal(invoice.obligation)}` : ''}</span>
      <strong>{Number.parseFloat(invoice.remaining) > 0 ? `Falta pagar ${formatBrlDecimal(invoice.remaining)}` : `Sem restante (${formatBrlDecimal(invoice.obligation)})`}</strong>
    </li>)}</ul>}
    {onOpenCards && <button type="button" onClick={onOpenCards}>Abrir Cartões</button>}
  </section>
}
