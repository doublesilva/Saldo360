import type { StatementFinancialClassification, StatementFinancialReconciliation } from '../api/financialDocuments'
import { formatBrlDecimal } from '../money'

const kinds: Record<string, string> = { FinancialOperation: 'Operação financeira', Fee: 'Encargo', Interest: 'Juros', Tax: 'Imposto', Credit: 'Crédito', Refund: 'Estorno', Payment: 'Pagamento', PreviousBalance: 'Saldo anterior', FinancedBalance: 'Saldo financiado', PrincipalDetail: 'Principal (detalhamento)', InterestDetail: 'Juros (detalhamento)', Rate: 'Taxa informativa', Limit: 'Limite', Offer: 'Oferta', Simulation: 'Simulação', Subtotal: 'Subtotal', CurrentTotal: 'Total atual', NextInvoiceTotal: 'Próxima fatura', RemainingFutureTotal: 'Demais faturas', FutureTotal: 'Total futuro', Unknown: 'Não classificado' }

function Reconciliation({ title, value }: { title: string; value: StatementFinancialReconciliation }) {
  return <section aria-label={title}><h5>{title}</h5><p>{value.status} · Referência oficial: {value.officialTotal === null ? 'Não apresentada' : formatBrlDecimal(value.officialTotal)} · Componentes: {formatBrlDecimal(value.componentsTotal)} · Diferença: {value.difference === null ? 'Não calculável' : formatBrlDecimal(value.difference)}</p></section>
}

export function StatementFinancialClassificationPanel({ value }: { value: StatementFinancialClassification }) {
  const purchases = value.document.purchases.items
  return <section aria-label="Classificação financeira integral">
    <h4>Grupos financeiros da fatura</h4>
    <p>Somente conferência documental. Nenhum lançamento ou obrigação será criado. Conciliação aritmética não comprova cobertura nem identidade.</p>
    <Reconciliation title="Conciliação da fatura atual" value={value.currentInvoice} />
    <Reconciliation title="Conciliação da próxima fatura" value={value.nextInvoice} />
    <p>Cobertura: {value.coverageStatus}. Datas desconhecidas permanecem desconhecidas.</p>
    {(['CurrentInvoice', 'FutureInstallments'] as const).map(scope => <details key={scope} open><summary>{scope === 'CurrentInvoice' ? 'Compras e parcelas atuais' : 'Parcelas de compras futuras'} ({purchases.filter(p => p.section === scope).length})</summary>
      {purchases.filter(p => p.section === scope).map((p, i) => <article key={i}><small>Entrada {purchases.indexOf(p) + 1}</small><strong>{p.originalDescription}</strong><p>{formatBrlDecimal(p.amount)} · {p.transactionDate ?? 'Data de compra não comprovada'} · página {p.page}</p><blockquote>{p.evidence}</blockquote></article>)}
    </details>)}
    {([['CurrentInvoice', 'Outros componentes atuais'], ['NextInvoice', 'Operações financeiras futuras contratadas'], ['Control', 'Totais e subtotais — não são lançamentos'], ['Information', 'Informações e detalhamentos — sem obrigação adicional'], ['Offer', 'Ofertas e simulações — não contratadas']] as const).map(([scope, label]) => <details key={scope} open><summary>{label} ({value.document.components.filter(c => c.scope === scope).length})</summary>
      {value.document.components.filter(c => c.scope === scope).map((c, i) => <article key={i}><small>Entrada {purchases.length + value.document.components.indexOf(c) + 1}</small><strong>{c.description}</strong><p>{kinds[c.kind] ?? c.kind} · {c.nature === 'Credit' ? 'Crédito −' : ''}{c.unit === 'BRL' ? formatBrlDecimal(c.amount) : `${c.amount} ${c.unit === 'Percent' ? '%' : '(moeda estrangeira)'}`} · página {c.page} · {c.confidence}</p><blockquote>{c.evidence}</blockquote><small>Contexto, página {c.contextPage}: {c.contextEvidence}</small></article>)}
    </details>)}
    {!!value.duplicates.length && <section aria-label="Possíveis duplicidades"><h5>Possíveis duplicidades — nenhuma linha removida</h5>{value.duplicates.map((d, i) => <p key={i}>Entrada {d.itemIndex + 1}{d.otherItemIndex === null ? ' / obrigação existente' : ` / entrada ${d.otherItemIndex + 1}`}: {d.code}</p>)}</section>}
    {!!value.diagnostics.length && <details><summary>Diagnósticos documentais</summary>{value.diagnostics.map((d, i) => <p key={i}>{d.code}{d.page ? ` · página ${d.page}` : ''}</p>)}</details>}
  </section>
}
