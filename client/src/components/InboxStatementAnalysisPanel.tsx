import { useEffect, useRef, useState } from 'react'
import { analyzeStatement, processStatement, consolidateStatement, correctStatementCategory, getStatementAnalysis, type StatementAnalysisResult, type StatementConsolidationResult } from '../api/financialDocuments'
import type { FinancialCategory } from '../api/categories'
import { formatBrlDecimal } from '../money'
import { PdfPasswordHint } from './PdfPasswordHint'

export function InboxStatementAnalysisPanel({ workspaceId, documentId, cardId, password, categories, blocked, imported = false, onBusy, onAnalyzed }: {
  workspaceId: string; documentId: string; cardId: string; password: string; categories: FinancialCategory[]; blocked: boolean; imported?: boolean; onBusy: (busy: boolean) => void; onAnalyzed: (financial?: StatementConsolidationResult) => void
}) {
  const [result, setResult] = useState<StatementAnalysisResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [financial, setFinancial] = useState<StatementConsolidationResult | null>(null)
  const alive = useRef(true); const pending = useRef(false)
  const version = useRef(0)
  useEffect(() => { alive.current = true; const controller = new AbortController(); const current = version.current; getStatementAnalysis(workspaceId, documentId, controller.signal).then(value => { if (!controller.signal.aborted && current === version.current) setResult(value) }).catch(() => { if (!controller.signal.aborted && current === version.current) setError('Não foi possível consultar a análise.') }); return () => { alive.current = false; controller.abort() } }, [workspaceId, documentId])
  const run = async (operation: () => ReturnType<typeof analyzeStatement>) => {
    if (pending.current || blocked) return
    pending.current = true; version.current++; setBusy(true); onBusy(true); setError(null)
    try { const response = await operation(); if (!alive.current) return; if (response.ok) { setResult(response.value); onAnalyzed() } else setError(Object.values(response.errors).flat()[0] ?? 'A análise requer revisão.') }
    catch { if (alive.current) setError('Não foi possível analisar a fatura. Tente novamente.') }
    finally { if (alive.current) { pending.current = false; setBusy(false); onBusy(false) } }
  }
  const analysis = result?.analysis && (!cardId || result.analysis.cardId === cardId) ? result.analysis : null
  const consolidated = imported || !!financial && ['Imported', 'AlreadyImported', 'NewVersionConsolidated'].includes(financial.status)
  const process = async (onlyConsolidate: boolean) => {
    if (pending.current || blocked || !cardId) return
    pending.current = true; version.current++; setBusy(true); onBusy(true); setError(null)
    try {
      const response = onlyConsolidate ? await consolidateStatement(workspaceId, documentId, cardId) : await processStatement(workspaceId, documentId, cardId, password || undefined)
      if (!alive.current) return
      if (!response.ok) { setError(Object.values(response.errors).flat()[0] ?? 'Não foi possível processar a fatura.'); return }
      const financialResult = 'consolidation' in response.value ? response.value.consolidation : response.value
      if ('consolidation' in response.value && response.value.analysis.analysis) setResult(response.value.analysis)
      setFinancial(financialResult); onAnalyzed(financialResult)
    } catch { if (alive.current) setError('Não foi possível processar a fatura. Tente novamente.') }
    finally { if (alive.current) { pending.current = false; setBusy(false); onBusy(false) } }
  }
  const extraction = analysis?.extraction?.primary
  return <section className="card" aria-label="Análise estruturada da fatura" aria-live="polite">
    <h4>Análise e categorias da fatura</h4>
    <button type="button" disabled={!cardId || blocked || busy || imported} onClick={() => void run(() => analyzeStatement(workspaceId, documentId, cardId, password || undefined))}>{busy ? 'Analisando fatura…' : 'Analisar fatura armazenada'}</button>
    <button type="button" disabled={!cardId || blocked || busy} onClick={() => void process(false)}>{busy ? 'Processando / consolidando…' : 'Processar e consolidar fatura'}</button>
    {analysis?.status === 'Reliable' && !consolidated && <button type="button" disabled={blocked || busy} onClick={() => void process(true)}>Pronta para consolidar — consolidar análise</button>}
    {financial && <p role={financial.status === 'Conflict' ? 'alert' : 'status'}>{({ Imported: 'Fatura oficial importada', AlreadyImported: 'Fatura já importada', NewVersionConsolidated: 'Nova versão consolidada', NeedsReview: 'Revisão necessária', Conflict: 'Conflito financeiro', Busy: 'Processamento já em andamento. Tente novamente.', NotFound: 'Documento ou cartão indisponível' } as Record<string, string>)[financial.status] ?? 'Revisão necessária'}{financial.version ? ` · versão ${financial.version}` : ''}{financial.officialTotal ? ` · ${formatBrlDecimal(financial.officialTotal)}` : ''}{financial.failureCode ? ` · ${financial.failureCode}` : ''}</p>}
    {!cardId && <p>Selecione explicitamente o cartão para analisar sem novo upload.</p>}
    {error && <p role="alert">{error}</p>}
    <PdfPasswordHint code={analysis?.passwordResolutionCode} />
    {analysis && <><p>Resultado: {analysis.status} · modo {analysis.extraction?.mode ?? 'Indisponível'} · tentativas {analysis.extraction?.attempts ?? 0}</p>
      {analysis.status !== 'Reliable' && <p>Há divergências ou evidências pendentes. Revisão necessária; nenhum lançamento foi importado.</p>}
      {analysis.status !== 'Reliable' && analysis.extraction?.comparison && <details><summary>Diferenças entre parser e agente</summary><p>Componentes somente no parser: {analysis.extraction.comparison.itemsOnlyInParser.join(' · ') || 'Nenhum'}</p><p>Componentes somente no agente: {analysis.extraction.comparison.itemsOnlyInAgent.join(' · ') || 'Nenhum'}</p></details>}
      {extraction && <><p>Total oficial: {extraction.reconciliation.officialTotal ? formatBrlDecimal(extraction.reconciliation.officialTotal) : 'Indisponível'} · Componentes: {extraction.reconciliation.componentsTotal ? formatBrlDecimal(extraction.reconciliation.componentsTotal) : 'Indisponível'} · Diferença: {extraction.reconciliation.difference ? formatBrlDecimal(extraction.reconciliation.difference) : 'Indisponível'}</p>
        <div className="import-rows">{extraction.items.map((item, index) => { const category = analysis.categories.find(c => c.itemIndex === index); return <article className="import-row" key={index}>
          <strong>{item.normalizedDescription ?? item.originalDescription}</strong>{item.normalizedDescription && item.normalizedDescription !== item.originalDescription && <small>Original: {item.originalDescription}</small>}<span>{item.transactionDate ?? 'Data não identificada'} · {formatBrlDecimal(item.amount)} · {item.nature} · {item.type}</span>
          <small>{item.section} · {item.includedInCurrentTotal ? 'Integra a fatura atual' : 'Informativo / futuro'}</small>
          {category && <><p>{consolidated ? 'Categoria da análise' : category.isProposal ? 'Proposta de categoria' : 'Categoria'}: {category.categoryName} · {category.confidence} · {category.source}</p>
            <label>Corrigir categoria de {item.originalDescription}<select value={category.categoryId ?? ''} disabled={blocked || busy || !!consolidated} onChange={event => { if (event.target.value) void run(() => correctStatementCategory(workspaceId, documentId, category.key, event.target.value)) }}>
              <option value="">{category.isProposal && !consolidated ? `${category.categoryName} (proposta, não criada)` : category.categoryName}</option>{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select></label></>}
        </article> })}</div></>}
    </>}
    <p className="meta">Analisar mantém a revisão sem escrita financeira. Processar consolida automaticamente apenas quando análise, categorias e conciliação forem confiáveis. Propostas são criadas na mesma transação da fatura; nenhum pagamento é gerado.</p>
    {consolidated && <p className="meta">A composição oficial preserva correções manuais correspondentes de versões anteriores, mesmo quando a análise sugere outra categoria.</p>}
  </section>
}
