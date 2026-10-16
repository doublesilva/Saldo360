import { useEffect, useMemo, useState } from 'react'
import { createReconciliation, listReconciliations, removeReconciliation, type DebtInstallment, type DebtReconciliation, type FinancialDebt } from '../api/debts'
import { listPlanned, type PlannedTransaction } from '../api/planned'
import { formatBrlDecimal } from '../money'

type Props = { familyId: string; debt: FinancialDebt; installment: DebtInstallment; onChanged?: () => void; onNavigateToAgenda?: (competence: string) => void; onNavigateToBudgets?: (competence: string) => void; onNavigateToPlanning?: () => void }

export function FinancialDebtReconciliation({ familyId, debt, installment, onChanged, onNavigateToAgenda, onNavigateToBudgets, onNavigateToPlanning }: Props) {
  const [planned, setPlanned] = useState<PlannedTransaction[]>([])
  const [links, setLinks] = useState<DebtReconciliation[]>([])
  const [selectedId, setSelectedId] = useState('')
  const [review, setReview] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const activeLink = useMemo(() => links.find((link) => link.debtInstallmentId === installment.id && link.removedAt === null) ?? null, [links, installment.id])
  const selected = planned.find((item) => item.id === selectedId) ?? null
  const load = async (signal: AbortSignal) => {
    const [page, loadedLinks] = await Promise.all([
      listPlanned(familyId, { from: '', to: '', accountId: debt.responsibleAccountId ?? '', categoryId: '', type: 'Expense', includeRealized: false, includeCancelled: false }, 1, signal, 100),
      listReconciliations(familyId, signal),
    ])
    setPlanned(page.items.filter((item) => item.status === 'Pending'))
    setLinks(loadedLinks)
  }
  useEffect(() => { const controller = new AbortController(); setError(null); setSelectedId(''); setReview(false); load(controller.signal).catch((failure) => { if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : 'Falha ao carregar previsões elegíveis.') }); return () => controller.abort() }, [familyId, debt.id, installment.id, debt.responsibleAccountId])
  const reconcile = async () => {
    if (!selected || busy) return
    setBusy(true); setError(null)
    try {
      const result = await createReconciliation(familyId, { debtInstallmentId: installment.id, plannedTransactionId: selected.id })
      if (!result) { setError('Conflito: parcela ou previsão já possui conciliação ativa.'); return }
      await load(new AbortController().signal); setSelectedId(''); setReview(false); onChanged?.()
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Falha ao conciliar. Verifique o estado da previsão.') } finally { setBusy(false) }
  }
  const remove = async () => {
    if (!activeLink || !window.confirm('Desfazer esta conciliação? Parcela e previsão poderão voltar a ser contabilizadas separadamente.')) return
    setBusy(true); setError(null)
    try { await removeReconciliation(familyId, activeLink.id); await load(new AbortController().signal); onChanged?.() } catch (failure) { setError(failure instanceof Error ? failure.message : 'Falha ao desfazer a conciliação.') } finally { setBusy(false) }
  }
  const competence = installment.dueDate.slice(0, 7)
  return <section className="debt-reconciliation" aria-labelledby={`reconciliation-${installment.id}`}>
    <h5 id={`reconciliation-${installment.id}`}>Conciliação da parcela {installment.installmentNumber}</h5>
    {error && <p className="status status--down" role="alert">{error}</p>}
    <div className="form__actions"><button type="button" onClick={() => onNavigateToAgenda?.(competence)}>Ver na Agenda</button>{installment.categoryId && <button type="button" onClick={() => onNavigateToBudgets?.(competence)}>Ver no Orçamento</button>}<button type="button" onClick={onNavigateToPlanning}>Ver no Planejamento</button></div>
    <div className="reconciliation-comparison"><article><h6>Parcela</h6><dl><div><dt>Dívida</dt><dd>{debt.name}</dd></div><div><dt>Vencimento</dt><dd>{installment.dueDate}</dd></div><div><dt>Valor nominal</dt><dd>{formatBrlDecimal(installment.scheduledAmount)}</dd></div><div><dt>Restante</dt><dd>{formatBrlDecimal(installment.remainingAmount)}</dd></div><div><dt>Conta</dt><dd>{debt.responsibleAccountId ?? 'Não informada'}</dd></div><div><dt>Categoria</dt><dd>{installment.categoryId ?? 'Não informada'}</dd></div></dl></article>{activeLink ? <article><h6>Vínculo ativo</h6><p>Previsão vinculada: {activeLink.plannedTransactionId}</p><p className="meta">A parcela permanece como obrigação; a previsão vinculada não acrescenta uma segunda saída.</p><button type="button" onClick={() => void remove()} disabled={busy}>{busy ? 'Atualizando…' : 'Desfazer conciliação'}</button></article> : <article><h6>Previsão</h6><label htmlFor={`planned-${installment.id}`}>Previsão de despesa</label><select id={`planned-${installment.id}`} value={selectedId} onChange={(event) => { setSelectedId(event.target.value); setReview(false) }} disabled={busy}><option value="">Selecionar previsão pendente</option>{planned.filter((item) => !links.some((link) => link.removedAt === null && link.plannedTransactionId === item.id)).map((item) => <option key={item.id} value={item.id}>{item.dueDate} · {item.description} · {formatBrlDecimal(item.amount)}</option>)}</select>{selected && <div className="reconciliation-selection"><strong>Previsão selecionada</strong><span>{selected.description} · {selected.dueDate} · {formatBrlDecimal(selected.amount)} · {selected.account.name}</span>{selected.amount !== installment.scheduledAmount && <p className="field-error" role="alert">Valor divergente. Ambos serão preservados; o valor contratual da parcela permanece a referência da obrigação.</p>}</div>}{selected && !review && <button type="button" onClick={() => setReview(true)} disabled={busy}>Revisar conciliação</button>}{review && selected && <div role="group" aria-label="Confirmação de conciliação"><p>Confirme o vínculo entre esta parcela e a previsão. Nenhum pagamento ou lançamento bancário será criado.</p><button type="button" className="button--primary" onClick={() => void reconcile()} disabled={busy}>{busy ? 'Conciliando…' : 'Confirmar conciliação'}</button><button type="button" onClick={() => setReview(false)} disabled={busy}>Voltar</button></div>}</article>}</div>
  </section>
}
