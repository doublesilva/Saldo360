import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialDecision } from '../../api/overview'
import { accountCoverage, decision } from './decisionFixtures'
import { OverviewDecisionSection } from './OverviewDecision'

afterEach(cleanup)

const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
const draw = (value: FinancialDecision, onNavigate = vi.fn()) => { render(<OverviewDecisionSection decision={value} onNavigate={onNavigate} />); return onNavigate }
const card = (name: string) => screen.getByRole('article', { name })

describe('Visão Geral — segurança financeira (T11.1)', () => {
  it('mostra o contexto da análise: competência, conta e tipo, data da posição bancária e última sincronização', () => {
    draw(decision())
    const context = screen.getByLabelText('Contexto da análise')
    expect(text(context)).toMatch(/Competência\s*Novembro de 2026\s*Mês corrente/)
    expect(text(context)).toContain('Itaú PF · PF')
    expect(text(context)).toContain('em 04/11/2026')
    expect(text(context)).toMatch(/Última sincronização\s*04\/11\/2026 às/)
  })

  it('indica a competência vigente quando a selecionada é outra e o consolidado em vez de uma conta', () => {
    const base = decision()
    draw({ ...base, context: { ...base.context, competence: '2026-12', period: 'Future', scopeKind: 'all', accountId: null, accountName: null, accountScope: null, bankPositionDate: null, lastSyncedAt: null } })
    const context = screen.getByLabelText('Contexto da análise')
    expect(text(context)).toContain('Mês futuro'); expect(text(context)).toContain('Vigente: Novembro de 2026')
    expect(text(context)).toContain('Todas as contas (consolidado)'); expect(text(context)).toContain('sem posição informada pelo banco'); expect(text(context)).toContain('sem sincronização')
  })

  it('mostra o saldo bancário observado e o cheque especial utilizado, ou diz que não há posição', () => {
    draw(decision())
    expect(text(card('Posição bancária observada'))).toMatch(/-R\$ 1\.000,00/)
    expect(text(card('Posição bancária observada'))).toMatch(/Cheque especial utilizado\s*R\$ 1\.000,00/)
    cleanup()
    const base = decision()
    draw({ ...base, context: { ...base.context, bankBalance: null, overdraftUsed: null } })
    expect(text(card('Posição bancária observada'))).toContain('Sem posição do banco')
  })

  it('compromissos: total do servidor, vencidos separados, natureza e vencimento, e atalho para a Agenda no mês analisado', () => {
    const onNavigate = draw(decision())
    const commitments = card('Compromissos restantes')
    expect(text(commitments)).toMatch(/R\$ 600,00/)
    expect(text(commitments)).toMatch(/Vencidos e não liquidados\s*R\$ 100,00/)
    expect(text(commitments)).toMatch(/A vencer até o fim da competência\s*R\$ 500,00/)
    expect(text(commitments)).toMatch(/Faturas de cartão a pagar\s*R\$ 300,00/)
    fireEvent.click(within(commitments).getByRole('button', { name: 'Ver na Agenda' }))
    expect(onNavigate).toHaveBeenCalledWith('calendar', { month: '2026-11' })
  })

  it('competência encerrada: não afirma que algo estava pendente na época e mostra o pago no mês', () => {
    const base = decision()
    draw({ ...base, context: { ...base.context, competence: '2026-10', period: 'Closed' }, commitments: { ...base.commitments, period: 'Closed', paidInMonth: '1234.50', overdue: '0.00' } })
    const commitments = card('Compromissos restantes')
    expect(text(commitments)).toContain('Ainda em aberto hoje, com vencimento no mês')
    expect(text(commitments)).not.toContain('Vencidos e não liquidados')
    expect(text(commitments)).toMatch(/Débitos efetivos no mês\s*R\$ 1\.234,50/)
  })

  it('cobertura: cada estado tem mensagem objetiva e etiqueta em texto (não depende só de cor)', () => {
    const cases: [FinancialDecision['coverage']['state'], string, string][] = [
      ['Covered', 'Seus compromissos estão cobertos.', 'Coberto'],
      ['CoveredWithCredit', 'Os pagamentos dependem de utilização do cheque especial.', 'Só com cheque especial'],
      ['Insufficient', 'Há risco de insuficiência em 22/11/2026.', 'Insuficiente'],
      ['Unavailable', 'Não é possível confirmar a cobertura com os dados disponíveis.', 'Indisponível'],
    ]
    for (const [state, message, tag] of cases) {
      const base = decision()
      draw({ ...base, coverage: { ...base.coverage, state, firstBeyondLimitDate: state === 'Insufficient' ? '2026-11-22' : null, deficit: state === 'Insufficient' ? '500.00' : null, unavailableReason: state === 'Unavailable' ? 'OpeningBalanceNotConfigured' : null } })
      const coverage = card('Cobertura dos compromissos')
      expect(text(coverage)).toContain(message); expect(text(coverage)).toContain(tag)
      cleanup()
    }
  })

  it('cobertura: saldo mínimo, déficit, fechamento projetado e recebimentos, com o estimado marcado como não garantido', () => {
    const base = decision()
    draw({ ...base, coverage: { ...base.coverage, state: 'Insufficient', minimumBalance: '-500.00', firstNegativeDate: '2026-11-22', firstBeyondLimitDate: '2026-11-22', deficit: '500.00', caveats: ['DependsOnInferredIncome'] } })
    const coverage = card('Cobertura dos compromissos')
    expect(text(coverage)).toMatch(/Saldo mínimo projetado\s*-R\$ 500,00/)
    expect(text(coverage)).toMatch(/Déficit\s*R\$ 500,00/)
    expect(text(coverage)).toMatch(/Fechamento projetado\s*R\$ 900,00/)
    expect(text(coverage)).toContain('O resultado depende de receitas estimadas')
    expect(text(coverage)).toContain('(estimada, não garantida)')
  })

  it('consolidado: mostra o risco de cada conta e diz que os saldos não se compensam', () => {
    const base = decision()
    draw({ ...base, context: { ...base.context, scopeKind: 'all', accountId: null }, coverage: { ...base.coverage, state: 'Insufficient', projectedClosing: null, firstBeyondLimitDate: '2026-11-22',
      accounts: [accountCoverage({ accountId: 'pj', name: 'Inter PJ', scope: 'PJ' }), accountCoverage({ state: 'Insufficient', firstBeyondLimitDate: '2026-11-22' })] } })
    const coverage = card('Cobertura dos compromissos')
    expect(text(coverage)).toMatch(/Inter PJ · PJ\s*Coberto/); expect(text(coverage)).toMatch(/Itaú PF · PF\s*Insuficiente 22\/11/)
    expect(text(coverage)).toContain('Os saldos de contas diferentes não se compensam.')
    expect(text(coverage)).not.toContain('Fechamento projetado')
  })

  it('margem segura calculada: valor do servidor, a restrição mais restritiva e a ressalva de que é gasto à vista', () => {
    draw(decision())
    const margin = card('Margem segura para gastar')
    expect(text(margin)).toMatch(/R\$ 400,00/); expect(text(margin)).toContain('Calculada')
    expect(text(margin)).toContain('Limitada por: Menor saldo diário até o fim da competência.')
    expect(text(margin)).toContain('Compras no cartão afetam o banco só no vencimento da fatura')
    expect(text(margin)).toContain('Receitas estimadas não entram no cálculo.')
    expect(text(margin)).toMatch(/Reserva mínima\s*não configurada/)
  })

  it('margem indisponível: mensagem objetiva e o motivo, nunca zero', () => {
    const base = decision()
    draw({ ...base, safeMargin: { ...base.safeMargin, state: 'Unavailable', amount: null, bindingConstraint: null, constraints: [], reasons: ['RecoveryTargetUnavailable'] } })
    const margin = card('Margem segura para gastar')
    expect(text(margin)).toContain('Não é possível calcular uma margem segura com os dados disponíveis.')
    expect(text(margin)).toContain('nenhuma meta é inventada'); expect(text(margin)).not.toMatch(/R\$ 0,00/)
  })

  it('margem preliminar: marcada como preliminar com as ressalvas', () => {
    const base = decision()
    draw({ ...base, safeMargin: { ...base.safeMargin, state: 'Preliminary', reasons: ['ProvisionalRecoveryTarget', 'CardCoverageIncomplete'] } })
    const margin = card('Margem segura para gastar')
    expect(text(margin)).toContain('Preliminar'); expect(text(margin)).toContain('ainda é provisória'); expect(text(margin)).toContain('sem conta pagadora')
  })

  it('meta de recuperação: meta original, efetiva, projetada, diferença e utilização esperada; fixada e nunca recalculada', () => {
    const onNavigate = draw(decision())
    const recovery = card('Meta de recuperação')
    expect(text(recovery)).toContain('Fixada')
    expect(text(recovery)).toMatch(/Meta original\s*R\$ 2\.500,00/); expect(text(recovery)).toMatch(/Cheque especial na abertura\s*R\$ 15\.000,00/)
    expect(text(recovery)).toMatch(/Recuperação projetada\s*R\$ 1\.000,00/); expect(text(recovery)).toMatch(/Diferença para a meta\s*-R\$ 1\.500,00 · abaixo da meta/)
    expect(text(recovery)).toMatch(/Utilização esperada no fechamento\s*R\$ 14\.000,00/); expect(text(recovery)).toContain('Não é recalculada.')
    fireEvent.click(within(recovery).getByRole('button', { name: 'Abrir a Projeção financeira' }))
    expect(onNavigate).toHaveBeenCalledWith('planning', { planningTab: 'projection' })
  })

  it('meta: explica quando não se aplica (conta não abriu negativa, consolidado)', () => {
    const base = decision()
    draw({ ...base, recovery: { ...base.recovery, state: 'NotApplicable', reason: 'AccountDidNotOpenNegative', openingUsed: null, target: null, gapToTarget: null } })
    expect(text(card('Meta de recuperação'))).toContain('A conta não abriu a competência usando o cheque especial')
    cleanup()
    draw({ ...base, recovery: { ...base.recovery, state: 'NotApplicable', reason: 'ConsolidatedScope', openingUsed: null, target: null, gapToTarget: null } })
    expect(text(card('Meta de recuperação'))).toContain('Selecione uma conta')
  })

  it('cada indicador tem caminho para o detalhe (Agenda, Contas, Planejamento) e avisa quando os dados mudaram durante a leitura', () => {
    const onNavigate = draw({ ...decision(), snapshotConsistent: false })
    expect(screen.getAllByRole('status').some(node => (node.textContent ?? '').includes('Os dados mudaram durante a leitura'))).toBe(true)
    fireEvent.click(within(card('Posição bancária observada')).getByRole('button', { name: 'Ver contas' }))
    fireEvent.click(within(card('Margem segura para gastar')).getByRole('button', { name: 'Ver contas' }))
    fireEvent.click(within(card('Cobertura dos compromissos')).getByRole('button', { name: 'Ver evolução na Agenda' }))
    expect(onNavigate.mock.calls.map(call => call[0])).toEqual(['accounts', 'accounts', 'calendar'])
  })
})
