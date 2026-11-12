import { getClassificationReview, type ClassificationReview, type ReviewItem as ClassificationItem } from '../../api/pluggyAutomation'
import { listReview, type ReviewItem as ImportItem, type ReviewPage } from '../../api/pluggyIntegration'
import { reviewReason } from '../../pluggyFormat'

// Fila "Requer sua decisão": reúne, só para leitura, o que os fluxos existentes já encaminham para decisão humana — movimentos importados em revisão (Contas → Integração bancária)
// e classificações importadas sem categoria segura (Automação → Revisar classificação). Motivo, impacto e alternativas vêm do diagnóstico de cada fonte; nada é inferido aqui.
// Itens ainda em processamento automático (ex.: "ainda não avaliados") não entram. Uma decisão só é agrupada com outra quando existe identidade comum comprovada:
// as duas APIs não compartilham identificador de movimento, então valor e descrição parecidos NUNCA unem itens.
//
// Paginação: as duas fontes ordenam por data crescente e não conhecem a prioridade da fila. Por isso a fila lê TODAS as páginas de cada fonte (em lotes de 100, só GET), ordena
// globalmente aqui e só então mostra, em lotes, o que a pessoa pediu. Assim um item fora dos totais nunca fica escondido atrás de itens de menor prioridade.

export type DecisionSource = 'import' | 'classification'
export type DecisionImpact = 'out-of-totals' | 'indeterminate' | 'accounted-until-decision' | 'accounted-category-pending'

export interface DecisionItem {
  key: string; source: DecisionSource; recordId: string; typeLabel: string; description: string; account: string | null; date: string | null
  amount: string | null; direction: 'Income' | 'Expense' | null; reason: string; impact: DecisionImpact; impactText: string
  /** Página (de `SOURCE_PAGE_SIZE`) em que a fonte devolveu o item; só uma dica para abrir o detalhe sem varrer a fonte inteira. */
  page: number
}
export interface DecisionQueue {
  items: DecisionItem[]
  /** Soma dos totais das fontes que responderam. Só é a contagem completa quando `totalComplete`. */
  total: number
  totalComplete: boolean
  /** Quantas pendências estão fora dos totais até a decisão. */
  outOfTotals: number
  failedSources: DecisionSource[]
}

/** Máximo aceito pelas duas APIs. */
export const SOURCE_PAGE_SIZE = 100
/** Quantos itens a fila mostra por vez. */
export const BATCH_SIZE = 25
const MAX_PAGES = 200

export const impactText: Record<DecisionImpact, string> = {
  'out-of-totals': 'Fora dos totais até a revisão',
  indeterminate: 'Impacto ainda indeterminado',
  'accounted-until-decision': 'Já contabilizado · continua nos totais até a decisão',
  'accounted-category-pending': 'Já contabilizado · categoria pendente',
}
const importTypes = { Evidence: 'Movimento importado', Link: 'Vínculo entre movimentos', Refund: 'Possível devolução' } as const

function fromImport(item: ImportItem, page: number): DecisionItem {
  const impact: DecisionImpact = item.accounted === undefined ? 'indeterminate' : item.accounted ? 'accounted-until-decision' : 'out-of-totals'
  return {
    key: `import:${item.postingId}`, source: 'import', recordId: item.postingId, typeLabel: importTypes[item.reviewKind ?? 'Evidence'], description: item.description,
    account: item.linkDisplayName, date: item.date, amount: item.amount, direction: item.direction, reason: reviewReason(item.reason), impact, impactText: impactText[impact], page,
  }
}

function fromClassification(item: ClassificationItem, page: number): DecisionItem {
  const counted = item.action === 'Transaction' || item.action === 'CardPurchase'
  const impact: DecisionImpact = counted ? 'accounted-category-pending' : 'indeterminate'
  const where = item.action === 'CardPurchase' ? `Cartão${item.cardName ? ` ${item.cardName}` : ''}` : item.accountName
  return {
    key: `classification:${item.action}:${item.recordId}`, source: 'classification', recordId: item.recordId, typeLabel: 'Classificação', description: item.description, account: where,
    date: item.date, amount: item.amount, direction: 'Expense', reason: 'Sem categoria segura', impact, impactText: impactText[impact], page,
  }
}

/** Fora dos totais primeiro; depois todos os demais, do mais recente ao mais antigo; o desempate final é a chave (fonte + identificador), para a ordem ser sempre a mesma. */
export const compareItems = (a: DecisionItem, b: DecisionItem) =>
  Number(b.impact === 'out-of-totals') - Number(a.impact === 'out-of-totals') || (b.date ?? '').localeCompare(a.date ?? '') || a.key.localeCompare(b.key)

interface SourcePage { items: DecisionItem[]; total: number; pageSize: number; accountedInReview?: number }
export const fetchImportPage = async (workspaceId: string, page: number, pageSize: number, signal?: AbortSignal): Promise<SourcePage> => {
  const result = await listReview(workspaceId, page, signal, pageSize)
  return { items: result.items.map(item => fromImport(item, page)), total: result.total, pageSize: result.pageSize, accountedInReview: result.accountedInReview }
}
export const fetchClassificationPage = async (workspaceId: string, page: number, pageSize: number, signal?: AbortSignal): Promise<SourcePage> => {
  const result = await getClassificationReview(workspaceId, { kind: 'category', page, pageSize }, signal)
  return { items: result.items.map(item => fromClassification(item, page)), total: result.total, pageSize: result.pageSize }
}

/** O que já foi lido de uma fonte. `failed` = a leitura parou numa página; `nextPage` é de onde uma nova tentativa continua. */
export interface SourceState { items: DecisionItem[]; total: number; nextPage: number; complete: boolean; failed: boolean; accountedInReview?: number }

/** Lê as páginas de uma fonte (só GET). Itens repetidos entre tentativas ou páginas são ignorados pela chave. Uma falha preserva o que já foi lido. */
export async function loadSource(fetchPage: (page: number) => Promise<SourcePage>, previous: SourceState | null, signal?: AbortSignal): Promise<SourceState> {
  const items = new Map((previous?.items ?? []).map(item => [item.key, item]))
  let page = previous?.nextPage ?? 1, total = previous?.total ?? 0, accountedInReview = previous?.accountedInReview
  for (; page <= MAX_PAGES; page++) {
    let result: SourcePage
    try { result = await fetchPage(page) }
    catch (error) {
      if (signal?.aborted) throw error
      return { items: [...items.values()], total, nextPage: page, complete: false, failed: true, accountedInReview }
    }
    total = result.total; accountedInReview = result.accountedInReview ?? accountedInReview
    for (const item of result.items) items.set(item.key, item)
    if (result.items.length === 0 || items.size >= total) { page++; break }
  }
  return { items: [...items.values()], total, nextPage: page, complete: true, failed: false, accountedInReview }
}

/** Junta as fontes lidas numa fila única e ordenada. Fonte nula = nunca respondeu (cobertura parcial; seu total é desconhecido). */
export function buildDecisionQueue({ review, classification }: { review: SourceState | null; classification: SourceState | null }): DecisionQueue {
  const items = new Map<string, DecisionItem>()
  for (const source of [review, classification]) for (const item of source?.items ?? []) items.set(item.key, item)
  const sorted = [...items.values()].sort(compareItems)
  const failedSources: DecisionSource[] = [...(review === null || review.failed ? ['import' as const] : []), ...(classification === null || classification.failed ? ['classification' as const] : [])]
  return {
    items: sorted, total: (review?.total ?? 0) + (classification?.total ?? 0), totalComplete: review !== null && classification !== null,
    outOfTotals: sorted.filter(item => item.impact === 'out-of-totals').length, failedSources,
  }
}

/** Resumo barato para a Visão Geral: só o total de cada fonte (página de 1 item) e, na revisão, quantos ainda estão fora dos totais (contagem do servidor). */
export function summarizeDecisionQueue({ review, classification }: { review: ReviewPage | null; classification: ClassificationReview | null }): DecisionQueue {
  const outOfTotals = review ? Math.max(0, review.total - (review.accountedInReview ?? 0)) : 0
  return {
    items: [], total: (review?.total ?? 0) + (classification?.total ?? 0), totalComplete: review !== null && classification !== null, outOfTotals,
    failedSources: [...(review ? [] : ['import' as const]), ...(classification ? [] : ['classification' as const])],
  }
}

/** Consulta somente leitura (GET). Uma fonte que falha não esconde a outra; só quando as duas falham o resumo inteiro falha. */
export async function loadDecisionSummary(workspaceId: string, signal?: AbortSignal): Promise<DecisionQueue> {
  const [review, classification] = await Promise.allSettled([
    listReview(workspaceId, 1, signal, 1),
    getClassificationReview(workspaceId, { kind: 'category', page: 1, pageSize: 1 }, signal),
  ])
  if (review.status === 'rejected' && classification.status === 'rejected') throw review.reason
  if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
  return summarizeDecisionQueue({ review: review.status === 'fulfilled' ? review.value : null, classification: classification.status === 'fulfilled' ? classification.value : null })
}

/**
 * Acha a página que contém um item. Começa pela dica (a página em que a fila o leu); se ele não estiver lá (a lista mudou), varre as demais antes de concluir
 * que já foi resolvido. Sem a varredura, um item de página tardia pareceria "resolvido" ao abrir o detalhe.
 */
export async function locatePage<P extends { items: unknown[]; total: number; pageSize: number }>(fetchPage: (page: number) => Promise<P>, has: (page: P) => boolean, hint: number): Promise<P> {
  const first = await fetchPage(hint)
  if (has(first)) return first
  const pages = Math.max(1, Math.ceil(first.total / first.pageSize))
  for (let page = 1; page <= pages; page++) {
    if (page === hint) continue
    const candidate = await fetchPage(page)
    if (has(candidate)) return candidate
  }
  return first
}

export const sourceLabels: Record<DecisionSource, string> = { import: 'movimentos importados em revisão', classification: 'classificações pendentes' }
