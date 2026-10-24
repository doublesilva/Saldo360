import { useEffect, useRef, useState } from 'react'
import { analyzeStatement, correctStatementCategory, getStatementAnalysis, type StatementAnalysisResult } from '../api/financialDocuments'
import type { FinancialCategory } from '../api/categories'
import { formatBrlDecimal } from '../money'

export function InboxStatementAnalysisPanel({ workspaceId, documentId, cardId, password, categories, blocked, onBusy, onAnalyzed }: {
  workspaceId: string; documentId: string; cardId: string; password: string; categories: FinancialCategory[]; blocked: boolean; onBusy: (busy: boolean) => void; onAnalyzed: () => void
}) {
  const [result, setResult] = useState<StatementAnalysisResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
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
  const extraction = analysis?.extraction?.primary
  return <section className="card" aria-label="Análise estruturada da fatura" aria-live="polite">
    <h4>Análise e categorias da fatura</h4>
    <button type="button" disabled={!cardId || blocked || busy} onClick={() => void run(() => analyzeStatement(workspaceId, documentId, cardId, password || undefined))}>{busy ? 'Analisando fatura…' : 'Analisar fatura armazenada'}</button>
    {!cardId && <p>Selecione explicitamente o cartão para analisar sem novo upload.</p>}
    {error && <p role="alert">{error}</p>}
    {analysis && <><p>Resultado: {analysis.status} · modo {analysis.extraction?.mode ?? 'Indisponível'} · tentativas {analysis.extraction?.attempts ?? 0}</p>
      {analysis.status !== 'Reliable' && <p>Há divergências ou evidências pendentes. Revisão necessária; nenhum lançamento foi importado.</p>}
      {analysis.status !== 'Reliable' && analysis.extraction?.comparison && <details><summary>Diferenças entre parser e agente</summary><p>Componentes somente no parser: {analysis.extraction.comparison.itemsOnlyInParser.join(' · ') || 'Nenhum'}</p><p>Componentes somente no agente: {analysis.extraction.comparison.itemsOnlyInAgent.join(' · ') || 'Nenhum'}</p></details>}
      {extraction && <><p>Total oficial: {extraction.reconciliation.officialTotal ? formatBrlDecimal(extraction.reconciliation.officialTotal) : 'Indisponível'} · Componentes: {extraction.reconciliation.componentsTotal ? formatBrlDecimal(extraction.reconciliation.componentsTotal) : 'Indisponível'} · Diferença: {extraction.reconciliation.difference ? formatBrlDecimal(extraction.reconciliation.difference) : 'Indisponível'}</p>
        <div className="import-rows">{extraction.items.map((item, index) => { const category = analysis.categories.find(c => c.itemIndex === index); return <article className="import-row" key={index}>
          <strong>{item.normalizedDescription ?? item.originalDescription}</strong>{item.normalizedDescription && item.normalizedDescription !== item.originalDescription && <small>Original: {item.originalDescription}</small>}<span>{item.transactionDate ?? 'Data não identificada'} · {formatBrlDecimal(item.amount)} · {item.nature} · {item.type}</span>
          <small>{item.section} · {item.includedInCurrentTotal ? 'Integra a fatura atual' : 'Informativo / futuro'}</small>
          {category && <><p>{category.isProposal ? 'Proposta de categoria' : 'Categoria'}: {category.categoryName} · {category.confidence} · {category.source}</p>
            <label>Corrigir categoria de {item.originalDescription}<select value={category.categoryId ?? ''} disabled={blocked || busy} onChange={event => { if (event.target.value) void run(() => correctStatementCategory(workspaceId, documentId, category.key, event.target.value)) }}>
              <option value="">{category.isProposal ? `${category.categoryName} (proposta, não criada)` : category.categoryName}</option>{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select></label></>}
        </article> })}</div></>}
    </>}
    <p className="meta">As categorias seguras já estão selecionadas. Propostas não criam categorias; não há importação financeira nesta análise.</p>
  </section>
}
