import { describe, expect, it } from 'vitest'
import { buildDecisionQueue } from './queueModel'
import { classificationItem, classificationPage, importItem, reviewPage } from './queueFixtures'

describe('fila Requer sua decisão — modelo', () => {
  it('rotula o impacto só pelo que o diagnóstico sustenta', () => {
    const queue = buildDecisionQueue({
      review: reviewPage([importItem({ postingId: 'a', accounted: false }), importItem({ postingId: 'b', accounted: true }), importItem({ postingId: 'c', accounted: undefined })]),
      classification: classificationPage([classificationItem({ recordId: 'k1' }), classificationItem({ recordId: 'k2', action: 'Planned' })]),
    })
    const byId = Object.fromEntries(queue.items.map(item => [item.recordId, item.impactText]))
    expect(byId.a).toBe('Fora dos totais até a revisão')
    expect(byId.b).toBe('Já contabilizado · continua nos totais até a decisão')
    expect(byId.c).toBe('Impacto ainda indeterminado')
    expect(byId.k1).toBe('Já contabilizado · categoria pendente')
    expect(byId.k2).toBe('Impacto ainda indeterminado')
  })

  it('ordena primeiro o que está fora dos totais, depois o mais recente, de forma determinística', () => {
    const input = {
      review: reviewPage([importItem({ postingId: 'old', date: '2026-11-01' }), importItem({ postingId: 'in', accounted: true, date: '2026-11-10' }), importItem({ postingId: 'new', date: '2026-11-09' })]),
      classification: classificationPage([classificationItem({ recordId: 'k', date: '2026-11-11' })]),
    }
    const order = buildDecisionQueue(input).items.map(i => i.recordId)
    expect(order).toEqual(['new', 'old', 'in', 'k'])
    expect(buildDecisionQueue({ review: input.review, classification: input.classification }).items.map(i => i.recordId)).toEqual(order)
  })

  it('não une movimentos parecidos sem vínculo comprovado (mesmo valor e descrição, fontes e registros distintos)', () => {
    const queue = buildDecisionQueue({
      review: reviewPage([importItem({ postingId: 'a', amount: '80.00', description: 'Loja X', accounted: true }), importItem({ postingId: 'b', amount: '80.00', description: 'Loja X', accounted: true })]),
      classification: classificationPage([classificationItem({ recordId: 'k', amount: '80.00', description: 'Loja X' })]),
    })
    expect(queue.items).toHaveLength(3)
    expect(queue.total).toBe(3)
  })

  it('não repete a mesma decisão quando o mesmo registro vem duas vezes da mesma fonte', () => {
    const queue = buildDecisionQueue({ review: reviewPage([importItem({ postingId: 'a' }), importItem({ postingId: 'a' })], 1), classification: null })
    expect(queue.items).toHaveLength(1)
  })

  it('fonte ausente vira cobertura parcial e mantém as pendências da outra; total vem das fontes que carregaram', () => {
    const queue = buildDecisionQueue({ review: null, classification: classificationPage([classificationItem()], 4) })
    expect(queue.failedSources).toEqual(['import'])
    expect(queue.items).toHaveLength(1)
    expect(queue.total).toBe(4)
    expect(buildDecisionQueue({ review: reviewPage([]), classification: classificationPage([]) })).toMatchObject({ items: [], total: 0, failedSources: [] })
  })

  it('mostra o tipo, a conta e o motivo vindos do diagnóstico', () => {
    const [link] = buildDecisionQueue({ review: reviewPage([importItem({ reviewKind: 'Link', reason: 'TransferWithoutCounterpart' })]), classification: null }).items
    expect(link).toMatchObject({ typeLabel: 'Vínculo entre movimentos', account: 'Conta Corrente', source: 'import' })
    expect(link.reason.length).toBeGreaterThan(0)
  })
})
