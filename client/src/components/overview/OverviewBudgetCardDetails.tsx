import { useCallback } from 'react'
import { getBudgetCards, type BudgetCardItem, type BudgetCards } from '../../api/overview'
import { formatDate } from '../../money'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { money } from './overviewText'
import { useLoad } from './useLoad'

// Detalhamento inline da linha "Cartões de crédito" (só carrega ao expandir). Valores, estados e datas vêm do servidor, na mesma base de `pfBudgetControl.cards.projected`:
// nada é somado aqui. Sem barra por cartão (a meta é única para todos os cartões). O estado da fatura e o de pagamento são informações separadas.

const invoiceText = { Closed: 'Fatura fechada', Open: 'Fatura aberta', Future: 'Fatura futura', ClosingUnconfirmed: 'Fechamento não confirmado' } as const
const qualityText = { Confirmed: 'valor confirmado pelo banco', Projected: 'valor projetado', Incomplete: 'valor incompleto' } as const
const paymentText = { Unpaid: 'não paga', Partial: 'paga em parte', Paid: 'paga', Overpaid: 'paga a mais' } as const
const notes: Record<string, string> = { MissingPayerAccount: 'Há cartão sem conta pagadora: ele não entra neste orçamento.', OfficialTotalUnavailable: 'Detalhamento parcial: não foi possível conferir com o total do orçamento.' }

function dates(card: BudgetCardItem) {
  const parts = [card.closing ? `fecha em ${formatDate(card.closing)}` : null, card.due ? `vence em ${formatDate(card.due)}` : null].filter(Boolean)
  return parts.length === 0 ? 'datas ainda não conhecidas' : `${parts.join(' · ')}${card.datesEstimated ? ' (datas estimadas)' : ''}`
}

function Body({ data, onOpenCards }: { data: BudgetCards; onOpenCards: () => void }) {
  return <>
    {data.cards.length === 0 ? <p className="ov-meta">Nenhum cartão com gasto nesta competência.</p> : <ul className="ov-cardrows">{data.cards.map(card => <li key={card.cardId}>
      <button type="button" className="ov-cardrows__button" onClick={onOpenCards} aria-label={`${card.name}, ${money(card.projected)}, ${invoiceText[card.invoiceState]}. Ver em Cartões`}>
        <span className="ov-cardrows__main"><strong>{card.name}</strong>
          <small>{invoiceText[card.invoiceState]}{card.paymentState ? ` · ${paymentText[card.paymentState]}` : ''} · {dates(card)}</small>
          <small>{qualityText[card.quality]}</small></span>
        <span className="ov-mono">{money(card.projected)}</span></button></li>)}</ul>}
    {data.foodApplied !== '0.00' && <p className="ov-cardrows__extra"><span>Previsão alimentar adicional dentro da meta</span><span className="ov-mono">{money(data.foodApplied)}</span></p>}
    {data.foodReserve !== null && data.foodApplied === '0.00' && <p className="ov-note">Reserva alimentar: {money(data.foodReserve)} dentro da meta dos cartões.</p>}
    {data.reasons.includes('UnexplainedDifference') && <p className="ov-note" role="status">Detalhamento parcial: o total oficial dos cartões é {money(data.official)}.</p>}
    {data.reasons.filter(reason => notes[reason]).map(reason => <p key={reason} className="ov-note" role="status">{notes[reason]}</p>)}
  </>
}

export function OverviewBudgetCardDetails({ workspaceId, competence, id, onOpenCards }: { workspaceId: string; competence: string; id: string; onOpenCards: () => void }) {
  const { load, retry } = useLoad(`${workspaceId}|${competence}`, useCallback((signal: AbortSignal) => getBudgetCards(workspaceId, competence, signal), [workspaceId, competence]))
  return <div id={id} className="ov-cardrows__panel">
    {load.state === 'loaded' ? <Body data={load.data} onOpenCards={onOpenCards} />
      : load.state === 'error' ? <OperationalError error={load.error} onRetry={retry} retryLabel="Recarregar" /> : <OperationalLoading>Carregando os cartões…</OperationalLoading>}
  </div>
}
