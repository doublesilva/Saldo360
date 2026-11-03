import { useEffect, useState } from 'react'
import { getCardPreview, listAvailableAccounts, listIntegrationConnections, type AvailableAccount, type CardPreview } from '../api/pluggyIntegration'
import type { PluggyConnection } from '../api/pluggyPoc'
import { formatMonthYear } from '../dates'
import { formatDate } from '../money'
import { money } from '../pluggyFormat'
import { OperationalError, OperationalLoading } from './OperationalFeedback'

const flagText: Record<string, string> = {
  NoBillsReturned: 'O banco não devolveu faturas: não há como conferir o valor a pagar.',
  AwaitingBankBill: 'A última fatura devolvida já venceu; a fatura em aberto ainda não existe no banco e o ciclo atual é só projeção.',
  NoClosingDate: 'O banco não informou a data de fechamento; o ciclo precisará ser confirmado por você.',
  FractionalBillCents: 'Há faturas com frações de centavo; o total do banco é mantido como informado.',
  CreditBill: 'Há fatura com total negativo (crédito).',
  TransactionsTruncated: 'A leitura de compras foi interrompida por limite de páginas: os dados podem estar incompletos.',
  HistoryBeforeCutoffExcluded: 'Há lançamentos anteriores à data de corte que não serão importados.',
  InstallmentsFromBeforeCutoff: 'Há compras parceladas feitas antes do corte cujas parcelas futuras estão dentro da janela: elas virão do banco nos movimentos a partir do corte.',
  BillsSpanCutoff: 'Há faturas que incluem lançamentos anteriores ao corte: a composição local ficará incompleta e só o total informado pelo banco valerá. Considere um corte anterior.',
}

type Status = { label: string; tone: 'available' | 'linked' | 'unavailable' }
/** Cartão disponível (pode ser vinculado), já vinculado ou indisponível para integração. A tela nunca cria vínculo. */
const statusOf = (account: AvailableAccount): Status =>
  account.notLinkableReason === 'AlreadyLinked' ? { label: 'Já vinculado ao Saldo360', tone: 'linked' }
    : account.linkable ? { label: 'Disponível para vincular', tone: 'available' } : { label: 'Indisponível para integração', tone: 'unavailable' }

/**
 * Cartões identificados pela Pluggy (PROJ-02). Somente leitura: lista os cartões das conexões e, sob demanda, mostra o que o banco informa (limite, faturas e cobranças
 * futuras). Não vincula, não cria cartão e não importa nada; só mostra o que o provedor devolveu e diz o que não veio.
 */
/** Proposta inicial de corte do histórico; o usuário pode alterar. Nunca é ampliada automaticamente. */
export const PROPOSED_IMPORT_FROM = '2026-10-01'

export function PluggyCardsPreview({ workspaceId }: { workspaceId: string }) {
  const [importFrom, setImportFrom] = useState(PROPOSED_IMPORT_FROM)
  const [accounts, setAccounts] = useState<{ connection: PluggyConnection; items: AvailableAccount[] }[] | null>(null)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    const controller = new AbortController()
    listIntegrationConnections(workspaceId, controller.signal).then(async list => {
      const loaded = await Promise.all(list.map(async connection => ({ connection, items: (await listAvailableAccounts(workspaceId, connection.id, controller.signal)).filter(a => a.kind === 'Card') })))
      if (!controller.signal.aborted) setAccounts(loaded)
    }).catch((cause: unknown) => { if (!controller.signal.aborted) setError(cause) })
    return () => controller.abort()
  }, [workspaceId])

  if (error !== null) return <OperationalError error={error} />
  if (accounts === null) return <OperationalLoading>Consultando os cartões na Pluggy…</OperationalLoading>
  const cards = accounts.flatMap(entry => entry.items.map(account => ({ connection: entry.connection, account })))
  return <section className="pluggy-cards" aria-labelledby="pluggy-cards-title">
    <h3 id="pluggy-cards-title">Cartões identificados pela Pluggy</h3>
    <p className="meta">Somente leitura: esta tela não vincula nem cria cartões e não importa compras. Para vincular, use o cadastro de contas e cartões.</p>
    {cards.length === 0 && <p className="meta">Nenhum cartão de crédito foi identificado nas conexões.</p>}
    <label htmlFor="pluggy-cutoff">Data inicial de importação (proposta)</label>
    <input id="pluggy-cutoff" type="date" value={importFrom} onChange={event => setImportFrom(event.target.value)} />
    <p className="meta">Compras anteriores a esta data não seriam importadas. As prévias abaixo mostram o impacto; nada é importado nem vinculado.</p>
    <ul className="pluggy-cards__list">{cards.map(({ connection, account }) => <CardEntry key={`${account.pluggyAccountId}|${importFrom}`} workspaceId={workspaceId} connection={connection} account={account} importFrom={importFrom} />)}</ul>
  </section>
}

function CardEntry({ workspaceId, connection, account, importFrom }: { workspaceId: string; connection: PluggyConnection; account: AvailableAccount; importFrom: string }) {
  const [state, setState] = useState<{ kind: 'idle' } | { kind: 'loading' } | { kind: 'error'; error: unknown } | { kind: 'ready'; preview: CardPreview }>({ kind: 'idle' })
  const status = statusOf(account)
  const open = () => {
    setState({ kind: 'loading' })
    getCardPreview(workspaceId, connection.id, account.pluggyAccountId, importFrom || null).then(preview => setState({ kind: 'ready', preview })).catch((cause: unknown) => setState({ kind: 'error', error: cause }))
  }
  const last4 = account.maskedNumber?.replace(/\D/g, '').slice(-4)
  return <li className="pluggy-cards__item">
    <h4>{account.displayName}</h4>
    <p className="meta">{connection.institution ?? 'Instituição não informada'} · {last4 ? `final ${last4}` : 'final não informado'} · <span className={`pluggy-cards__status pluggy-cards__status--${status.tone}`}>{status.label}</span></p>
    {state.kind === 'idle' && <button type="button" onClick={open}>Ver prévia do cartão</button>}
    {state.kind === 'loading' && <OperationalLoading>Consultando o cartão na Pluggy…</OperationalLoading>}
    {state.kind === 'error' && <OperationalError error={state.error} onRetry={open} />}
    {state.kind === 'ready' && <PreviewDetails preview={state.preview} />}
  </li>
}

function PreviewDetails({ preview }: { preview: CardPreview }) {
  const informed = (value: string | null) => value === null ? 'Não informado pelo banco' : money(value)
  return <div className="pluggy-cards__preview">
    <dl className="credit-card-limit">
      <div><dt>Limite</dt><dd>{informed(preview.creditLimit)}</dd></div>
      <div><dt>Usado</dt><dd>{informed(preview.usedLimit)}</dd></div>
      <div><dt>Disponível</dt><dd>{informed(preview.availableLimit)}</dd></div>
      <div><dt>Vencimento</dt><dd>{preview.dueDay === null ? 'Não informado pelo banco' : `dia ${preview.dueDay}`}</dd></div>
      <div><dt>Última fatura</dt><dd>{preview.lastBillDue === null || preview.lastBillTotal === null ? 'Nenhuma devolvida' : `${formatDate(preview.lastBillDue)} · ${money(preview.lastBillTotal)}`}</dd></div>
    </dl>
    <p className="meta">Compras lidas: {preview.transactionsRead} ({preview.posted} efetivadas, {preview.pending} pendentes, {preview.withInstallments} parceladas) · faturas lidas: {preview.billsRead}.</p>
    {preview.cutoff && <CutoffImpact cutoff={preview.cutoff} />}
    {preview.flags.length > 0 && <ul className="plan-card__notes" aria-label="Pontos de atenção do cartão">{preview.flags.map(flag => <li key={flag}>{flagText[flag] ?? flag}</li>)}</ul>}
    {preview.upcoming.length > 0
      ? <table className="monthly-plan__table"><caption>Cobranças já conhecidas pelo banco (não é a fatura nem a meta)</caption>
        <thead><tr><th scope="col">Mês</th><th scope="col">Lançamentos</th><th scope="col">Parcelas</th><th scope="col">Valor</th></tr></thead>
        <tbody>{preview.upcoming.map(row => <tr key={row.month}><th scope="row">{formatMonthYear(`${row.month}-01`)}</th><td>{row.entries}</td><td>{row.installments}</td><td>{money(row.amount)}</td></tr>)}</tbody></table>
      : <p className="meta">O banco não informou cobranças futuras para este cartão. Isso não significa gasto zero.</p>}
  </div>
}

function CutoffImpact({ cutoff }: { cutoff: NonNullable<CardPreview['cutoff']> }) {
  return <section aria-label="Impacto da data de corte" className="pluggy-cards__cutoff">
    <h5>Impacto do corte em {formatDate(cutoff.importFrom)}</h5>
    <p className="meta">{cutoff.importedTransactions} lançamento(s) a partir do corte seriam importados; {cutoff.excludedTransactions} anterior(es) ficariam de fora.
      {' '}{cutoff.installmentSeriesStartedBefore} compra(s) parcelada(s) feita(s) antes do corte têm {cutoff.installmentsInsideWindow} parcela(s) dentro da janela.</p>
    {cutoff.billsSpanningCutoff.length > 0 && <table className="monthly-plan__table"><caption>Faturas que misturam lançamentos de antes e depois do corte</caption>
      <thead><tr><th scope="col">Vencimento</th><th scope="col">Total do banco</th><th scope="col">A partir do corte</th><th scope="col">Antes do corte</th></tr></thead>
      <tbody>{cutoff.billsSpanningCutoff.map(bill => <tr key={bill.dueDate}><th scope="row">{formatDate(bill.dueDate)}</th><td>{money(bill.bankTotal)}</td><td>{money(bill.fromCutoff)}</td><td>{money(bill.beforeCutoff)}</td></tr>)}</tbody></table>}
  </section>
}
