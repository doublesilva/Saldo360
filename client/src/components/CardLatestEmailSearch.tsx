import { useEffect, useRef, useState } from 'react'
import { searchLatestCardEmail, type CardLatestEmailResult, type FinancialCreditCard } from '../api/creditCards'
import { FinancialDocumentInbox } from './FinancialDocumentInbox'
import { analyzeStatement } from '../api/financialDocuments'
import { PdfPasswordHint } from './PdfPasswordHint'

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
  const [password, setPassword] = useState('')
  const [unknownOutcome, setUnknownOutcome] = useState(false)
  const mounted = useRef(true)
  const inFlight = useRef(false)
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  const search = async () => {
    if (!card?.gmailQuery || inFlight.current || unknownOutcome) return
    inFlight.current = true; setBusy(true); setResult(null); setError(null); setOpenInbox(false); setPassword('')
    try {
      const response = await searchLatestCardEmail(workspaceId, card.id)
      if (!mounted.current) return
      if (response.ok) { setResult(response.value); if (response.value.processing?.consolidation.status === 'Imported') onChanged?.() }
      else if ([408, 409, 502, 503, 504].includes(response.status ?? 0)) { setUnknownOutcome(true); setError('Resultado ainda desconhecido. Consulte a Inbox; a busca e a análise não serão repetidas automaticamente.') }
      else setError(Object.values(response.errors).flat()[0] ?? labels.Error)
    } catch { if (mounted.current) { setUnknownOutcome(true); setError('Resultado ainda desconhecido. Consulte a Inbox; a busca e a análise não serão repetidas automaticamente.') } }
    finally { if (mounted.current) { inFlight.current = false; setBusy(false) } }
  }
  const resume = async () => {
    if (!result?.documentId || !card || inFlight.current || unknownOutcome || !password) return
    inFlight.current = true; setBusy(true); setError(null)
    const secret = password; setPassword('')
    try {
      const response = await analyzeStatement(workspaceId, result.documentId, card.id, secret)
      if (!mounted.current) return
      if (response.ok) {
        const code = response.value.analysis?.failureCode ?? response.value.analysis?.consolidationBlockReason ?? null
        setResult({ ...result, processing: { analysis: response.value, consolidation: { status: response.value.status === 'Reliable' ? 'AwaitingConfirmation' : 'NeedsReview', invoiceId: null, version: null, officialTotal: null, failureCode: code } }, failureCode: code, inboxStatus: response.value.status === 'Reliable' ? 'ReadyForReview' : 'NeedsReview' })
      }
      else if ([408, 409, 502, 503, 504].includes(response.status)) { setUnknownOutcome(true); setOpenInbox(true); setError('Resultado ainda desconhecido. Consulte a Inbox, sem repetir a análise.') }
      else setError('Não foi possível retomar o processamento.')
    } catch { if (mounted.current) { setUnknownOutcome(true); setOpenInbox(true); setError('Resultado ainda desconhecido. Consulte a Inbox, sem repetir a análise.') } }
    finally { if (mounted.current) { inFlight.current = false; setBusy(false) } }
  }
  const financial = result?.processing?.consolidation
  const passwordNeeded = ['PasswordRequired', 'PasswordRejected'].includes(financial?.failureCode ?? result?.failureCode ?? '')
  if (!card) return null
  return <section aria-label="Busca de fatura no e-mail" aria-live="polite">
    <h5>Fatura por e-mail</h5>
    {!card.gmailQuery && <><p>{labels.QueryMissing}</p><button type="button" onClick={() => onConfigure?.(card.id)}>Configurar no cadastro do cartão</button></>}
    <button type="button" disabled={!card.gmailQuery || busy || unknownOutcome || card.isArchived} onClick={() => void search()}>{busy ? 'Buscando no Gmail…' : 'Buscar fatura no e-mail'}</button>
    {error && <p role="alert">{error}</p>}
    <PdfPasswordHint code={result?.processing?.analysis.analysis?.passwordResolutionCode} />
    {financial && <p>{['Imported', 'AlreadyImported'].includes(financial.status) ? 'Fatura já confirmada.' : financial.status === 'AwaitingConfirmation' ? 'Análise concluída. Nenhum dado financeiro gravado; confirme na Inbox.' : financial.status === 'Busy' ? 'Processamento já em andamento.' : `Revisão necessária: ${financial.failureCode ?? financial.status}.`}</p>}
    {passwordNeeded && !unknownOutcome && <form onSubmit={e => { e.preventDefault(); void resume() }}><label>Senha do PDF<input type="password" autoComplete="off" maxLength={128} value={password} onChange={e => setPassword(e.target.value)} /></label><button type="submit" disabled={busy || !password}>Desbloquear e continuar automaticamente</button></form>}
    {result && <div><p>{labels[result.status] ?? labels.Error}</p>{result.status !== 'NeedsReview' && result.failureCode === 'CardIdentificationRequired' && <p>{labels.NeedsReview}</p>}{result.documentId && <>
      <p>{card.name} · {result.fileName} · {result.receivedAt ? new Date(result.receivedAt).toLocaleString('pt-BR') : 'Data indisponível'} · Inbox: {result.inboxStatus}</p>
      <button type="button" onClick={() => setOpenInbox(true)}>Abrir documento na Inbox</button>
    </>}</div>}
    {unknownOutcome && !result?.documentId && <button type="button" onClick={() => setOpenInbox(true)}>Consultar documentos recebidos na Inbox</button>}
    {openInbox && <FinancialDocumentInbox key={result?.documentId ?? workspaceId} workspaceId={workspaceId} initialDocumentId={result?.documentId ?? undefined} initialCreditCardId={card.id} onChanged={onChanged} />}
    <p className="meta">A busca recebe e analisa o PDF sem importar. A confirmação financeira é explícita na Inbox. O upload manual continua disponível abaixo.</p>
  </section>
}
