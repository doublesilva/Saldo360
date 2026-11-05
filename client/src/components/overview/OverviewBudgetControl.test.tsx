import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { BudgetControl } from '../../api/overview'
import { budgetControl, group } from './budgetControlFixtures'
import { OverviewBudgetControlSection } from './OverviewBudgetControl'

afterEach(cleanup)

const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
const draw = (value: BudgetControl, onNavigate = vi.fn()) => { render(<OverviewBudgetControlSection control={value} competence="2026-11" onNavigate={onNavigate} />); return onNavigate }
const card = (name: string) => screen.getByRole('article', { name })

describe('Visão Geral — consumo do orçamento (T11.2)', () => {
  it('mostra o orçamento definido, realizado, comprometido, estimado, projetado e a margem, separadamente', () => {
    draw(budgetControl())
    const total = card('Orçamento mensal')
    expect(text(total)).toMatch(/Orçamento definido\s*R\$ 12\.500,00/); expect(text(total)).toMatch(/Despesas realizadas\s*R\$ 5\.000,00/)
    expect(text(total)).toMatch(/Comprometidas, não realizadas\s*R\$ 1\.500,00/); expect(text(total)).toMatch(/Estimadas complementares\s*R\$ 1\.000,00/)
    expect(text(total)).toMatch(/Consumo total projetado\s*R\$ 7\.500,00/); expect(text(total)).toMatch(/Margem até o orçamento\s*R\$ 5\.000,00/); expect(text(total)).toContain('Dentro da meta')
  })

  it('apresenta os dois grupos exclusivos com as suas metas, e a provisão alimentar nos cartões', () => {
    draw(budgetControl())
    const cards = card('Cartões de crédito'); const outside = card('Fora dos cartões')
    expect(text(cards)).toMatch(/Orçamento definido\s*R\$ 5\.000,00/); expect(text(cards)).toContain('Estimadas (parcelas e provisão alimentar)')
    expect(text(outside)).toMatch(/Orçamento definido\s*R\$ 7\.500,00/); expect(text(outside)).toMatch(/Consumo total projetado\s*R\$ 4\.000,00/)
    expect(within(cards).getByRole('progressbar', { name: /Consumo projetado de Cartões de crédito/ }).getAttribute('aria-valuetext')).toBe('70,0%')
  })

  it('explica que o consumo é por competência e que metas de categoria e fixas não são somadas', () => {
    draw(budgetControl())
    expect(screen.getByText(/pagamento da fatura afeta o caixa, mas não aumenta o consumo/)).not.toBeNull()
    expect(screen.getByText(/não são somadas a este total/)).not.toBeNull()
  })

  it('excesso preservado: mostra o excesso, a etiqueta e o que já foi realizado acima da meta', () => {
    const base = budgetControl()
    draw({ ...base, cards: group('Cards', { realized: '5200.00', projected: '5200.00', excess: '200.00', excessRealized: '200.00', margin: '0.00', status: 'Exceeded', projectedPercent: '104.0' }) })
    const cards = card('Cartões de crédito')
    expect(text(cards)).toContain('Acima da meta'); expect(text(cards)).toMatch(/Excesso sobre o orçamento\s*R\$ 200,00/)
    expect(text(cards)).toContain('Já realizado acima da meta em R$ 200,00')
  })

  it('orçamento não configurado: diz "Não definido" (nunca zero) e oferece definir as metas', () => {
    const base = budgetControl()
    const onNavigate = draw({ ...base, state: 'TargetNotDefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' },
      cards: group('Cards', { state: 'TargetNotDefined', target: null, margin: null, excess: null, excessRealized: null, projectedPercent: null, status: 'NoTarget' }) })
    const total = card('Orçamento mensal')
    expect(text(total)).toContain('O orçamento total não foi definido.'); expect(text(total)).toMatch(/Orçamento definido\s*Não definido/); expect(text(total)).toContain('Sem meta')
    expect(text(card('Cartões de crédito'))).toContain('Meta não definida')
    fireEvent.click(within(total).getByRole('button', { name: 'Definir metas' }))
    expect(onNavigate).toHaveBeenCalledWith('budgets', { competence: '2026-11', budgetGroup: undefined })
  })

  it('conta isolada: a meta global não é apresentada como da conta', () => {
    const base = budgetControl()
    draw({ ...base, scopeKind: 'account', targetsAttributable: false, notes: ['TargetsGlobalOnly'], total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, status: 'NoTarget' },
      cards: group('Cards', { state: 'TargetNotAttributable', target: null, margin: null, excess: null, status: 'NoTarget' }), outsideCards: group('OutsideCards', { state: 'TargetNotAttributable', target: null, margin: null, excess: null, status: 'NoTarget' }) })
    expect(text(card('Cartões de crédito'))).toContain('A meta é global e não se aplica a uma conta isolada')
    expect(screen.getByText(/Selecione "Todas as contas"/)).not.toBeNull()
    expect(screen.queryByText('O orçamento total não foi definido.')).toBeNull()
  })

  it('estados incompletos: lista as lacunas dos dados', () => {
    draw({ ...budgetControl(), cards: group('Cards', { state: 'Partial', reasons: ['CardCoverageIncomplete', 'MissingPayerAccount'] }) })
    expect(text(card('Cartões de crédito'))).toContain('Dados incompletos: cobertura de cartões incompleta; cartão sem conta pagadora.')
  })

  it('pressões: ordem do servidor, valor, referência, impacto, qualidade e ação; o não estimável não vira valor', () => {
    draw(budgetControl())
    const section = screen.getByRole('region', { name: 'O que está pressionando meu orçamento?' })
    const items = within(section).getAllByRole('listitem')
    expect(items).toHaveLength(4)
    expect(text(items[0])).toContain('1. Despesas vencidas ainda não liquidadas'); expect(text(items[0])).toContain('R$ 900,00'); expect(text(items[0])).toContain('Impacto no caixa · Confirmado')
    expect(text(items[1])).toContain('Referência: meta dos cartões de R$ 5.000,00'); expect(text(items[1])).toContain('Projetado')
    expect(text(items[2])).toContain('Categoria Lazer acima do orçamento')
    expect(text(items[3])).toContain('Valor não estimável'); expect(text(items[3])).toContain('Impacto não estimável')
  })

  it('sem pressões: diz que nenhum fator foi identificado', () => {
    draw({ ...budgetControl(), pressures: [], recommendations: [] })
    expect(screen.getByText('Nenhum fator de pressão identificado com os dados disponíveis.')).not.toBeNull()
    expect(screen.getByText('Nenhuma ação necessária com os dados disponíveis.')).not.toBeNull()
  })

  it('cada pressão e recomendação leva à tela correta: faturas, categorias, Agenda, Projeção, revisão de lançamentos e contas', () => {
    const onNavigate = draw(budgetControl())
    const pressures = screen.getByRole('region', { name: 'O que está pressionando meu orçamento?' })
    fireEvent.click(within(pressures).getByRole('button', { name: 'Abrir Agenda' }))
    fireEvent.click(within(pressures).getByRole('button', { name: 'Consultar faturas' }))
    fireEvent.click(within(pressures).getByRole('button', { name: 'Abrir orçamento' }))
    fireEvent.click(within(pressures).getByRole('button', { name: 'Ver contas' }))
    const actions = screen.getByRole('region', { name: 'Ações recomendadas' })
    fireEvent.click(within(actions).getByRole('button', { name: 'Classificar lançamentos' }))
    fireEvent.click(within(actions).getByRole('button', { name: 'Abrir Projeção financeira' }))
    expect(onNavigate.mock.calls).toEqual([
      ['calendar', { month: '2026-11' }], ['cards'], ['budgets', { competence: '2026-11', budgetGroup: 'categories' }], ['accounts'],
      ['transactions', { review: { kind: 'category' } }], ['planning', { planningTab: 'projection' }],
    ])
  })

  it('recomendações usam o texto objetivo da especificação', () => {
    draw(budgetControl())
    const actions = screen.getByRole('region', { name: 'Ações recomendadas' })
    expect(text(actions)).toContain('Os cartões ultrapassaram a meta mensal. Consulte as faturas.')
    expect(text(actions)).toContain('Há despesas sem categoria que dificultam a análise do orçamento.')
    expect(text(actions)).toContain('A receita PJ depende de uma estimativa ainda não confirmada.')
  })
})
