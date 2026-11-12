import { getClassificationReview, type ClassificationReview, type ReviewItem as ClassificationItem } from '../../api/pluggyAutomation'
import { listReview, type ReviewItem as ImportItem, type ReviewPage } from '../../api/pluggyIntegration'
import { reviewReason } from '../../pluggyFormat'

// Fila "Requer sua decisão": reúne, só para leitura, o que os fluxos existentes já encaminham para decisão humana — movimentos importados em revisão (Contas → Integração bancária)
// e classificações importadas sem categoria segura (Automação → Revisar classificação). Motivo, impacto e alternativas vêm do diagnóstico de cada fonte; nada é inferido aqui.
// Itens ainda em processamento automático (ex.: "ainda não avaliados") não entram. Uma decisão só é agrupada com outra quando existe identidade comum comprovada:
// as duas APIs não compartilham identificador de movimento, então valor e descrição parecidos NUNCA unem itens.

export type DecisionSource = 'import' | 'classification'
export type DecisionImpact = 'out-of-totals' | 'indeterminate' | 'accounted-until-decision' | 'accounted-category-pending'

export interface DecisionItem {
  key: string; source: DecisionSource; recordId: string; typeLabel: string; description: string; account: string | null; date: string | null
  amount: string | null; direction: 'Income' | 'Expense' | null; reason: string; impact: DecisionImpact; impactText: string
}
export interface DecisionQueue {
  items: DecisionItem[]
  /** Total real nas fontes disponíveis (pode ser maior que `items`, que traz só a primeira página de cada fonte). */
  total: number
  /** Quantas pendências estão fora dos totais até a decisão (só as de fonte confirmada). */
  outOfTotals: number
  failedSources: DecisionSource[]
}

export const PAGE_SIZE = 25

export const impactText: Record<DecisionImpact, string> = {
  'out-of-totals': 'Fora dos totais até a revisão',
  indeterminate: 'Impacto ainda indeterminado',
  'accounted-until-decision': 'Já contabilizado · continua nos totais até a decisão',
  'accounted-category-pending': 'Já contabilizado · categoria pendente',
}
const impactRank: Record<DecisionImpact, number> = { 'out-of-totals': 0, indeterminate: 1, 'accounted-until-decision': 2, 'accounted-category-pending': 3 }
const importTypes = { Evidence: 'Movimento importado', Link: 'Vínculo entre movimentos', Refund: 'Possível devolução' } as const

function fromImport(item: ImportItem): DecisionItem {
  const impact: DecisionImpact = item.accounted === undefined ? 'indeterminate' : item.accounted ? 'accounted-until-decision' : 'out-of-totals'
  return {
    key: `import:${item.postingId}`, source: 'import', recordId: item.postingId, typeLabel: importTypes[item.reviewKind ?? 'Evidence'], description: item.description,
    account: item.linkDisplayName, date: item.date, amount: item.amount, direction: item.direction, reason: reviewReason(item.reason), impact, impactText: impactText[impact],
  }
}

function fromClassification(item: ClassificationItem): DecisionItem {
  const counted = item.action === 'Transaction' || item.action === 'CardPurchase'
  const impact: DecisionImpact = counted ? 'accounted-category-pending' : 'indeterminate'
  const where = item.action === 'CardPurchase' ? `Cartão${item.cardName ? ` ${item.cardName}` : ''}` : item.accountName
  return {
    key: `classification:${item.action}:${item.recordId}`, source: 'classification', recordId: item.recordId, typeLabel: 'Classificação', description: item.description, account: where,
    date: item.date, amount: item.amount, direction: 'Expense', reason: 'Sem categoria segura', impact, impactText: impactText[impact],
  }
}

/** Primeiro o que afeta os totais; dentro do mesmo grupo, o mais recente; o desempate final é a chave, para a ordem ser sempre a mesma. */
const compare = (a: DecisionItem, b: DecisionItem) =>
  impactRank[a.impact] - impactRank[b.impact] || (b.date ?? '').localeCompare(a.date ?? '') || a.key.localeCompare(b.key)

export function buildDecisionQueue({ review, classification }: { review: ReviewPage | null; classification: ClassificationReview | null }): DecisionQueue {
  const items = new Map<string, DecisionItem>()
  for (const item of review?.items ?? []) items.set(`import:${item.postingId}`, fromImport(item))
  for (const item of classification?.items ?? []) { const mapped = fromClassification(item); items.set(mapped.key, mapped) }
  const sorted = [...items.values()].sort(compare)
  const failedSources: DecisionSource[] = [...(review ? [] : ['import' as const]), ...(classification ? [] : ['classification' as const])]
  return {
    items: sorted, total: (review?.total ?? 0) + (classification?.total ?? 0),
    outOfTotals: sorted.filter(i => i.impact === 'out-of-totals').length, failedSources,
  }
}

/** Consulta somente leitura (GET). Uma fonte que falha não esconde a outra; só quando as duas falham a fila inteira falha. */
export async function loadDecisionQueue(workspaceId: string, signal?: AbortSignal): Promise<DecisionQueue> {
  const [review, classification] = await Promise.allSettled([
    listReview(workspaceId, 1, signal),
    getClassificationReview(workspaceId, { kind: 'category', page: 1, pageSize: PAGE_SIZE }, signal),
  ])
  if (review.status === 'rejected' && classification.status === 'rejected') throw review.reason
  if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
  return buildDecisionQueue({ review: review.status === 'fulfilled' ? review.value : null, classification: classification.status === 'fulfilled' ? classification.value : null })
}

export const sourceLabels: Record<DecisionSource, string> = { import: 'movimentos importados em revisão', classification: 'classificações pendentes' }
