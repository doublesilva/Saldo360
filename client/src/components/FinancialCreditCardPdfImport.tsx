import { useRef, useState } from 'react'
import type { FinancialCategory } from '../api/categories'
import { confirmPdf, preReconcilePdf, previewPdf, type PdfConfirmationResult, type PdfImportOptions, type PdfPreReconciliation, type PdfPreReconciliationItem, type PdfPreview } from '../api/creditCardImports'
import { formatBrlDecimal } from '../money'

const firstError = (errors: Record<string, string[]>) => Object.values(errors).flat()[0] ?? 'Verifique os dados informados.'
const errorMessage = (error: unknown) => error instanceof Error ? error.message : 'Erro desconhecido.'
const money = (value: string | null | undefined) => value == null ? 'Não identificado' : formatBrlDecimal(value)
const purchaseTypes = new Set(['Purchase', 'InternationalPurchase'])

export function FinancialCreditCardPdfImport({ workspaceId, cardId, categories, onChanged }: { workspaceId: string; cardId: string; categories: FinancialCategory[]; onChanged?: () => void }) {
  const version = useRef(0)
  const [file, setFile] = useState<File | null>(null)
  const [competence, setCompetence] = useState('')
  const [issuerProfile, setIssuerProfile] = useState('')
  const [password, setPassword] = useState('')
  const [preview, setPreview] = useState<PdfPreview | null>(null)
  const [report, setReport] = useState<PdfPreReconciliation | null>(null)
  const [result, setResult] = useState<PdfConfirmationResult | null>(null)
  const [categoryByComponent, setCategoryByComponent] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState<'preview' | 'reconcile' | 'confirm' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [conflict, setConflict] = useState(false)

  const options = (): PdfImportOptions => ({ competence: competence || undefined, issuerProfile: issuerProfile || undefined, password: password || undefined })
  const clearAnalysis = () => { version.current += 1; setBusy(null); setPreview(null); setReport(null); setResult(null); setCategoryByComponent({}); setError(null); setConflict(false) }
  const changeFile = (next: File | null) => { clearAnalysis(); setFile(next); setPassword('') }
  const run = async <T,>(stage: 'preview' | 'reconcile' | 'confirm', operation: (signal: AbortSignal) => Promise<{ ok: true; value: T; status: number } | { ok: false; errors: Record<string, string[]>; status: number }>, success: (value: T) => void) => {
    const current = ++version.current; const controller = new AbortController(); setBusy(stage); setError(null); setConflict(false)
    try { const response = await operation(controller.signal); if (current !== version.current) return; if (response.ok) success(response.value); else { setConflict(response.status === 409); setError(firstError(response.errors)) } }
    catch (failure) { if (current === version.current) setError(errorMessage(failure)) }
    finally { if (current === version.current) setBusy(null) }
  }
  const analyze = () => { if (!file) return; setReport(null); setResult(null); void run('preview', (signal) => previewPdf(workspaceId, cardId, file, options(), signal), setPreview) }
  const reconcile = () => { if (!file) return; setResult(null); void run('reconcile', (signal) => preReconcilePdf(workspaceId, cardId, file, options(), signal), (value) => { setReport(value); setCategoryByComponent({}) }) }
  const createPurchases = report?.items.filter((item) => item.classification === 'Create' && purchaseTypes.has(item.type)) ?? []
  const blocking = report?.items.filter((item) => item.includedInCurrentTotal && ['NeedsReview', 'Conflict', 'Unsupported'].includes(item.classification)) ?? []
  const missingCategories = createPurchases.filter((item) => !categoryByComponent[item.componentId])
  const confirmDisabled = !report || report.reconciliation.status !== 'Reconciled' || blocking.length > 0 || missingCategories.length > 0 || busy !== null
  const confirm = () => {
    if (!file || !report || confirmDisabled) return
    const decisions = createPurchases.map((item) => ({ componentId: item.componentId, categoryId: categoryByComponent[item.componentId] }))
    void run('confirm', async (signal) => { try { return await confirmPdf(workspaceId, cardId, file, options(), report.documentKey, decisions, signal) } finally { setPassword('') } }, (value) => { setResult(value); onChanged?.() })
  }

  return <section className="pdf-import-flow">
    <p className="notice">O PDF só altera dados após revisão completa e confirmação explícita.</p>
    <div className="form-row">
      <label>Arquivo PDF<input type="file" accept=".pdf,application/pdf" onChange={(event) => changeFile(event.target.files?.[0] ?? null)} /></label>
      <label>Competência opcional<input type="month" value={competence} onChange={(event) => { clearAnalysis(); setCompetence(event.target.value) }} /></label>
      <label>Perfil do emissor opcional<input value={issuerProfile} onChange={(event) => { clearAnalysis(); setIssuerProfile(event.target.value) }} placeholder="Detecção automática" /></label>
      <label>Senha do PDF opcional<input type="password" autoComplete="off" value={password} onChange={(event) => { clearAnalysis(); setPassword(event.target.value) }} /></label>
    </div>
    <div className="form__actions">
      <button type="button" className="button--primary" disabled={!file || busy !== null} onClick={analyze}>{busy === 'preview' ? 'Analisando…' : '1. Analisar PDF'}</button>
      {preview && <button type="button" disabled={busy !== null} onClick={reconcile}>{busy === 'reconcile' ? 'Comparando…' : '2. Comparar com Saldo360'}</button>}
      {(preview || report || result) && <button type="button" onClick={() => { clearAnalysis(); setPassword('') }}>Abandonar análise</button>}
    </div>
    {error && <p className="status status--down" role="alert">{error}</p>}
    {conflict && <button type="button" onClick={reconcile}>Reanalisar documento</button>}
    {preview && <PdfPreviewPanel preview={preview} />}
    {report && <PdfReconciliationPanel report={report} categories={categories} categoryByComponent={categoryByComponent} onCategory={(componentId, categoryId) => { setCategoryByComponent((current) => ({ ...current, [componentId]: categoryId })); setResult(null) }} />}
    {report && !result && <div className="pdf-import-confirm"><h5>Confirmação</h5>{blocking.length > 0 && <p className="status status--down">Resolva {blocking.length} componente(s) financeiro(s) bloqueante(s) antes de confirmar.</p>}{missingCategories.length > 0 && <p className="status status--down">Selecione categoria para {missingCategories.length} compra(s) nova(s).</p>}<button type="button" className="button--primary" disabled={confirmDisabled} onClick={confirm}>{busy === 'confirm' ? 'Confirmando…' : '3. Confirmar importação'}</button></div>}
    {result && <PdfResult result={result} />}
  </section>
}

function PdfPreviewPanel({ preview }: { preview: PdfPreview }) {
  return <section><h5>Prévia do documento</h5><p>{preview.pageCount} página(s) · parser {preview.parser} · segurança {preview.security?.status ?? 'não informada'}</p><dl><Fact label="Emissor" value={preview.statement.issuer} /><Fact label="Cartão" value={preview.statement.cardLastDigits ? `•••• ${preview.statement.cardLastDigits}` : null} /><Fact label="Identificação" value={preview.cardResolution?.status ?? 'InsufficientEvidence'} /><Fact label="Competência" value={preview.statement.competence} /><Fact label="Fechamento" value={preview.statement.closingDate} /><Fact label="Vencimento" value={preview.statement.dueDate} /><Fact label="Total oficial" value={money(preview.statement.totalAmount)} /></dl>{preview.cardResolution?.status === 'Ambiguous' && <p className="notice">Mais de um cartão corresponde à fatura. Selecione explicitamente o cartão correto.</p>}{preview.cardResolution && preview.cardResolution.status !== 'Matched' && preview.cardResolution.status !== 'Ambiguous' && <p className="notice">A identidade do cartão não pôde ser validada automaticamente; o cadastro não será alterado.</p>}{preview.reconciliation && <p className="notice">Conferência interna: {preview.reconciliation.status} · composição {money(preview.reconciliation.componentsTotal)} · diferença {money(preview.reconciliation.difference)}</p>}{preview.warnings.map((warning) => <p className="notice" key={warning}>{warning}</p>)}</section>
}

export function PdfReconciliationPanel({ report, categories, categoryByComponent, onCategory }: { report: PdfPreReconciliation; categories: FinancialCategory[]; categoryByComponent: Record<string, string>; onCategory: (componentId: string, categoryId: string) => void }) {
  const current = report.items.filter((item) => item.includedInCurrentTotal); const future = report.items.filter((item) => !item.includedInCurrentTotal)
  return <section><h5>Pré-conciliação</h5><p className="meta">Documento {report.documentKey.slice(0, 18)}… · destino {report.destination.creditCard} · fatura {report.destination.invoice}</p><dl><Fact label="Criar" value={String(report.counts.create)} /><Fact label="Já existentes" value={String(report.counts.alreadyExists)} /><Fact label="Revisão" value={String(report.counts.needsReview)} /><Fact label="Conflitos" value={String(report.counts.conflict)} /><Fact label="Não suportados" value={String(report.counts.unsupported)} /><Fact label="Informativos" value={String(report.counts.informational)} /></dl>{report.destination.notes.map((note) => <p className="notice" key={note}>{note}</p>)}<PdfItems title="Componentes da fatura atual" items={current} categories={categories} categoryByComponent={categoryByComponent} onCategory={onCategory} /><PdfItems title="Parcelas futuras e informações" items={future} categories={categories} categoryByComponent={categoryByComponent} onCategory={onCategory} />{report.limitations.map((limitation) => <p className="meta" key={limitation}>{limitation}</p>)}</section>
}

function PdfItems({ title, items, categories, categoryByComponent, onCategory }: { title: string; items: PdfPreReconciliationItem[]; categories: FinancialCategory[]; categoryByComponent: Record<string, string>; onCategory: (componentId: string, categoryId: string) => void }) {
  return <section><h6>{title}</h6>{items.length === 0 ? <p className="meta">Nenhum componente.</p> : <div className="import-table">{items.map((item) => { const needsCategory = item.classification === 'Create' && purchaseTypes.has(item.type); return <article className={`import-row pdf-classification--${item.classification}`} key={item.componentId}><span>Página {item.page} · {item.type} · {item.classification}</span><strong>{money(item.amount)}</strong><span>{item.transactionDate ?? 'Sem data'} · {item.description}{item.installmentNumber ? ` · ${item.installmentNumber}/${item.installmentCount}` : ''}</span>{needsCategory && <label>Categoria<select aria-label={`Categoria de ${item.description}`} value={categoryByComponent[item.componentId] ?? ''} onChange={(event) => onCategory(item.componentId, event.target.value)}><option value="">Selecione</option>{categories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}</select></label>}<small>{item.reasons.join(' · ') || (item.matchedEntityType ? `Vínculo: ${item.matchedEntityType}` : 'Sem observações')}</small></article> })}</div>}</section>
}

export function PdfResult({ result }: { result: PdfConfirmationResult }) {
  const replayed = result.createdPurchases + result.createdHistoricalInstallments + result.createdAdjustments === 0 && result.alreadyExisting > 0
  return <section role="status"><h5>Importação concluída</h5>{replayed && <p className="notice">Este documento já havia sido importado. Nenhum registro foi duplicado.</p>}<dl><Fact label="Compras criadas" value={String(result.createdPurchases)} /><Fact label="Parcelas históricas" value={String(result.createdHistoricalInstallments)} /><Fact label="Ajustes criados" value={String(result.createdAdjustments)} /><Fact label="Já existentes" value={String(result.alreadyExisting)} /><Fact label="Informativos" value={String(result.informational)} /><Fact label="Total oficial" value={money(result.finalReconciliation.officialTotal)} /><Fact label="Total persistido" value={money(result.finalReconciliation.persistedTotal)} /><Fact label="Diferença" value={money(result.finalReconciliation.difference)} /></dl><p className="meta">Operação {result.importId} · {result.status}</p></section>
}

function Fact({ label, value }: { label: string; value: string | null | undefined }) { return <div><dt>{label}</dt><dd>{value || 'Não identificado'}</dd></div> }
