import { useCallback, useEffect, useRef, useState } from 'react'
import { analyzeStatement, consolidateStatement, correctStatementCategory, getFinancialDocument, getStatementAnalysis, type StatementAnalysisResult, type StatementConsolidationResult } from '../api/financialDocuments'
import type { FinancialCategory } from '../api/categories'
import { formatBrlDecimal } from '../money'
import { PdfPasswordHint } from './PdfPasswordHint'
import { StatementFinancialClassificationPanel } from './StatementFinancialClassificationPanel'

export function InboxStatementAnalysisPanel({ workspaceId, documentId, cardId, password, categories, blocked, imported = false, documentUpdatedAt, onBusy, onAnalyzed, onPasswordResolution }: {
  workspaceId: string; documentId: string; cardId: string; password: string; categories: FinancialCategory[]; blocked: boolean; imported?: boolean; documentUpdatedAt?: string; onBusy: (busy: boolean) => void; onAnalyzed: (financial?: StatementConsolidationResult) => void; onPasswordResolution?: (code: string | null) => void
}) {
  const [result, setResult] = useState<StatementAnalysisResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [watching, setWatching] = useState(false)
  const unknown = useRef(false)
  const awaitingConfirmation = useRef(false)
  const baselineUpdate = useRef<string | undefined>(undefined)
  const [financial, setFinancial] = useState<StatementConsolidationResult | null>(null)
  const alive = useRef(true); const pending = useRef(false)
  const version = useRef(0)
  const onBusyRef = useRef(onBusy)
  useEffect(() => { onBusyRef.current = onBusy }, [onBusy])
  const onAnalyzedRef = useRef(onAnalyzed)
  useEffect(() => { onAnalyzedRef.current = onAnalyzed }, [onAnalyzed])
  useEffect(() => { alive.current = true; const controller = new AbortController(); const current = version.current; getStatementAnalysis(workspaceId, documentId, controller.signal).then(value => { if (!controller.signal.aborted && current === version.current) { setResult(value); if (value?.status === 'Analyzing') { unknown.current = true; setWatching(true) } } }).catch(() => { if (!controller.signal.aborted && current === version.current) setError('Não foi possível consultar a análise.') }); return () => { alive.current = false; controller.abort() } }, [workspaceId, documentId])
  const consult = useCallback(async (signal?: AbortSignal) => {
    try {
      if (awaitingConfirmation.current) {
        const item = await getFinancialDocument(workspaceId, documentId, signal)
        if (signal?.aborted || !alive.current || item?.status !== 'Imported') return false
        const confirmed = { status: 'AlreadyImported', invoiceId: null, version: null, officialTotal: null, failureCode: null }
        setFinancial(confirmed); onAnalyzedRef.current(confirmed)
        setError(null); unknown.current = false; setWatching(false); onBusyRef.current(false)
        return true
      }
      const value = await getStatementAnalysis(workspaceId, documentId, signal)
      if (signal?.aborted || !alive.current) return false
      // DTO strings and JSON timestamps may format fractional seconds differently.
      // Only a newer persisted update can resolve an ambiguous POST, never the old saved result.
      if (value?.analysis && value.status !== 'Analyzing' && (!baselineUpdate.current || !!value.updatedAt && Date.parse(value.updatedAt) > Date.parse(baselineUpdate.current))) {
        setResult(value); setError(null); unknown.current = false; setWatching(false); onBusyRef.current(false); onAnalyzedRef.current()
        return true
      }
    } catch { /* Keep an unknown outcome blocked; GET failure must never resubmit a paid operation. */ }
    return false
  }, [workspaceId, documentId])
  useEffect(() => {
    if (!watching) return
    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    let reads = 0
    const poll = async () => { if (await consult(controller.signal) || controller.signal.aborted) return; if (++reads < 60) timer = setTimeout(() => void poll(), 3000) }
    timer = setTimeout(() => void poll(), 3000)
    return () => { controller.abort(); clearTimeout(timer) }
  }, [watching, consult])
  const awaitStatus = () => { unknown.current = true; setWatching(true); setError('Resultado ainda não confirmado. Consultando apenas o estado; nenhuma análise será repetida automaticamente.') }
  const run = async (operation: () => ReturnType<typeof analyzeStatement>) => {
    if (pending.current || blocked || unknown.current) return
    pending.current = true; version.current++; baselineUpdate.current = documentUpdatedAt ?? result?.updatedAt ?? undefined; awaitingConfirmation.current = false; setBusy(true); onBusy(true); setError(null)
    try { const response = await operation(); if (!alive.current) return; if (response.ok) { if (['Busy', 'Analyzing'].includes(response.value.status)) awaitStatus(); else { setResult(response.value); onAnalyzed() } } else { if (response.reason !== 'Conflict' && [408, 409, 502, 503, 504].includes(response.status)) awaitStatus(); else setError(Object.values(response.errors).flat()[0] ?? 'A análise requer revisão.') } }
    catch { if (alive.current) awaitStatus() }
    finally { if (alive.current) { pending.current = false; setBusy(false); if (!unknown.current) onBusy(false) } }
  }
  const analysis = result?.analysis && (!cardId || result.analysis.cardId === cardId) ? result.analysis : null
  const passwordCode = analysis?.passwordResolutionCode ?? analysis?.failureCode ?? null
  useEffect(() => { onPasswordResolution?.(passwordCode) }, [onPasswordResolution, passwordCode])
  const known = (result?.workflow ?? analysis?.workflow) === 'KnownInstallments'
  const integral = analysis?.extraction?.financialClassification
  const consolidated = imported || !!financial && ['Imported', 'AlreadyImported', 'NewVersionConsolidated', 'InvoicesLinked'].includes(financial.status)
  const process = async (onlyConsolidate: boolean) => {
    if (!onlyConsolidate) { await run(() => analyzeStatement(workspaceId, documentId, cardId, password || undefined)); return }
    if (pending.current || blocked || unknown.current || !cardId) return
    pending.current = true; version.current++; awaitingConfirmation.current = true; setBusy(true); onBusy(true); setError(null)
    try {
      const response = await consolidateStatement(workspaceId, documentId, cardId)
      if (!alive.current) return
      if (!response.ok) { if ([408, 409, 502, 503, 504].includes(response.status)) awaitStatus(); else setError(Object.values(response.errors).flat()[0] ?? 'Não foi possível processar a fatura.'); return }
      const financialResult = response.value
      setFinancial(financialResult); onAnalyzed(financialResult)
    } catch { if (alive.current) awaitStatus() }
    finally { if (alive.current) { pending.current = false; setBusy(false); if (!unknown.current) onBusy(false) } }
  }
  const extraction = analysis?.extraction?.primary
  const financialLabel = financial && ((known ? {
    Imported: 'Parcelas conhecidas importadas', AlreadyImported: 'Documento já confirmado', InvoicesLinked: 'Faturas compatíveis vinculadas',
    AwaitingConfirmation: 'Aguardando confirmação única', NeedsReview: 'Importação bloqueada',
  } as Record<string, string> : {})[financial.status] ?? ({ Imported: 'Fatura oficial importada', AlreadyImported: 'Fatura já importada', NewVersionConsolidated: 'Nova versão consolidada', NeedsReview: 'Revisão necessária', Conflict: 'Conflito financeiro', Busy: 'Processamento já em andamento. Tente novamente.', NotFound: 'Documento ou cartão indisponível' } as Record<string, string>)[financial.status] ?? 'Processamento bloqueado')
  return <section className="card" aria-label="Análise estruturada da fatura" aria-live="polite">
    <h4>{known ? 'Extração documental e parcelas conhecidas' : 'Análise e categorias da fatura'}</h4>
    <button type="button" disabled={!cardId || blocked || busy || watching || imported} onClick={() => void run(() => analyzeStatement(workspaceId, documentId, cardId, password || undefined))}>{busy ? 'Analisando fatura…' : 'Analisar fatura armazenada'}</button>
    <button type="button" disabled={!cardId || blocked || busy || watching || consolidated} onClick={() => void process(false)}>{busy ? 'Analisando…' : 'Processar sem importar'}</button>
    {watching && <p role="status">Análise em andamento ou resultado ainda desconhecido. <button type="button" onClick={() => void consult()}>Consultar resultado</button> Nenhuma chamada paga será repetida automaticamente.</p>}
    {!integral && analysis?.status === 'Reliable' && !analysis.consolidationBlockReason && !consolidated && <button type="button" disabled={blocked || busy || watching} onClick={() => void process(true)}>{known ? 'Confirmar importação do documento neste cartão' : 'Solicitar consolidação — revalidar análise'}</button>}
    {known && consolidated && <button type="button" disabled={!cardId || blocked || busy || watching} onClick={() => void process(true)}>Vincular faturas compatíveis existentes</button>}
    {known && <p>Extração: {analysis?.status === 'Reliable' ? 'concluída' : 'pendente ou bloqueada'} · Importação: {consolidated ? 'confirmada e persistida' : analysis?.knownInstallmentImport?.eligible ? 'elegível, aguardando confirmação única' : 'não elegível'}. A confirmação associa este documento ao cartão selecionado. Datas desconhecidas não serão preenchidas; nenhuma fatura será criada.</p>}
    {known && financial?.status === 'AwaitingConfirmation' && <p role="status">Nenhum dado financeiro gravado. Confirme o documento para registrar as parcelas conhecidas.</p>}
    {financial && <p role={financial.status === 'Conflict' ? 'alert' : 'status'}>{financialLabel}{financial.version ? ` · versão ${financial.version}` : ''}{financial.officialTotal ? ` · ${formatBrlDecimal(financial.officialTotal)}` : ''}{financial.failureCode ? ` · ${financial.failureCode}` : ''}</p>}
    {!cardId && <p>Selecione explicitamente o cartão para analisar sem novo upload.</p>}
    {error && <p role="alert">{error}</p>}
    <PdfPasswordHint code={analysis?.passwordResolutionCode} />
    {analysis && <><p>Resultado: {analysis.status} · modo {analysis.extraction?.mode ?? 'Indisponível'} · tentativas {analysis.extraction?.attempts ?? 0}</p>
      {!integral && <p>Prévia financeira: {extraction ? 'disponível' : 'indisponível'} · Validação: {analysis.extraction?.validationStatus ?? analysis.extraction?.assessment ?? 'Indisponível'} · Conciliação: {extraction?.reconciliation.status ?? 'Indisponível'}</p>}
      {analysis.consolidationBlockReason && <p role="status">{known ? 'Importação bloqueada para diagnóstico técnico' : 'Prévia não autoriza consolidação. Revisão necessária'}: {analysis.consolidationBlockReason}.</p>}
      {analysis.extraction?.documentWarnings?.map(code => <p key={code}>{({ DocumentIdentityIncomplete: 'Identificação documental incompleta; o cartão selecionado foi mantido como contexto.', IdentityEvidenceUnverified: 'Evidência de identidade não corroborada; revise antes de consolidar.', ParserAgentDifference: 'Há diferenças diagnósticas entre parser e agente.', CardMismatch: 'Identificação incompatível com o cartão selecionado. Consolidação bloqueada.', ParserContextUnavailable: 'Comparação documental do parser indisponível.' } as Record<string, string>)[code] ?? 'Alerta documental para revisão.'}</p>)}
      {analysis.status !== 'Reliable' && <p>{known ? 'Divergência técnica ou evidência pendente; importação bloqueada, sem edição manual de linhas.' : 'Há divergências ou evidências pendentes. Revisão necessária; nenhum lançamento foi importado.'}</p>}
      {analysis.extraction?.comparison && <details><summary>Diferenças entre parser e agente</summary><p>Componentes somente no parser: {analysis.extraction.comparison.itemsOnlyInParser.join(' · ') || 'Nenhum'}</p><p>Componentes somente no agente: {analysis.extraction.comparison.itemsOnlyInAgent.join(' · ') || 'Nenhum'}</p></details>}
      {integral && <StatementFinancialClassificationPanel value={integral} />}
      {extraction && !integral && <><p>Total oficial: {extraction.reconciliation.officialTotal ? formatBrlDecimal(extraction.reconciliation.officialTotal) : 'Indisponível'} · Componentes: {extraction.reconciliation.componentsTotal ? formatBrlDecimal(extraction.reconciliation.componentsTotal) : 'Indisponível'} · Diferença: {extraction.reconciliation.difference ? formatBrlDecimal(extraction.reconciliation.difference) : 'Indisponível'}</p>
        <div className="import-rows">{extraction.items.map((item, index) => { const category = analysis.categories.find(c => c.itemIndex === index); return <article className="import-row" key={index}>
          <strong>{item.normalizedDescription ?? item.originalDescription}</strong>{item.normalizedDescription && item.normalizedDescription !== item.originalDescription && <small>Original: {item.originalDescription}</small>}<span>{item.transactionDate ?? 'Data não identificada'} · {formatBrlDecimal(item.amount)} · {item.nature} · {item.type}</span>
          <small>{item.section} · {item.includedInCurrentTotal ? 'Integra a fatura atual' : 'Informativo / futuro'}</small>
          {category && <><p>{consolidated ? 'Categoria da análise' : category.isProposal ? 'Proposta de categoria' : 'Categoria'}: {category.categoryName} · {category.confidence} · {category.source}</p>
            {!known && <label>Corrigir categoria de {item.originalDescription}<select value={category.categoryId ?? ''} disabled={blocked || busy || !!consolidated} onChange={event => { if (event.target.value) void run(() => correctStatementCategory(workspaceId, documentId, category.key, event.target.value)) }}>
              <option value="">{category.isProposal && !consolidated ? `${category.categoryName} (proposta, não criada)` : category.categoryName}</option>{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select></label>}</>}
        </article> })}</div></>}
    </>}
    {!known && <p className="meta">Analisar e processar não gravam fatos financeiros. A consolidação exige solicitação explícita e revalida análise, identidade, categorias e conciliação.</p>}
    {consolidated && !known && <p className="meta">A composição oficial preserva correções manuais correspondentes de versões anteriores, mesmo quando a análise sugere outra categoria.</p>}
  </section>
}
