import { describe, expect, it } from 'vitest'
import { planActual } from '../planActualFixtures'
import { budgetControl } from './budgetControlFixtures'
import { decision } from './decisionFixtures'
import { budgetDestination, buildPriorityItems, visibleItems, type PriorityInput } from './priorityItems'

const calm = (): PriorityInput => {
  const base = decision()
  return {
    competence: '2026-11', plan: { ...planActual, sync: null }, classification: { state: 'Complete', needsReview: 0, notEvaluated: 0 },
    decision: { ...base, commitments: { ...base.commitments, overdue: '0.00' }, recovery: { ...base.recovery, gapToTarget: '100.00' } },
    control: budgetControl({ pressures: [], recommendations: [] }),
  }
}
const plain = (value: string | undefined) => value?.replace(/[  ]/g, ' ')
const keys = (input: PriorityInput) => buildPriorityItems(input).map(item => item.key)

describe('Onde preciso agir — escolha e ordem dos itens', () => {
  it('sem risco, excesso nem pendência não há itens', () => { expect(buildPriorityItems(calm())).toEqual([]) })

  it('sem dados do servidor não inventa alertas', () => { expect(buildPriorityItems({ competence: '2026-11', decision: null, control: null, plan: null, classification: null })).toEqual([]) })

  it('ordena risco de caixa ou da meta, depois excesso de orçamento, depois pendências de confiabilidade', () => {
    const input = calm(); const base = input.decision!
    const items = buildPriorityItems({
      ...input,
      decision: { ...base, coverage: { ...base.coverage, state: 'Insufficient', deficit: '500.00', firstBeyondLimitDate: '2026-11-22' }, commitments: { ...base.commitments, overdue: '90.00' } },
      control: budgetControl({ recommendations: [{ code: 'UncategorizedExpenses', action: 'OpenReview', actionContext: null }] }),
      classification: { state: 'Complete', needsReview: 2, notEvaluated: 0 },
    })
    expect(items.map(item => item.rank)).toEqual([...items.map(item => item.rank)].sort())
    expect(items.map(item => item.key).slice(0, 2)).toEqual(['coverage', 'overdue'])
    expect(items.at(-1)?.key).toBe('reliability-budget')
    expect(plain(items.find(item => item.key === 'coverage')?.impact)).toBe('Déficit estimado de R$ 500,00.')
  })

  it('cobertura insuficiente sem déficit calculado diz que o valor não pode ser calculado, nunca zero', () => {
    const input = calm(); const base = input.decision!
    const item = buildPriorityItems({ ...input, decision: { ...base, coverage: { ...base.coverage, state: 'Insufficient', deficit: null } } }).find(i => i.key === 'coverage')
    expect(item?.impact).toBe('O valor do déficit ainda não pode ser calculado.'); expect(item?.impact).not.toMatch(/R\$ 0/)
  })

  it('cobertura só com cheque especial não é tratada como saldo próprio', () => {
    const input = calm(); const base = input.decision!
    const item = buildPriorityItems({ ...input, decision: { ...base, coverage: { ...base.coverage, state: 'CoveredWithCredit' } } })[0]
    expect(item.rank).toBe(1); expect(item.impact).toContain('crédito do banco, que não é saldo seu')
  })

  it('recuperação abaixo da meta: meta provisória é dita provisória e meta ausente não gera alerta de desvio', () => {
    const input = calm(); const base = input.decision!
    const provisional = buildPriorityItems({ ...input, decision: { ...base, recovery: { ...base.recovery, state: 'Provisional', gapToTarget: '-300.00' } } })[0]
    expect(plain(provisional.impact)).toBe('Faltam R$ 300,00 para a meta provisória.')
    const fixed = buildPriorityItems({ ...input, decision: { ...base, recovery: { ...base.recovery, state: 'Fixed', gapToTarget: '-300.00' } } })[0]
    expect(plain(fixed.impact)).toBe('Faltam R$ 300,00 para a meta.')
    expect(keys({ ...input, decision: { ...base, recovery: { ...base.recovery, state: 'NotRegistered', gapToTarget: '-300.00' } } })).toEqual([])
  })

  it('compromissos vencidos não viram alerta em competência encerrada', () => {
    const input = calm(); const base = input.decision!
    expect(keys({ ...input, decision: { ...base, context: { ...base.context, period: 'Closed' }, commitments: { ...base.commitments, overdue: '90.00' } } })).toEqual([])
  })

  it('excesso do orçamento vem das pressões e do total, sem repetir a recomendação equivalente', () => {
    const input = calm(); const full = budgetControl()
    const items = buildPriorityItems({ ...input, pfControl: { ...full, total: { ...full.total, excess: '250.00', status: 'Exceeded' } } })
    expect(items.map(item => item.key)).toEqual(expect.arrayContaining(['total-over', 'pressure-CardsOverTarget-', 'pressure-CategoryOverTarget-Lazer']))
    expect(items.filter(item => item.key === 'recommendation-CardsOverTarget')).toHaveLength(0)
    expect(plain(items.find(item => item.key === 'total-over')?.impact)).toBe('Excesso de R$ 250,00 sobre a meta.')
    expect(plain(items.find(item => item.key === 'pressure-CardsOverTarget-')?.impact)).toBe('Excesso de R$ 400,00 sobre a meta.')
  })

  it('agrupa os avisos técnicos por consequência em um só item, com as causas listadas uma vez cada', () => {
    const input = calm(); const base = input.decision!
    const items = buildPriorityItems({
      ...input, decision: { ...base, snapshotConsistent: false, commitments: { ...base.commitments, caveats: ['CardCoverageIncomplete'] }, coverage: { ...base.coverage, caveats: ['CardCoverageIncomplete', 'BalanceDivergentFromBank'] } },
      plan: { ...planActual, snapshotConsistent: false, coverage: { ...planActual.coverage, complete: false, reviewAffectsConfidence: true, reviewCount: 4 } },
    })
    const group = items.filter(item => item.rank === 3)
    expect(group).toHaveLength(1)
    const texts = group[0].causes!.map(cause => cause.text)
    expect(new Set(texts).size).toBe(texts.length)
    expect(texts.filter(text => text.includes('faturas de cartão sem conta pagadora'))).toHaveLength(1)
    expect(texts.filter(text => text.includes('mudaram durante a leitura'))).toHaveLength(1)
    expect(texts).toEqual(expect.arrayContaining(['4 movimentação(ões) do banco aguardam sua decisão (ou foram ignoradas por você) e não entram nos totais.', 'Projeção incompleta: parte dos dados ainda não pode ser calculada.']))
    expect(group[0].causes!.find(cause => cause.key === 'snapshot')?.action).toEqual({ label: 'Atualizar', reload: true })
  })

  it('as explicações das ressalvas sozinhas não são um alerta', () => {
    const input = calm()
    expect(keys({ ...input, plan: { ...planActual, situation: { ...planActual.situation, reasons: ['InsufficientHistory'] } } })).toEqual([])
  })
})

describe('Pontos de atenção — orçamento PF × contexto bancário', () => {
  it('o excesso dos cartões vem do orçamento PF, uma só vez, mesmo sem meta total, e não depende do controle bancário', () => {
    const input = calm(); const full = budgetControl()
    const noTotal = { ...full, total: { ...full.total, targetDefined: false, target: null, excess: null, status: 'NoTarget' as const } }
    const fromPf = buildPriorityItems({ ...input, control: budgetControl({ pressures: [], recommendations: [] }), pfControl: noTotal })
    expect(fromPf.filter(item => item.key === 'pressure-CardsOverTarget-')).toHaveLength(1)
    expect(plain(fromPf.find(item => item.key === 'pressure-CardsOverTarget-')?.impact)).toBe('Excesso de R$ 400,00 sobre a meta.')
    expect(fromPf.some(item => item.key === 'recommendation-CardsOverTarget')).toBe(false)
    // O controle bancário sozinho (conta escolhida) não gera alerta de orçamento.
    expect(keys({ ...input, control: full, pfControl: null }).filter(key => key.startsWith('pressure-Cards') || key === 'total-over')).toEqual([])
  })

  it('encargos e receitas não recebidas continuam vindo do contexto bancário', () => {
    const input = calm(); const base = budgetControl({ recommendations: [] })
    const bank = { ...base.pressures[0], kind: 'CardFinanceCharges', amount: '90.00', amountKind: 'Charged' as const, action: 'OpenCards' as const }
    const items = buildPriorityItems({ ...input, control: { ...base, pressures: [bank] }, pfControl: budgetControl({ pressures: [], recommendations: [] }) })
    expect(items.map(item => item.key)).toEqual(['pressure-CardFinanceCharges-'])
  })
})

describe('Onde preciso agir — itens visíveis', () => {
  const item = (key: string, rank: 1 | 2 | 3) => ({ key, rank, title: key, impact: '', action: null })

  it('mostra três por padrão e todos ao expandir', () => {
    const items = [item('a', 2), item('b', 2), item('c', 2), item('d', 3), item('e', 3)]
    expect(visibleItems(items, false).map(i => i.key)).toEqual(['a', 'b', 'c'])
    expect(visibleItems(items, true)).toHaveLength(5)
  })

  it('nunca esconde um item de risco de caixa ou da meta', () => {
    const items = [item('a', 1), item('b', 1), item('c', 1), item('d', 1), item('e', 2)]
    expect(visibleItems(items, false).map(i => i.key)).toEqual(['a', 'b', 'c', 'd'])
  })
})

describe('Destinos de ação do orçamento', () => {
  it('leva cada ação à tela correta, com a competência quando a tela precisa dela', () => {
    expect(budgetDestination('OpenCards', null, '2026-11')).toEqual({ section: 'cards' })
    expect(budgetDestination('OpenBudget', 'categories', '2026-11')).toEqual({ section: 'budgets', context: { competence: '2026-11', budgetGroup: 'categories' } })
    expect(budgetDestination('OpenAgenda', null, '2026-11')).toEqual({ section: 'calendar', context: { month: '2026-11' } })
    expect(budgetDestination('OpenPlanning', null, '2026-11')).toEqual({ section: 'planning', context: { planningTab: 'projection' } })
    expect(budgetDestination('OpenReview', null, '2026-11')).toEqual({ section: 'transactions', context: { review: { kind: 'category' } } })
    expect(budgetDestination('OpenAccounts', null, '2026-11')).toEqual({ section: 'accounts' })
  })
})

describe('pontos de atenção — sincronização bancária por conexão', () => {
  const sync = (connections: Partial<NonNullable<PriorityInput['plan']>['sync'] extends infer S ? S extends { connections: (infer C)[] } ? C : never : never>[]) => ({
    ...calm(), plan: { ...planActual, sync: { ...planActual.sync!, connections: connections.map((c, i) => ({ connectionId: `c${i + 1}`, label: `Banco ${i + 1}`, status: 'Completed', lastSuccessAt: '2026-11-11T12:00:00Z', lastAttemptAt: '2026-11-11T12:00:00Z', nextAttemptAt: null, consentExpiringSoon: false, stale: false, ...c })) } },
  }) as PriorityInput
  const find = (input: PriorityInput) => buildPriorityItems(input).find(item => item.key === 'sync-attention')

  it('conexão saudável não gera alerta', () => expect(find(sync([{}]))).toBeUndefined())

  it('uma conexão atrasada: nomeia o banco, mostra o último sucesso e abre aquela conexão', () => {
    const item = find(sync([{ stale: true }, { connectionId: 'ok', label: 'Outro', stale: false }]))!
    expect(item.title).toBe('Sincronização bancária atrasada · Banco 1')
    expect(plain(item.impact)).toContain('Último sucesso em 11/11/2026')
    expect(item.action).toEqual({ label: 'Ver conexão', to: { section: 'automation', context: { automationTab: 'connections', connectionIds: ['c1'] } } })
  })

  it('falha preserva o último sucesso e não é tratada como dado recente', () => {
    const item = find(sync([{ status: 'Failed', lastAttemptAt: '2026-11-12T09:00:00Z' }]))!
    expect(item.title).toBe('Falha na sincronização bancária · Banco 1')
    expect(plain(item.impact)).toContain('Último sucesso em 11/11/2026')
  })

  it('nunca sincronizada não inventa data', () => expect(plain(find(sync([{ stale: true, lastSuccessAt: null }]))!.impact)).toContain('Ainda sem sincronização bem-sucedida.'))

  it('várias conexões afetadas: mostra a quantidade e leva a todas elas', () => {
    const item = find(sync([{ stale: true }, { status: 'ConsentExpired' }, {}]))!
    expect(item.title).toBe('Sincronização bancária com problema · 2 conexões')
    expect(item.action?.to?.context?.connectionIds).toEqual(['c1', 'c2'])
  })
})
