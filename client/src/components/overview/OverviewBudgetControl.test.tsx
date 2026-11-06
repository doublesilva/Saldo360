import { useState } from 'react'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { BudgetControl } from '../../api/overview'
import { budgetControl, group } from './budgetControlFixtures'
import { OverviewBudgetSummarySection } from './OverviewBudgetControl'

afterEach(cleanup)

const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
function Harness({ value, onNavigate }: { value: BudgetControl; onNavigate: () => void }) {
  const [open, setOpen] = useState(false)
  return <OverviewBudgetSummarySection control={value} competence="2026-11" pressuresOpen={open} onPressuresOpenChange={setOpen} onNavigate={onNavigate} />
}
const draw = (value: BudgetControl, onNavigate = vi.fn()) => { render(<Harness value={value} onNavigate={onNavigate} />); return onNavigate }
const summary = () => screen.getByRole('article', { name: 'Orçamento mensal' })
const bars = () => screen.getByRole('list', { name: 'Cartões e fora dos cartões' })

describe('Meu orçamento (T11.2 / T12.2)', () => {
  it('é um resumo: meta efetiva, já gasto, futuras conhecidas, estimativas, total previsto e margem, com a linguagem direta', () => {
    draw(budgetControl())
    const total = summary()
    expect(text(total)).toContain('Meta mensal efetiva'); expect(text(total)).toContain('R$ 12.500,00')
    expect(text(total)).toMatch(/Já gasto\s*R\$ 5\.000,00/); expect(text(total)).toMatch(/Despesas futuras já conhecidas\s*R\$ 1\.500,00/); expect(text(total)).toMatch(/Estimativas complementares\s*R\$ 1\.000,00/)
    expect(text(total)).toMatch(/Total previsto de gastos\s*R\$ 7\.500,00/); expect(text(total)).toMatch(/Margem em relação à meta\s*R\$ 5\.000,00/); expect(text(total)).toContain('Dentro da meta')
    for (const old of ['Consumo total projetado', 'Comprometidas, não realizadas', 'Valor não estimável']) expect(text(document.body)).not.toContain(old)
  })

  it('não chama a margem do orçamento de dinheiro disponível', () => {
    draw(budgetControl())
    expect(text(summary())).toContain('não dinheiro disponível na conta'); expect(text(summary())).not.toMatch(/saldo disponível|dinheiro disponível:/i)
  })

  it('usa duas barras, cartões e fora dos cartões, em vez de três cards detalhados', () => {
    draw(budgetControl())
    expect(screen.getAllByRole('article')).toHaveLength(1)
    const items = within(bars()).getAllByRole('listitem')
    expect(items).toHaveLength(2)
    expect(text(items[0])).toContain('Cartões de crédito'); expect(text(items[0])).toContain('Total previsto R$ 3.500,00 de R$ 5.000,00'); expect(text(items[0])).toContain('Margem de R$ 1.500,00')
    expect(text(items[1])).toContain('Fora dos cartões'); expect(text(items[1])).toContain('Total previsto R$ 4.000,00 de R$ 7.500,00')
    expect(within(items[0]).getByRole('progressbar', { name: /Total previsto de Cartões de crédito/ }).getAttribute('aria-valuetext')).toBe('70,0%')
  })

  it('explica que o gasto é por competência e que categorias e fixas não são somadas', () => {
    draw(budgetControl())
    expect(screen.getByText(/pagamento da fatura afeta o caixa, mas não aumenta o gasto/)).not.toBeNull()
    expect(screen.getByText(/não são somadas a este total/)).not.toBeNull()
  })

  it('mostra de onde vem a meta: herdada, personalizada, registro anterior ou não configurada', () => {
    draw({ ...budgetControl(), totalTargetOrigin: 'Inherited', totalTargetFrom: '2026-10' })
    expect(text(summary())).toContain('Meta herdada do padrão, em vigor desde Outubro de 2026')
    cleanup(); draw({ ...budgetControl(), totalTargetOrigin: 'Customized' })
    expect(text(summary())).toContain('Meta personalizada neste mês')
    cleanup(); draw({ ...budgetControl(), totalTargetOrigin: 'Historical' })
    expect(text(summary())).toContain('Meta do registro mensal anterior')
    cleanup(); draw(budgetControl())
    expect(text(summary())).not.toMatch(/herdada|personalizada|registro mensal/)
  })

  it('excesso preservado: mostra o excesso, a etiqueta e o que já foi gasto acima da meta', () => {
    const base = budgetControl()
    draw({ ...base, total: { ...base.total, excess: '300.00', margin: '0.00', status: 'Exceeded' }, cards: group('Cards', { realized: '5200.00', projected: '5200.00', excess: '200.00', excessRealized: '200.00', margin: '0.00', status: 'Exceeded', projectedPercent: '104.0' }) })
    expect(text(summary())).toContain('Acima da meta'); expect(text(summary())).toMatch(/Excesso sobre a meta\s*R\$ 300,00/)
    const cards = within(bars()).getAllByRole('listitem')[0]
    expect(text(cards)).toContain('Excesso de R$ 200,00'); expect(text(cards)).toContain('Já gasto acima da meta em R$ 200,00')
  })

  it('competência sem meta configurada: diz "Meta não configurada" (nunca zero) e oferece definir as metas', () => {
    const base = budgetControl()
    const onNavigate = draw({ ...base, state: 'TargetNotDefined', totalTargetOrigin: 'Undefined', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, projectedPercent: null, status: 'NoTarget' },
      cards: group('Cards', { state: 'TargetNotDefined', target: null, margin: null, excess: null, excessRealized: null, projectedPercent: null, status: 'NoTarget' }) })
    expect(text(summary())).toContain('Meta não configurada'); expect(text(summary())).toContain('O orçamento total não foi definido.'); expect(text(summary())).not.toMatch(/Sem meta/)
    expect(text(within(bars()).getAllByRole('listitem')[0])).toContain('Meta não configurada')
    fireEvent.click(within(summary()).getByRole('button', { name: 'Definir metas' }))
    expect(onNavigate).toHaveBeenCalledWith('budgets', { competence: '2026-11', budgetGroup: undefined })
  })

  it('conta isolada: a meta global não é atribuída a ela e não é dada como "não configurada"', () => {
    const base = budgetControl()
    draw({ ...base, scopeKind: 'account', targetsAttributable: false, notes: ['TargetsGlobalOnly'], totalTargetOrigin: 'Inherited', total: { ...base.total, targetDefined: false, target: null, margin: null, excess: null, status: 'NoTarget' },
      cards: group('Cards', { state: 'TargetNotAttributable', target: null, margin: null, excess: null, status: 'NoTarget' }), outsideCards: group('OutsideCards', { state: 'TargetNotAttributable', target: null, margin: null, excess: null, status: 'NoTarget' }) })
    expect(text(summary())).toContain('Meta global'); expect(text(summary())).toContain('Esta conta isolada não recebe a meta global'); expect(text(summary())).not.toContain('herdada')
    expect(screen.getByText(/Selecione "Todas as contas"/)).not.toBeNull()
    expect(text(bars())).toContain('A meta é global e não se aplica a uma conta isolada'); expect(text(bars())).not.toContain('Meta não configurada')
    expect(screen.queryByText('O orçamento total não foi definido.')).toBeNull()
    expect(within(summary()).getByRole('button', { name: 'Abrir orçamento completo' })).not.toBeNull()
  })

  it('estados incompletos: lista as lacunas dos dados', () => {
    draw({ ...budgetControl(), cards: group('Cards', { state: 'Partial', reasons: ['CardCoverageIncomplete', 'MissingPayerAccount'] }) })
    expect(text(bars())).toContain('Dados incompletos: cobertura de cartões incompleta; cartão sem conta pagadora.')
  })

  it('as pressões ficam recolhidas; abertas, mantêm a ordem do servidor, e o não estimável não vira valor', () => {
    draw(budgetControl())
    expect(screen.queryByRole('list', { name: '' })).toBeNull(); expect(text(document.body)).not.toContain('Despesas vencidas ainda não liquidadas')
    fireEvent.click(screen.getByText('O que está pressionando meu orçamento?'))
    const items = screen.getAllByRole('listitem').filter(item => /^\d\./.test(text(item)))
    expect(items).toHaveLength(4)
    expect(text(items[0])).toContain('1. Despesas vencidas ainda não liquidadas'); expect(text(items[0])).toContain('R$ 900,00'); expect(text(items[0])).toContain('Impacto no caixa · Confirmado')
    expect(text(items[1])).toContain('Referência: meta dos cartões de R$ 5.000,00')
    expect(text(items[3])).toContain('Ainda não é possível calcular'); expect(text(items[3])).toContain('Impacto não estimável'); expect(text(items[3])).not.toContain('Valor não estimável')
  })

  it('sem pressões: diz que nenhum fator foi identificado', () => {
    draw({ ...budgetControl(), pressures: [], recommendations: [] })
    fireEvent.click(screen.getByText('O que está pressionando meu orçamento?'))
    expect(screen.getByText('Nenhum fator de pressão identificado com os dados disponíveis.')).not.toBeNull()
  })

  it('cada pressão leva à tela correta: faturas, categorias, Agenda e contas; o orçamento completo abre na competência', () => {
    const onNavigate = draw(budgetControl())
    fireEvent.click(within(summary()).getByRole('button', { name: 'Abrir orçamento completo' }))
    fireEvent.click(screen.getByText('O que está pressionando meu orçamento?'))
    for (const name of ['Abrir Agenda', 'Consultar faturas', 'Abrir orçamento', 'Ver contas']) fireEvent.click(screen.getByRole('button', { name }))
    expect(onNavigate.mock.calls).toEqual([
      ['budgets', { competence: '2026-11', budgetGroup: undefined }], ['calendar', { month: '2026-11' }], ['cards', undefined],
      ['budgets', { competence: '2026-11', budgetGroup: 'categories' }], ['accounts', undefined],
    ])
  })
})
