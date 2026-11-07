import { useCallback } from 'react'
import { getBudgetCards, type BudgetCardItem, type BudgetCards } from '../../api/overview'
import { formatDate } from '../../money'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { money, monthTitle } from './overviewText'
import { useLoad } from './useLoad'

// Detalhamento inline da linha "Cartões de crédito" (só carrega ao expandir). Valores, estados e datas vêm do servidor, na mesma base de `pfBudgetControl.cards.projected`:
// nada é somado aqui. Sem barra por cartão (a meta é única para todos os cartões). O estado da fatura e o de pagamento são informações separadas.

const invoiceText = { Closed: 'Fechada', Open: 'Aberta', Future: 'Futura', ClosingUnconfirmed: 'Fechamento não confirmado' } as const
const paymentText = { Unpaid: 'não paga', Partial: 'paga em parte', Paid: 'paga', Overpaid: 'paga a mais' } as const
const notes: Record<string, string> = { MissingPayerAccount: 'Há cartão sem conta pagadora: ele não entra neste orçamento.', OfficialTotalUnavailable: 'Detalhamento parcial: não foi possível conferir com o total do orçamento.' }

const day = (iso: string) => formatDate(iso).slice(0, 5)
const cardIcon = <svg className="ov-icon" viewBox="0 0 16 16" aria-hidden="true"><rect x="1.75" y="3.5" width="12.5" height="9" rx="1.5" /><path d="M1.75 6.5h12.5M4.5 10h2.5" /></svg>
const basketIcon = <svg className="ov-icon" viewBox="0 0 16 16" aria-hidden="true"><path d="M2 6.5h12l-1.2 6.2a1 1 0 0 1-1 .8H4.2a1 1 0 0 1-1-.8zM5.5 6.5 8 2.5l2.5 4" /></svg>

function dates(card: BudgetCardItem) {
  const parts = [card.closing ? `Fecha ${day(card.closing)}` : null, card.due ? `Vence ${day(card.due)}` : null].filter(Boolean)
  return parts.length === 0 ? 'Datas ainda não conhecidas' : parts.join(' · ')
}

/** Uma só linha de qualidade, por prioridade: dados parciais, datas estimadas e, por fim, se o valor é confirmado ou projetado. */
function Quality({ card }: { card: BudgetCardItem }) {
  if (card.quality === 'Incomplete') return <small className="ov-cardrows__quality ov-cardrows__quality--warn"><span aria-hidden="true">⚠ </span>Dados parciais</small>
  if (card.datesEstimated) return <small className="ov-cardrows__quality">Datas estimadas</small>
  return card.quality === 'Confirmed' ? <small className="ov-cardrows__quality ov-cardrows__quality--ok">Confirmada pelo banco</small> : <small className="ov-cardrows__quality">Valor projetado</small>
}

function Body({ data, competence, onOpenCards }: { data: BudgetCards; competence: string; onOpenCards: () => void }) {
  const month = monthTitle(competence).split(' de ')[0].toLowerCase()
  return <>
    <p className="ov-cardrows__head"><strong>Faturas de {month}</strong><span>{data.cards.length} {data.cards.length === 1 ? 'cartão' : 'cartões'}</span></p>
    {data.cards.length === 0 ? <p className="ov-meta">Nenhum cartão com gasto nesta competência.</p> : <ul className="ov-cardrows">{data.cards.map(card => <li key={card.cardId}>
      <button type="button" className="ov-cardrows__button" onClick={onOpenCards} aria-label={`${card.name}, ${money(card.projected)}, fatura ${invoiceText[card.invoiceState].toLowerCase()}. Ver em Cartões`}>
        <span className="ov-cardrows__icon">{cardIcon}</span>
        <span className="ov-cardrows__main"><strong>{card.name}</strong>
          <small><span className={`ov-cardrows__state${card.invoiceState === 'ClosingUnconfirmed' ? ' ov-cardrows__state--warn' : ''}`}>{invoiceText[card.invoiceState]}</span>{card.paymentState ? ` · ${paymentText[card.paymentState]}` : ''} · {dates(card)}</small>
          <Quality card={card} /></span>
        <span className="ov-mono ov-cardrows__amount">{money(card.projected)}</span></button></li>)}</ul>}
    {data.foodApplied !== '0.00' && <p className="ov-cardrows__extra"><span className="ov-cardrows__icon">{basketIcon}</span><span className="ov-cardrows__main"><strong>Previsão alimentar adicional dentro da meta</strong></span><span className="ov-mono ov-cardrows__amount">{money(data.foodApplied)}</span></p>}
    {data.foodReserve !== null && data.foodApplied === '0.00' && <p className="ov-cardrows__extra ov-cardrows__extra--note"><span className="ov-cardrows__icon">{basketIcon}</span><span className="ov-cardrows__main"><strong>Reserva alimentar</strong><small>{money(data.foodReserve)} dentro da meta dos cartões.</small></span></p>}
    {data.reasons.includes('UnexplainedDifference') && <p className="ov-note" role="status">Detalhamento parcial: o total oficial dos cartões é {money(data.official)}.</p>}
    {data.reasons.filter(reason => notes[reason]).map(reason => <p key={reason} className="ov-note" role="status">{notes[reason]}</p>)}
  </>
}

export function OverviewBudgetCardDetails({ workspaceId, competence, id, onOpenCards }: { workspaceId: string; competence: string; id: string; onOpenCards: () => void }) {
  const { load, retry } = useLoad(`${workspaceId}|${competence}`, useCallback((signal: AbortSignal) => getBudgetCards(workspaceId, competence, signal), [workspaceId, competence]))
  return <div id={id} className="ov-cardrows__panel">
    {load.state === 'loaded' ? <Body data={load.data} competence={competence} onOpenCards={onOpenCards} />
      : load.state === 'error' ? <OperationalError error={load.error} onRetry={retry} retryLabel="Recarregar" /> : <OperationalLoading>Carregando os cartões…</OperationalLoading>}
  </div>
}
