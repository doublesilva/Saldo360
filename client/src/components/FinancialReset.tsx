import { useEffect, useRef, useState } from 'react'
import { previewFinancialReset, resetFinancialWorkspace, retryResetCleanup, type ResetPreview, type ResetResult } from '../api/financialReset'

const labels: Record<string, string> = {
  accounts: 'Contas', categories: 'Categorias', transactions: 'Lançamentos', transfers: 'Transferências',
  plannedTransactions: 'Previsões', recurrences: 'Recorrências', recurrenceMaterializations: 'Materializações',
  budgets: 'Orçamentos mensais', budgetTargets: 'Metas por categoria', creditCards: 'Cartões',
  creditCardPurchases: 'Compras no cartão', creditCardInstallments: 'Parcelas do cartão', creditCardInvoices: 'Faturas',
  creditCardInvoicePayments: 'Pagamentos de faturas', creditCardInvoiceAdjustments: 'Ajustes de faturas',
  plannedInvoiceReconciliations: 'Conciliações de faturas', creditCardImportBatches: 'Lotes de importação',
  creditCardImportItems: 'Itens de importação', creditCardImportedInstallments: 'Parcelas importadas',
  debts: 'Dívidas', debtInstallments: 'Parcelas de dívidas', debtPayments: 'Pagamentos de dívidas',
  debtInstallmentReconciliations: 'Conciliações de dívidas', goals: 'Objetivos', goalAllocations: 'Alocações',
  planningScenarios: 'Cenários', planningMonthlyParameters: 'Parâmetros mensais', documentInboxItems: 'Documentos',
  serviceAccountBindings: 'Vínculos financeiros de serviços', emailCollectorCheckpoints: 'Checkpoints de coleta',
}

export function FinancialReset({ workspaceId, onReset }: { workspaceId: string; onReset: (result: ResetResult) => void }) {
  const [open, setOpen] = useState(false)
  const [preview, setPreview] = useState<ResetPreview | null>(null)
  const [confirmation, setConfirmation] = useState('')
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [revision, setRevision] = useState(0)
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    previewFinancialReset(workspaceId, controller.signal).then(value => { if (!controller.signal.aborted) { setPreview(value); setLoading(false) } })
      .catch((cause: unknown) => { if (!controller.signal.aborted) { setLoading(false); setError(cause instanceof Error ? cause.message : 'Falha na prévia.') } })
    return () => controller.abort()
  }, [open, workspaceId, revision])
  useEffect(() => { if (preview) input.current?.focus() }, [preview])
  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (busy || !preview || confirmation !== 'APAGAR ORÇAMENTO') return
    setBusy(true); setError(null)
    try { onReset(await resetFinancialWorkspace(workspaceId, confirmation)) }
    catch (cause: unknown) { setError(cause instanceof Error ? cause.message : 'Falha no reset.'); setPreview(null); setConfirmation('') }
    finally { setBusy(false) }
  }
  return <section aria-labelledby="reset-settings-title"><h3 id="reset-settings-title">Configurações</h3><h4>Zona de perigo</h4>
    <p>Seu usuário, sessão, vínculos sociais e contas de serviço pessoais serão preservados.</p>
    {!open && <button type="button" onClick={() => { setOpen(true); setPreview(null); setConfirmation(''); setError(null); setLoading(true) }}>Resetar orçamento</button>}
    {open && <form onSubmit={submit} aria-busy={busy || loading}>
      <p>Esta ação apagará permanentemente todos os dados financeiros deste orçamento.</p>
      <p>Ela não pode ser desfeita pela aplicação.</p>
      <p>Dados ativos serão removidos. Backups de infraestrutura podem seguir a política de retenção do ambiente.</p>
      {loading && <p role="status">Consultando impacto…</p>}
      {error && <p role="alert">{error}</p>}
      {error && !preview && <button type="button" disabled={busy} onClick={() => { setError(null); setLoading(true); setRevision(value => value + 1) }}>Recarregar prévia</button>}
      {preview && <><p>Este reset removerá:</p><dl>{Object.entries(preview.counts).map(([key, value]) => <div key={key}><dt>{labels[key] ?? key}</dt><dd>{value}</dd></div>)}</dl>
        {preview.pendingFileCleanup > 0 && <><p>Arquivos de reset anterior aguardando remoção: {preview.pendingFileCleanup}.</p><button type="button" disabled={busy} onClick={() => {
          setBusy(true); void retryResetCleanup(workspaceId).then(value => setPreview(old => old ? { ...old, ...value } : old))
            .catch(() => setError('Cleanup não concluído. A intenção permanece registrada.')).finally(() => setBusy(false))
        }}>Repetir cleanup pendente</button></>}
        <label>Digite APAGAR ORÇAMENTO<input ref={input} value={confirmation} onChange={event => setConfirmation(event.target.value)} disabled={busy} autoComplete="off" /></label></>}
      <button type="submit" disabled={!preview || busy || confirmation !== 'APAGAR ORÇAMENTO'}>{busy ? 'Reiniciando…' : 'Apagar dados financeiros'}</button>
      <button type="button" disabled={busy} onClick={() => { setOpen(false); setPreview(null); setConfirmation(''); setError(null) }}>Cancelar</button>
      {busy && <p role="status">Aguarde a confirmação do servidor.</p>}
    </form>}
  </section>
}
