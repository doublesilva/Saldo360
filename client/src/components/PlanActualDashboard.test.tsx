import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { PlanActualDashboard as PlanData, PlanOverdraft, PlanSync } from '../api/planActual'
import { PlanActualDashboard } from './PlanActualDashboard'
import { planActual, planActualIndicator } from './planActualFixtures'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')

const stub = (data: Partial<PlanData> | ((url: string) => PlanData) = {}, extra?: (url: string, init?: RequestInit) => Promise<Response> | undefined) => {
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    const handled = extra?.(url, init)
    if (handled) return handled
    if (url === '/auth/csrf') return json({ token: 'csrf' })
    return json(typeof data === 'function' ? data(url) : { ...planActual, ...data })
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}
const planCalls = (fetchMock: ReturnType<typeof stub>) => fetchMock.mock.calls.map(call => new URL(String(call[0]), 'http://x')).filter(url => url.pathname.endsWith('/plan-actual'))

const overdraft = (overrides: Partial<PlanOverdraft> = {}): PlanOverdraft => ({
  eligible: true, state: 'Complete', usedAtStart: '15000.00', usedLatest: '13811.51', effectiveRecovery: '1188.49', projectedUsedAtEnd: '10000.00', projectedRecovery: '5000.00',
  projectedRecoveryFromNow: '3811.51', contractedLimit: '16100.00', periodKind: 'Current', reasons: [],
  accounts: [{
    accountId: 'pf', name: 'Conta A', state: 'Complete', contractedLimit: '16100.00', usedLatest: '13811.51', observedAt: '2026-10-28T22:00:00Z', observedAsOf: '2026-10-28', usedAtStart: '15000.00',
    startAsOf: '2026-09-30', startApproximate: false, effectiveRecovery: '1188.49', direction: 'Recovered', fullyRecovered: false, projectedUsedAtEnd: '10000.00', projectedRecovery: '5000.00',
    projectedRecoveryFromNow: '3811.51', projectedBalanceAtEnd: '-10000.00', exceedsLimit: false, reasons: [],
  }],
  ...overrides,
})

describe('PlanActualDashboard', () => {
  it('abre na conta principal do mês dos filtros e mostra planejado, projetado, realizado, diferença e percentual', async () => {
    const fetchMock = stub()
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const income = await screen.findByRole('article', { name: 'Receitas: planejado, projetado e recebido' })
    expect(text(within(income).getByText('Recebido', { selector: 'small' }).nextElementSibling)).toBe('R$ 450,00')
    expect(text(within(income).getByText('Planejado').nextElementSibling)).toBe('R$ 2.500,00')
    expect(text(within(income).getByText('Projetado').nextElementSibling)).toBe('R$ 2.450,00')
    expect(text(within(income).getByText('Diferença (real. − plan.)').nextElementSibling)).toBe('-R$ 2.050,00')
    expect(text(within(income).getByText('Executado').nextElementSibling)).toBe('18,0%')
    expect(within(income).getByRole('progressbar', { name: 'Percentual executado' }).getAttribute('aria-valuenow')).toBe('18')
    const query = planCalls(fetchMock)[0].searchParams
    expect([query.get('scope'), query.get('competence')]).toEqual(['primary', '2026-10'])
    expect((screen.getByLabelText('Escopo do painel') as HTMLSelectElement).value).toBe('primary')
    expect(screen.getByText(/Conta principal:/).textContent).toContain('Conta A')
  })

  it('troca de conta sem recarregar a página, lista PF e PJ e preserva o mês quando a competência muda', async () => {
    const fetchMock = stub()
    const { rerender } = render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e recebido' })
    const select = screen.getByLabelText('Escopo do painel') as HTMLSelectElement
    expect(Array.from(select.options).map(option => option.textContent)).toEqual(['Conta principal · Conta A', 'Todas as contas', 'Conta A · PF', 'Conta B · PJ'])

    fireEvent.change(select, { target: { value: 'all' } })
    await waitFor(() => expect(planCalls(fetchMock).at(-1)!.searchParams.get('scope')).toBe('all'))
    fireEvent.change(select, { target: { value: 'account:pj' } })
    await waitFor(() => expect(planCalls(fetchMock).at(-1)!.searchParams.get('accountId')).toBe('pj'))
    rerender(<PlanActualDashboard workspaceId="w" competence="2026-09" />)

    await waitFor(() => expect(planCalls(fetchMock).at(-1)!.searchParams.get('competence')).toBe('2026-09'))
    const last = planCalls(fetchMock).at(-1)!.searchParams
    expect([last.get('scope'), last.get('accountId')]).toEqual(['account', 'pj']) // o escopo escolhido sobrevive à troca de mês
    expect((screen.getByLabelText('Escopo do painel') as HTMLSelectElement).value).toBe('account:pj')
  })

  it('sem conta principal avisa que exibe todas as contas, e o consolidado explica que não conta transferências', async () => {
    stub({ scope: { ...planActual.scope, requested: 'primary', kind: 'all', accountId: null, accountName: null, primaryAccountId: null, primaryAccountName: null, primaryMissing: true } })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    expect(await screen.findByText(/Nenhuma conta principal definida/)).toBeTruthy()
    expect(screen.getByText(/sem contar transferências internas/)).toBeTruthy()
    expect(Array.from((screen.getByLabelText('Escopo do painel') as HTMLSelectElement).options)[0].textContent).toBe('Conta principal')
  })

  it('sem planejamento mostra "Não definido", nunca um zero, e continua mostrando o realizado', async () => {
    stub({ income: planActualIndicator({ plannedState: 'NotDefined', planned: null, realizedDifference: null, projectedDifference: null, executionPercent: null, projectedPercent: null, plannedSource: null, realized: '321.45', projected: '321.45', pending: '0.00', reasons: ['PlanNotDefined'] }) })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const income = await screen.findByRole('article', { name: 'Receitas: planejado, projetado e recebido' })
    expect(text(within(income).getByText('Planejado').nextElementSibling)).toBe('Não definido')
    expect(text(within(income).getByText('Recebido', { selector: 'small' }).nextElementSibling)).toBe('R$ 321,45')
    expect(within(income).queryByText('Executado')).toBeNull()
    expect(within(income).queryByRole('progressbar')).toBeNull()
    expect(within(income).queryByText(/Diferença/)).toBeNull()
  })

  it('alterna entre consumo e fluxo de caixa sem nova consulta, cada perspectiva com os dois valores na mesma base', async () => {
    const fetchMock = stub()
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    const expenses = await screen.findByRole('article', { name: 'Despesas: planejado, projetado e efetivo' })

    // Consumo (padrão): orçamento por categoria não é atribuível a uma conta.
    expect(text(within(expenses).getByText('Efetivo', { selector: 'small' }).nextElementSibling)).toBe('R$ 1.200,00')
    expect(text(within(expenses).getByText('Planejado').nextElementSibling)).toBe('Não definido')
    expect(within(expenses).getByText(/escolha “Todas as contas”/)).toBeTruthy()
    const callsBefore = planCalls(fetchMock).length

    fireEvent.click(within(expenses).getByRole('radio', { name: 'Fluxo de caixa' }))

    expect(text(within(expenses).getByText('Efetivo', { selector: 'small' }).nextElementSibling)).toBe('R$ 700,00')
    expect(text(within(expenses).getByText('Planejado').nextElementSibling)).toBe('R$ 900,00')
    expect(within(expenses).getByRole('radio', { name: 'Fluxo de caixa' }).getAttribute('aria-checked')).toBe('true')
    expect(planCalls(fetchMock)).toHaveLength(callsBefore)
    expect(within(expenses).getByText(/Pagamento de fatura não entra aqui|incluindo pagamentos de fatura/)).toBeTruthy()
  })

  it('mostra a recuperação do cheque especial só quando elegível, com início, utilização atual e projeção', async () => {
    stub({ overdraft: overdraft() })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const recovery = await screen.findByRole('article', { name: 'Recuperação do cheque especial' })
    expect(text(within(recovery).getByText('Recuperação efetiva até a última sincronização').nextElementSibling)).toBe('R$ 1.188,49')
    expect(text(within(recovery).getByText('Utilizado no início').nextElementSibling)).toBe('R$ 15.000,00')
    expect(text(within(recovery).getByText('Recuperação projetada até o fim do mês').nextElementSibling)).toBe('R$ 5.000,00')
    expect(within(recovery).getByText('Recuperação')).toBeTruthy()
    expect(within(recovery).getByText(/não compõe saldo nem patrimônio/)).toBeTruthy()
    expect(within(recovery).getByText(/não é tratada como meta descumprida/)).toBeTruthy()
  })

  it('não mostra o card do cheque especial sem limite contratado ou utilizado', async () => {
    stub()
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e recebido' })

    expect(screen.queryByRole('article', { name: 'Recuperação do cheque especial' })).toBeNull()
  })

  it('com histórico insuficiente preserva o valor atual do limite e não inventa recuperação', async () => {
    stub({ overdraft: overdraft({ effectiveRecovery: null, usedAtStart: null, projectedRecovery: null, state: 'InsufficientHistory', reasons: ['InsufficientHistory'] }) })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const recovery = await screen.findByRole('article', { name: 'Recuperação do cheque especial' })
    expect(within(recovery).getByText('Histórico insuficiente', { selector: 'strong' })).toBeTruthy()
    expect(text(within(recovery).getByText('Utilizado hoje').nextElementSibling)).toBe('R$ 13.811,51')
    expect(within(recovery).queryByText('Utilizado no início')).toBeNull()
    expect(within(recovery).getByText(/não há observação do saldo no início do mês/)).toBeTruthy()
  })

  it('sinaliza aumento do endividamento, limite totalmente recuperado e projeção acima do limite', async () => {
    const base = overdraft().accounts[0]
    const { unmount } = render(<span />)
    unmount()
    stub({ overdraft: overdraft({ effectiveRecovery: '-300.00', accounts: [{ ...base, effectiveRecovery: '-300.00', direction: 'Increased', exceedsLimit: true }] }) })
    const view = render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    const increase = await screen.findByRole('article', { name: 'Recuperação do cheque especial' })
    expect(within(increase).getByText('Aumento do endividamento')).toBeTruthy()
    expect(within(increase).getByText(/ultrapassa o limite contratado/)).toBeTruthy()
    view.unmount()

    stub({ overdraft: overdraft({ usedLatest: '0.00', accounts: [{ ...base, usedLatest: '0.00', fullyRecovered: true }] }) })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    const recovered = await screen.findByRole('article', { name: 'Recuperação do cheque especial' })
    expect(within(recovered).getByText(/Limite totalmente recuperado: nenhuma recuperação adicional é projetada/)).toBeTruthy()
  })

  it('compara a meta dos cartões com o consumo confirmado, pendente e projetado, e destaca o excesso', async () => {
    stub({ cards: { ...planActual.cards, confirmed: '4300.00', pendingKnown: '500.00', projected: '5700.00', remaining: '0.00', excessProjected: '700.00', confirmedPercent: '86.0', projectedPercent: '114.0', financeCharges: '42.50' } })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const cards = await screen.findByRole('article', { name: 'Meta dos cartões × consumo' })
    expect(text(within(cards).getByText('Consumo confirmado').nextElementSibling)).toBe('R$ 4.300,00')
    expect(text(within(cards).getByText('Meta mensal').nextElementSibling)).toBe('R$ 5.000,00')
    expect(text(within(cards).getByText('Compras pendentes conhecidas').nextElementSibling)).toBe('R$ 500,00')
    expect(text(within(cards).getByText('Projeção atualizada').nextElementSibling)).toBe('R$ 5.700,00')
    expect(text(within(cards).getByText('Excesso projetado').nextElementSibling)).toBe('R$ 700,00')
    expect(text(within(cards).getByText('Consumido (projetado)').nextElementSibling)).toBe('114,0%')
    expect(within(cards).getByText(/Inclui R\$ 42,50 de encargos/)).toBeTruthy()
  })

  it('define a meta dos cartões da conta pagadora pelo backend, com CSRF e a competência do filtro', async () => {
    let target: unknown
    const fetchMock = stub({ cards: { ...planActual.cards, plannedState: 'NotDefined', target: null, targetSource: null, remaining: null, excessProjected: null, excessConfirmed: null, confirmedPercent: null, projectedPercent: null } },
      (url, init) => url.endsWith('/financial-dashboard/card-target') && init?.method === 'PUT' ? (target = JSON.parse(String(init.body)), json({ accountId: 'pf', effectiveFrom: '2026-10', amount: '5000.00' })) : undefined)
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    const cards = await screen.findByRole('article', { name: 'Meta dos cartões × consumo' })
    expect(text(within(cards).getByText('Meta mensal').nextElementSibling)).toBe('Não definido')

    fireEvent.click(within(cards).getByRole('button', { name: 'Definir meta' }))
    fireEvent.change(within(cards).getByLabelText('Meta mensal dos cartões'), { target: { value: '5000,00' } })
    fireEvent.click(within(cards).getByRole('button', { name: 'Salvar meta' }))

    await waitFor(() => expect(target).toEqual({ accountId: 'pf', competence: '2026-10', amount: '5000.00' }))
    const put = fetchMock.mock.calls.find(call => call[1]?.method === 'PUT')!
    expect((put[1]!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    // Depois de salvar, o painel recarrega do backend.
    await waitFor(() => expect(planCalls(fetchMock).length).toBeGreaterThan(1))
  })

  it('mostra o erro do backend ao salvar a meta e em "Todas as contas" pede para escolher uma conta', async () => {
    stub({}, (url, init) => url.endsWith('/card-target') && init?.method === 'PUT' ? json({ title: 'Bad', status: 400, detail: 'A meta pertence a uma conta bancária ativa.' }, 400) : undefined)
    const view = render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    const cards = await screen.findByRole('article', { name: 'Meta dos cartões × consumo' })
    fireEvent.click(within(cards).getByRole('button', { name: 'Alterar meta' }))
    fireEvent.click(within(cards).getByRole('button', { name: 'Salvar meta' }))
    expect((await within(cards).findByRole('alert')).textContent).toBeTruthy()
    view.unmount()

    stub({ scope: { ...planActual.scope, kind: 'all', accountId: null, accountName: null } })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    expect(await screen.findByText(/Selecione uma conta para definir a meta/)).toBeTruthy()
  })

  it('cartões sem conta pagadora no escopo ficam fora do card e explicam por quê', async () => {
    stub({ cards: { ...planActual.cards, applicable: false, state: 'Unavailable', cards: [] } })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    expect(await screen.findByText('Nenhum cartão é pago por esta conta.')).toBeTruthy()
  })

  it('mostra a situação do mês e diferencia saldo indisponível de saldo zero, com o parcial à parte', async () => {
    stub({ situation: { ...planActual.situation, state: 'Partial', realizedBalance: null, partialRealizedBalance: '500.00', consideredAccounts: 1, unavailableAccounts: 1, projectedClosing: null, minimumProjected: null, minimumProjectedDate: null, reasons: ['ProjectionUnavailable:BeforeOpeningBalanceDate'] } })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const situation = (await screen.findByRole('heading', { name: 'Situação financeira do mês' })).closest('section')!
    expect(text(within(situation).getByText('Saldo realizado atual').nextElementSibling)).toBe('Indisponível (parcial: R$ 500,00 em 1 conta(s))')
    expect(text(within(situation).getByText('Saldo projetado no fechamento').nextElementSibling)).toBe('Indisponível')
    expect(within(situation).getByText(/1 conta\(s\) sem saldo calculável/)).toBeTruthy()
  })

  it('mostra o saldo zero como R$ 0,00 e a data de risco de saldo negativo', async () => {
    stub({ situation: { ...planActual.situation, realizedBalance: '0.00', negativeRiskDate: '2026-10-20', projectedClosing: '-400.00' } })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const situation = (await screen.findByRole('heading', { name: 'Situação financeira do mês' })).closest('section')!
    expect(text(within(situation).getByText('Saldo realizado atual').nextElementSibling)).toBe('R$ 0,00')
    expect(text(within(situation).getByText('Risco de saldo negativo').nextElementSibling)).toBe('a partir de 20/10/2026')
  })

  it('indica movimentações em revisão, que afetam a confiança, e leva à tela de revisão sem esconder o realizado', async () => {
    const navigate = vi.fn()
    stub({ coverage: { ...planActual.coverage, complete: false, reviewCount: 3, reviewInMonth: 2, reviewAffectsConfidence: true, reasons: ['MovementsInReview'] },
      income: planActualIndicator({ state: 'Partial', reasons: ['MovementsInReview'] }) })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" onNavigate={navigate} />)

    const notice = await screen.findByRole('status', { name: 'Cobertura e revisão' })
    expect(notice.textContent).toContain('3 movimentação(ões) em revisão, 2 neste mês')
    expect(notice.textContent).toContain('reduzem a confiança')
    fireEvent.click(within(notice).getByRole('button', { name: 'Revisar movimentações' }))
    expect(navigate).toHaveBeenCalledWith('accounts')
    expect(text(within(screen.getByRole('article', { name: 'Receitas: planejado, projetado e recebido' })).getByText('Recebido', { selector: 'small' }).nextElementSibling)).toBe('R$ 450,00')
  })

  it('sem revisões nem ressalvas não mostra aviso de cobertura', async () => {
    stub()
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e recebido' })

    expect(screen.queryByRole('status', { name: 'Cobertura e revisão' })).toBeNull()
  })

  const sync = (overrides: Partial<PlanSync> = {}): PlanSync => ({
    autoEnabled: false, autoIntervalHours: 24, oldestSuccessAt: '2026-10-16T12:00:00Z', latestSuccessAt: '2026-10-16T12:00:00Z', sameMoment: true, stale: false, state: 'Completed', nextAttemptAt: null,
    connections: [{ connectionId: 'c1', label: 'Itaú', status: 'Completed', lastSuccessAt: '2026-10-16T12:00:00Z', lastAttemptAt: '2026-10-16T12:00:00Z', nextAttemptAt: null, consentExpiringSoon: false }], ...overrides,
  })

  it('com a sincronização automática desligada diz isso, não promete tentativa e mantém "Sincronizar agora"', async () => {
    stub({ sync: sync() })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const strip = await screen.findByLabelText('Sincronização bancária')
    expect(strip.textContent).toContain('Última sincronização bem-sucedida: 16/10/2026 09:00')
    expect(strip.textContent).toContain('Automática desligada.')
    expect(strip.textContent).not.toContain('Próxima tentativa')
    expect(within(strip).getByRole('button', { name: 'Sincronizar agora' })).toBeTruthy()
  })

  it('com a automática ligada mostra a próxima tentativa, e a defasagem vira alerta', async () => {
    stub({ sync: sync({ autoEnabled: true, nextAttemptAt: '2026-10-17T12:00:00Z', stale: true }) })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const strip = await screen.findByLabelText('Sincronização bancária')
    expect(strip.textContent).toContain('Próxima tentativa prevista: 17/10/2026 09:00')
    expect(strip.textContent).not.toContain('Automática desligada')
    expect(within(strip).getByText(/Atualização desatualizada/)).toBeTruthy()
  })

  it('em "Todas as contas" não resume momentos diferentes numa data só e oferece uma sincronização por conexão', async () => {
    stub({ sync: sync({
      sameMoment: false, oldestSuccessAt: '2026-10-15T12:00:00Z', latestSuccessAt: '2026-10-16T12:00:00Z',
      connections: [
        { connectionId: 'c1', label: 'Itaú PF', status: 'Completed', lastSuccessAt: '2026-10-16T12:00:00Z', lastAttemptAt: null, nextAttemptAt: null, consentExpiringSoon: false },
        { connectionId: 'c2', label: 'Nubank PJ', status: 'Completed', lastSuccessAt: '2026-10-15T12:00:00Z', lastAttemptAt: null, nextAttemptAt: null, consentExpiringSoon: false },
      ],
    }) })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    const strip = await screen.findByLabelText('Sincronização bancária')
    expect(strip.textContent).toContain('Sincronização mais antiga: 15/10/2026 09:00 · mais recente: 16/10/2026 09:00')
    expect(within(strip).getByText('Nubank PJ: 15/10/2026 09:00')).toBeTruthy()
    expect(within(strip).getAllByRole('button').map(button => button.textContent)).toEqual(['Sincronizar agora · Itaú PF', 'Sincronizar agora · Nubank PJ'])
  })

  it('"Sincronizar agora" usa a sincronização existente da conexão e recarrega o painel', async () => {
    const fetchMock = stub({ sync: sync() }, (url, init) => url.endsWith('/connections/c1/sync') && init?.method === 'POST' ? json({ id: 'r1', status: 'Completed' }) : undefined)
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    const strip = await screen.findByLabelText('Sincronização bancária')

    fireEvent.click(within(strip).getByRole('button', { name: 'Sincronizar agora' }))

    await waitFor(() => expect(fetchMock.mock.calls.some(call => String(call[0]) === '/api/financial-workspaces/w/pluggy-poc/connections/c1/sync')).toBe(true))
    await waitFor(() => expect(planCalls(fetchMock).length).toBeGreaterThan(1))
  })

  it('sem conexões integradas não há bloco de sincronização', async () => {
    stub({ sync: null })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)
    await screen.findByRole('article', { name: 'Receitas: planejado, projetado e recebido' })

    expect(screen.queryByLabelText('Sincronização bancária')).toBeNull()
  })

  it('mostra o estado de carregamento, o erro com nova tentativa e rejeita uma resposta fora do contrato', async () => {
    let attempts = 0
    stub((): PlanData => { throw new Error('unused') }, (url) => {
      if (!url.includes('/plan-actual')) return undefined
      attempts++
      return attempts === 1 ? json({ title: 'Erro', status: 503 }, 503) : attempts === 2 ? json({ unexpected: true }) : json(planActual)
    })
    render(<PlanActualDashboard workspaceId="w" competence="2026-10" />)

    expect(screen.getByText('Carregando o planejado × realizado…')).toBeTruthy()
    await screen.findByRole('alert')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    await waitFor(() => expect(attempts).toBe(2))
    // A resposta fora do contrato vira erro (com nova tentativa), nunca um painel quebrado.
    expect(await screen.findByRole('button', { name: 'Tentar novamente' })).toBeTruthy()
    expect(screen.queryByRole('article', { name: 'Receitas: planejado, projetado e recebido' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByRole('article', { name: 'Receitas: planejado, projetado e recebido' })).toBeTruthy()
  })
})
