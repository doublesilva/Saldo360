import { describe, expect, it, vi } from 'vitest'
import { buildDecisionQueue, loadSource, locatePage, summarizeDecisionQueue, type DecisionItem, type SourceState } from './queueModel'
import { classificationPage, decisionItem, importItem, reviewPage } from './queueFixtures'

/** Fonte fictícia com paginação real: devolve `items` em páginas de `size`, como as APIs (ordem de origem = data crescente). */
const pager = (items: DecisionItem[], size: number, fail: Set<number> = new Set()) => vi.fn(async (page: number) => {
  if (fail.has(page)) throw new Error(`falha na página ${page}`)
  return { items: items.slice((page - 1) * size, page * size).map(item => ({ ...item, page })), total: items.length, pageSize: size }
})
const many = (count: number, make: (index: number) => Partial<DecisionItem>) => Array.from({ length: count }, (_, index) => decisionItem({ ...make(index) }))
const day = (index: number) => `2026-10-${String((index % 28) + 1).padStart(2, '0')}`

describe('fila Requer sua decisão — modelo', () => {
  it('rotula o impacto só pelo que o diagnóstico sustenta (resumo e itens)', () => {
    expect(summarizeDecisionQueue({ review: { ...reviewPage([importItem()], 3), accountedInReview: 1 }, classification: classificationPage([], 2) })).toMatchObject({ total: 5, outOfTotals: 2, totalComplete: true, failedSources: [] })
    expect(summarizeDecisionQueue({ review: null, classification: classificationPage([], 2) })).toMatchObject({ total: 2, totalComplete: false, failedSources: ['import'] })
  })

  it('com mais de 25 itens em cada fonte, todas as páginas são lidas e o último item é alcançável', async () => {
    const review = many(130, index => ({ key: `import:r${index}`, recordId: `r${index}`, date: day(index) }))
    const classification = many(60, index => ({ key: `classification:c${index}`, source: 'classification', recordId: `c${index}`, date: day(index) }))
    const [a, b] = await Promise.all([loadSource(pager(review, 100), null), loadSource(pager(classification, 100), null)])
    const queue = buildDecisionQueue({ review: a, classification: b })
    expect(queue.items).toHaveLength(190)
    expect(queue.total).toBe(190)
    expect(queue.items.map(i => i.key)).toContain('import:r129')
    expect(new Set(queue.items.map(i => i.key)).size).toBe(190)
  })

  it('um item fora dos totais de página tardia aparece antes dos de menor prioridade, e a ordem é global, estável e decrescente em data', async () => {
    // Na origem (data crescente) o item prioritário fica na última página.
    const review = [...many(250, index => ({ key: `import:r${index}`, recordId: `r${index}`, date: '2026-09-01' })), decisionItem({ key: 'import:late', recordId: 'late', date: '2026-01-01', impact: 'out-of-totals' })]
    const classification = many(30, index => ({ key: `classification:c${index}`, source: 'classification', recordId: `c${index}`, date: '2026-11-11', impact: 'accounted-category-pending' }))
    const build = async () => buildDecisionQueue({ review: await loadSource(pager(review, 100), null), classification: await loadSource(pager(classification, 25), null) })
    const queue = await build()
    expect(queue.items[0].key).toBe('import:late')
    expect(queue.items.slice(1, 31).every(item => item.source === 'classification')).toBe(true) // 11/11 antes de 01/09
    expect((await build()).items.map(i => i.key)).toEqual(queue.items.map(i => i.key))
    expect(queue.outOfTotals).toBe(1)
  })

  it('desempata por identificador estável quando prioridade e data coincidem', () => {
    const items = [decisionItem({ key: 'import:b' }), decisionItem({ key: 'import:a' }), decisionItem({ key: 'classification:z', source: 'classification' })]
    const source: SourceState = { items, total: 3, nextPage: 2, complete: true, failed: false }
    expect(buildDecisionQueue({ review: source, classification: source }).items.map(i => i.key)).toEqual(['classification:z', 'import:a', 'import:b'])
  })

  it('falha no meio preserva o lido; nova tentativa continua de onde parou, sem duplicar nem reler as páginas boas', async () => {
    const items = many(250, index => ({ key: `import:r${index}`, recordId: `r${index}`, date: day(index) }))
    const fail = new Set([2])
    const fetchPage = pager(items, 100, fail)
    const first = await loadSource(fetchPage, null)
    expect(first).toMatchObject({ failed: true, nextPage: 2, total: 250 })
    expect(first.items).toHaveLength(100)
    expect(buildDecisionQueue({ review: first, classification: { items: [], total: 0, nextPage: 2, complete: true, failed: false } })).toMatchObject({ failedSources: ['import'], total: 250, totalComplete: true })
    fail.clear(); fetchPage.mockClear()
    const second = await loadSource(fetchPage, first)
    expect(fetchPage.mock.calls.map(([page]) => page)).toEqual([2, 3])
    expect(second).toMatchObject({ failed: false, complete: true })
    expect(new Set(second.items.map(i => i.key)).size).toBe(250)
  })

  it('fonte que nunca respondeu = cobertura parcial; total só é completo com as duas fontes', () => {
    const only: SourceState = { items: [decisionItem()], total: 4, nextPage: 2, complete: true, failed: false }
    expect(buildDecisionQueue({ review: null, classification: only })).toMatchObject({ failedSources: ['import'], total: 4, totalComplete: false })
  })

  it('não une movimentos parecidos sem vínculo comprovado (fontes e registros distintos)', () => {
    const a = decisionItem({ key: 'import:a', description: 'Loja X', amount: '80.00' }), b = decisionItem({ key: 'import:b', description: 'Loja X', amount: '80.00' })
    const c = decisionItem({ key: 'classification:c', source: 'classification', description: 'Loja X', amount: '80.00' })
    const queue = buildDecisionQueue({ review: { items: [a, b], total: 2, nextPage: 2, complete: true, failed: false }, classification: { items: [c], total: 1, nextPage: 2, complete: true, failed: false } })
    expect(queue.items).toHaveLength(3)
  })

  it('locatePage começa pela dica e só varre as outras páginas se o item não estiver nela', async () => {
    const items = Array.from({ length: 250 }, (_, index) => `i${index}`)
    const fetchPage = vi.fn(async (page: number) => ({ items: items.slice((page - 1) * 100, page * 100), total: 250, pageSize: 100 }))
    const found = await locatePage(fetchPage, p => p.items.includes('i120'), 2)
    expect(found.items).toContain('i120'); expect(fetchPage).toHaveBeenCalledTimes(1)
    fetchPage.mockClear()
    const moved = await locatePage(fetchPage, p => p.items.includes('i120'), 1) // a lista mudou: o item não está mais onde estava
    expect(moved.items).toContain('i120'); expect(fetchPage.mock.calls.map(([page]) => page)).toEqual([1, 2])
    fetchPage.mockClear()
    await locatePage(fetchPage, p => p.items.includes('inexistente'), 2)
    expect(fetchPage).toHaveBeenCalledTimes(3) // só conclui "não existe" depois de varrer tudo
  })
})
