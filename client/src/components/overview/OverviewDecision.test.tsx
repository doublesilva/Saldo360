import { useState } from 'react'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialDecision } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import { planActual } from '../planActualFixtures'
import { accountCoverage, decision } from './decisionFixtures'
import { OverviewSituationSection, type DecisionStatus } from './OverviewDecision'

afterEach(cleanup)

const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
type Plan = { data: PlanActualDashboard | null; loading: boolean; failed: boolean }
const loadedPlan: Plan = { data: { ...planActual, periodKind: 'Current' }, loading: false, failed: false }

function Harness({ value, plan, status, onNavigate, competence }: { value: FinancialDecision | null; plan: Plan; status: DecisionStatus; onNavigate: () => void; competence: string }) {
  const [open, setOpen] = useState(false)
  return <OverviewSituationSection competence={competence} decision={value} decisionStatus={status} plan={plan} scopeText="Itaú PF" coverageOpen={open} onCoverageOpenChange={setOpen} onNavigate={onNavigate} />
}
const draw = (value: FinancialDecision | null, { plan = loadedPlan, status = value ? 'ready' : 'unavailable', competence = '2026-11' }: { plan?: Plan; status?: DecisionStatus; competence?: string } = {}) => {
  const onNavigate = vi.fn()
  render(<Harness value={value} plan={plan} status={status} onNavigate={onNavigate} competence={competence} />)
  return onNavigate
}
const card = (name: string) => screen.getByRole('article', { name })
const openDetails = () => fireEvent.click(screen.getByText('Detalhes de cobertura, compromissos e margem segura'))

describe('Minha situação financeira (T12.2)', () => {
  it('mostra o contexto: competência, tipo do período, conta e data da posição bancária', () => {
    draw(decision())
    const context = screen.getByLabelText('Contexto da análise')
    expect(text(context)).toContain('Novembro de 2026'); expect(text(context)).toContain('Mês corrente')
    expect(text(context)).toContain('Itaú PF'); expect(text(context)).toContain('Posição do banco em 04/11/2026')
    cleanup()
    const base = decision()
    draw({ ...base, context: { ...base.context, period: 'Future', bankPositionDate: null } }, { competence: '2026-12' })
    expect(text(screen.getByLabelText('Contexto da análise'))).toContain('Mês futuro'); expect(text(screen.getByLabelText('Contexto da análise'))).toContain('Sem posição informada pelo banco')
  })

  it('tem exatamente quatro indicadores principais, cada valor uma vez', () => {
    draw(decision())
    const names = screen.getAllByRole('article').map(article => article.getAttribute('aria-label'))
    expect(names).toEqual(['Saldo bancário observado', 'Compromissos restantes', 'Fechamento projetado', 'Vou conseguir pagar minhas contas?'])
    expect(screen.getAllByText('Saldo bancário observado')).toHaveLength(1)
    expect(screen.getAllByText('Compromissos restantes')).toHaveLength(1)
  })

  it('saldo negativo é dito em texto e nunca ocultado; saldo positivo e indisponível também', () => {
    draw(decision())
    expect(text(card('Saldo bancário observado'))).toMatch(/-R\$ 1\.000,00/); expect(text(card('Saldo bancário observado'))).toContain('Saldo negativo')
    expect(text(card('Saldo bancário observado'))).toContain('Informado pelo banco em 04/11/2026')
    cleanup()
    const base = decision()
    draw({ ...base, context: { ...base.context, bankBalance: '2500.00' } })
    expect(text(card('Saldo bancário observado'))).toContain('R$ 2.500,00'); expect(text(card('Saldo bancário observado'))).not.toContain('Saldo negativo')
    cleanup()
    draw({ ...base, context: { ...base.context, bankBalance: null, bankPositionDate: null } })
    expect(text(card('Saldo bancário observado'))).toContain('Sem posição do banco'); expect(text(card('Saldo bancário observado'))).not.toMatch(/R\$ 0,00/)
  })

  it('compromissos restantes: total do servidor e atalho para a Agenda na competência analisada', () => {
    const onNavigate = draw(decision())
    expect(text(card('Compromissos restantes'))).toContain('R$ 600,00'); expect(text(card('Compromissos restantes'))).toContain('Ainda falta pagar')
    fireEvent.click(within(card('Compromissos restantes')).getByRole('button', { name: 'Ver na Agenda' }))
    expect(onNavigate).toHaveBeenCalledWith('calendar', { month: '2026-11' })
  })

  it('competência encerrada não afirma que algo estava pendente na época', () => {
    const base = decision()
    draw({ ...base, context: { ...base.context, competence: '2026-10', period: 'Closed' }, commitments: { ...base.commitments, period: 'Closed', paidInMonth: '1234.50', overdue: '0.00' } },
      { plan: { ...loadedPlan, data: { ...planActual, periodKind: 'Past' } } })
    expect(text(card('Compromissos restantes'))).toContain('Ainda em aberto hoje, com vencimento no mês')
    expect(screen.getByRole('article', { name: 'Saldo no fechamento' })).toBeTruthy(); expect(screen.queryByRole('article', { name: 'Fechamento projetado' })).toBeNull()
    openDetails()
    expect(text(document.body)).toMatch(/Débitos efetivos no mês\s*R\$ 1\.234,50/); expect(text(document.body)).not.toContain('Vencidos e não liquidados')
  })

  it('fechamento projetado vem do plan-actual e é dito como projeção, não como saldo do banco', () => {
    draw(decision())
    expect(text(card('Fechamento projetado'))).toContain('R$ 1.200,00'); expect(text(card('Fechamento projetado'))).toContain('Projetado: não é o saldo do banco')
  })

  it('fechamento negativo é identificado em texto; indisponível explica o motivo e não vira zero', () => {
    draw(decision(), { plan: { ...loadedPlan, data: { ...planActual, situation: { ...planActual.situation, projectedClosing: '-300.00' } } } })
    expect(text(card('Fechamento projetado'))).toMatch(/-R\$ 300,00/); expect(text(card('Fechamento projetado'))).toContain('Saldo negativo')
    cleanup()
    draw(decision(), { plan: { ...loadedPlan, data: { ...planActual, situation: { ...planActual.situation, projectedClosing: null, reasons: ['InsufficientHistory'] } } } })
    expect(text(card('Fechamento projetado'))).toContain('Indisponível'); expect(text(card('Fechamento projetado'))).toContain('Histórico insuficiente')
  })

  it('o fechamento carrega e falha sem derrubar os demais indicadores', () => {
    draw(decision(), { plan: { data: null, loading: true, failed: false } })
    expect(text(card('Fechamento projetado'))).toContain('Carregando…'); expect(text(card('Compromissos restantes'))).toContain('R$ 600,00')
    cleanup()
    draw(decision(), { plan: { data: null, loading: false, failed: true } })
    expect(text(card('Fechamento projetado'))).toContain('Ainda não é possível calcular'); expect(text(card('Saldo bancário observado'))).toContain('-R$ 1.000,00')
  })

  it('cobertura: pergunta direta, mensagem objetiva e etiqueta em texto para cada estado', () => {
    const cases: [FinancialDecision['coverage']['state'], string, string][] = [
      ['Covered', 'Seus compromissos estão cobertos.', 'Coberto'],
      ['CoveredWithCredit', 'Os pagamentos dependem de utilização do cheque especial.', 'Só com cheque especial'],
      ['Insufficient', 'Há risco de insuficiência em 22/11/2026.', 'Insuficiente'],
      ['Unavailable', 'Não é possível confirmar a cobertura com os dados disponíveis.', 'Indisponível'],
    ]
    for (const [state, message, tag] of cases) {
      const base = decision()
      draw({ ...base, coverage: { ...base.coverage, state, firstBeyondLimitDate: state === 'Insufficient' ? '2026-11-22' : null, deficit: state === 'Insufficient' ? '500.00' : null, unavailableReason: state === 'Unavailable' ? 'OpeningBalanceNotConfigured' : null } })
      const coverage = card('Vou conseguir pagar minhas contas?')
      expect(text(coverage)).toContain(message); expect(text(coverage)).toContain(tag)
      cleanup()
    }
  })

  it('cobertura mostra só a conclusão e o primeiro risco; saldos mínimos, recebimentos e ressalvas ficam na expansão', () => {
    const base = decision()
    draw({ ...base, coverage: { ...base.coverage, state: 'Insufficient', minimumBalance: '-500.00', firstNegativeDate: '2026-11-22', firstBeyondLimitDate: '2026-11-22', deficit: '500.00', caveats: ['DependsOnInferredIncome'] } })
    const coverage = card('Vou conseguir pagar minhas contas?')
    expect(text(coverage)).toContain('Primeiro risco: Saldo negativo a partir de 22/11/2026.')
    for (const detail of ['Saldo mínimo projetado', 'Déficit', 'Obrigações consideradas', 'Recebimentos esperados', 'depende de receitas estimadas']) expect(text(coverage)).not.toContain(detail)
    expect(text(screen.getByLabelText('Margem segura para gastar à vista'))).toContain('R$ 400,00')
    expect(document.body.textContent).not.toContain('Recebimentos esperados')
    openDetails()
    const details = text(document.body)
    expect(details).toMatch(/Saldo mínimo projetado\s*-R\$ 500,00/); expect(details).toMatch(/Déficit\s*R\$ 500,00/); expect(details).toContain('Recebimentos esperados')
    expect(details).toContain('O resultado depende de receitas estimadas')
    expect(text(document.body)).toContain('(estimada, não garantida)')
  })

  it('consolidado: o risco de cada conta fica na expansão e os saldos não se compensam', () => {
    const base = decision()
    draw({ ...base, context: { ...base.context, scopeKind: 'all', accountId: null }, coverage: { ...base.coverage, state: 'Insufficient', projectedClosing: null, firstBeyondLimitDate: '2026-11-22',
      accounts: [accountCoverage({ accountId: 'pj', name: 'Inter PJ', scope: 'PJ' }), accountCoverage({ state: 'Insufficient', firstBeyondLimitDate: '2026-11-22' })] } })
    openDetails()
    expect(text(document.body)).toMatch(/Inter PJ · PJ\s*Coberto/); expect(text(document.body)).toMatch(/Itaú PF · PF\s*Insuficiente 22\/11/)
    expect(text(document.body)).toContain('Os saldos de contas diferentes não se compensam.')
  })

  it('margem segura calculada aparece em uma linha e não é chamada de saldo disponível; restrições ficam na expansão', () => {
    draw(decision())
    const margin = text(screen.getByLabelText('Margem segura para gastar à vista'))
    expect(margin).toContain('R$ 400,00'); expect(margin).toContain('Não é saldo disponível nem crédito do banco.')
    expect(document.body.textContent).not.toContain('Limitada por')
    openDetails()
    expect(text(document.body)).toContain('Limitada por: Menor saldo diário até o fim da competência.')
    expect(text(document.body)).toMatch(/Reserva mínima\s*não configurada/); expect(text(document.body)).toContain('Compras no cartão afetam o banco só no vencimento da fatura')
  })

  it('margem segura indisponível: mensagem objetiva com o motivo, nunca zero', () => {
    const base = decision()
    draw({ ...base, safeMargin: { ...base.safeMargin, state: 'Unavailable', amount: null, bindingConstraint: null, constraints: [], reasons: ['RecoveryTargetUnavailable'] } })
    const margin = text(screen.getByLabelText('Margem segura para gastar à vista'))
    expect(margin).toContain('ainda não é possível calcular'); expect(margin).toContain('nenhuma meta é inventada'); expect(margin).not.toMatch(/R\$ 0,00/)
  })

  it('margem preliminar é marcada como preliminar e a ressalva fica na expansão', () => {
    const base = decision()
    draw({ ...base, safeMargin: { ...base.safeMargin, state: 'Preliminary', reasons: ['ProvisionalRecoveryTarget'] } })
    expect(text(screen.getByLabelText('Margem segura para gastar à vista'))).toContain('Preliminar')
    openDetails(); expect(text(document.body)).toContain('ainda é provisória')
  })

  it('a expansão começa fechada e reúne compromissos por natureza e vencimento, com os atalhos', () => {
    const onNavigate = draw(decision())
    expect(document.body.textContent).not.toContain('Por natureza')
    openDetails()
    expect(text(document.body)).toMatch(/Vencidos e não liquidados\s*R\$ 100,00/); expect(text(document.body)).toMatch(/A vencer até o fim da competência\s*R\$ 500,00/)
    expect(text(document.body)).toMatch(/Faturas de cartão a pagar\s*R\$ 300,00/)
    fireEvent.click(screen.getByRole('button', { name: 'Ver evolução na Agenda' })); fireEvent.click(screen.getAllByRole('button', { name: 'Ver contas' }).at(-1) as HTMLElement)
    expect(onNavigate.mock.calls.map(call => call[0])).toEqual(['calendar', 'accounts'])
  })

  it('o aviso de dados alterados durante a leitura fica sempre visível', () => {
    draw({ ...decision(), snapshotConsistent: false })
    expect(screen.getAllByRole('status').some(node => (node.textContent ?? '').includes('Os dados mudaram durante a leitura'))).toBe(true)
  })

  it('sem a análise do servidor: saldo calculado com o nome certo, sem inventar cobertura, e carregamento distinto de indisponível', () => {
    draw(null, { status: 'unavailable' })
    expect(text(card('Saldo calculado'))).toContain('não é o saldo informado pelo banco'); expect(text(card('Análise de cobertura indisponível'))).toContain('Ainda não é possível calcular')
    expect(screen.queryByRole('article', { name: 'Saldo bancário observado' })).toBeNull()
    cleanup()
    draw(null, { status: 'loading' })
    expect(text(card('Análise de cobertura indisponível'))).toContain('Carregando…'); expect(text(card('Análise de cobertura indisponível'))).not.toContain('não está disponível')
  })
})
