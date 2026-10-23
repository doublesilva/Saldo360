import { useEffect, useRef, useState } from 'react'
import { searchLatestCardEmail, type CardLatestEmailResult, type FinancialCreditCard } from '../api/creditCards'
import { FinancialDocumentInbox } from './FinancialDocumentInbox'

const labels: Record<string, string> = {
  Found: 'Fatura encontrada.', AlreadyReceived: 'Documento já recebido.', NoMatch: 'Nenhuma fatura encontrada.',
  Unavailable: 'Gmail indisponível. Tente novamente mais tarde.', Busy: 'Já existe uma busca em andamento para este cartão.',
  NeedsReview: 'A identificação do cartão requer revisão. Nenhuma associação automática foi realizada.',
  QueryMissing: 'Este cartão não possui pesquisa de faturas configurada. Configure a query Gmail no cadastro do cartão.',
  Error: 'Erro na pesquisa. Tente novamente ou refine a query Gmail.',
}

export function CardLatestEmailSearch({ workspaceId, card, onConfigure, onChanged }: {
  workspaceId: string; card?: FinancialCreditCard; onConfigure?: (id: string) => void; onChanged?: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<CardLatestEmailResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [openInbox, setOpenInbox] = useState(false)
  const mounted = useRef(true)
  const inFlight = useRef(false)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const search = async () => {
    if (!card?.gmailQuery || inFlight.current) return
    inFlight.current = true; setBusy(true); setResult(null); setError(null); setOpenInbox(false)
    try {
      const response = await searchLatestCardEmail(workspaceId, card.id)
      if (!mounted.current) return
      if (response.ok) setResult(response.value)
      else setError(Object.values(response.errors).flat()[0] ?? labels.Error)
    } catch { if (mounted.current) setError(labels.Error) }
    finally { if (mounted.current) { inFlight.current = false; setBusy(false) } }
  }
  if (!card) return null
  return <section aria-label="Busca de fatura no e-mail" aria-live="polite">
    <h5>Fatura por e-mail</h5>
    {!card.gmailQuery && <><p>{labels.QueryMissing}</p><button type="button" onClick={() => onConfigure?.(card.id)}>Configurar no cadastro do cartão</button></>}
    <button type="button" disabled={!card.gmailQuery || busy || card.isArchived} onClick={() => void search()}>{busy ? 'Buscando no Gmail…' : 'Buscar fatura no e-mail'}</button>
    {error && <p role="alert">{error}</p>}
    {result && <div><p>{labels[result.status] ?? labels.Error}</p>{result.status !== 'NeedsReview' && result.failureCode === 'CardIdentificationRequired' && <p>{labels.NeedsReview}</p>}{result.documentId && <>
      <p>{card.name} · {result.fileName} · {result.receivedAt ? new Date(result.receivedAt).toLocaleString('pt-BR') : 'Data indisponível'} · Inbox: {result.inboxStatus}</p>
      <button type="button" onClick={() => setOpenInbox(true)}>Abrir documento na Inbox</button>
    </>}</div>}
    {openInbox && result?.documentId && <FinancialDocumentInbox key={result.documentId} workspaceId={workspaceId} initialDocumentId={result.documentId} onChanged={onChanged} />}
    <p className="meta">A busca recebe o PDF sem confirmar lançamentos financeiros. O upload manual continua disponível abaixo.</p>
  </section>
}
