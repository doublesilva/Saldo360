import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { IncomeForecastSettings, IntegratedMonth, IntegratedProjection } from '../api/monthlyPlan'
import { IncomeForecastEditor } from './IncomeForecastEditor'
import { IntegratedProjectionView } from './IntegratedProjectionView'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')

const month = (competence: string, over: Partial<IntegratedMonth> = {}): IntegratedMonth => ({
  competence, periodKind: 'Future',
  income: { source: 'Forecast', workedCompetence: '2026-10', businessDays: 21, billableDays: 21, hoursPerDay: '8', hourlyRate: '100.00', expectedGross: '16800.00', paymentDate: '2026-11-10', realizedIncome: '0.00', adjusted: false },
  pj: { opening: '500.00', pendingIncome: '16800.00', pendingExpense: '2260.00', taxReserve: '1848.00', taxObligationRegistered: false, operationalReserve: '0.00', closing: '15040.00', available: '13192.00', suggestedTransfer: '13192.00' },
  transfers: { plannedPjToPf: '3000.00', realizedPjToPf: '0.00' },
  pf: { opening: '-13811.51', closing: '-14000.00', entries: '0.00', exits: '3177.78', result: '-3177.78', fixedPending: '3177.78', cardsOutstanding: '0.00', otherPending: '0.00', transfersIn: '3000.00' },
  overdraft: { limit: '16100.00', usedStart: '13811.51', usedEnd: '14000.00', available: '2100.00', excess: '0.00', recovery: '-188.49', target: '1000.00', targetState: 'Provisional', achievementPercent: '-18.8' },
  cards: { applicable: true, state: 'Complete', plannedState: 'Defined', target: '5000.00', targetSource: 'BudgetTarget', targetPartial: false, confirmed: '1000.00', pendingKnown: '2300.00', estimated: '0.00', projected: '3300.00',
    remaining: '1700.00', excessProjected: '0.00', excessConfirmed: '0.00', confirmedPercent: '20.0', projectedPercent: '66.0', financeCharges: '0.00', cards: [], payerAccountIds: [], reasons: [] },
  complete: false, quality: ['NoCardsRegistered'],
  ...over,
})
const projection = (months: IntegratedMonth[] = [month('2026-11'), month('2026-12')]): IntegratedProjection => ({
  incomeForecastActive: true, pjAccountId: 'pj', pjAccountName: 'Itaú PJ', pfAccountId: 'pf', pfAccountName: 'Itaú PF', recoveryOverPeriod: '-188.49', months,
})

describe('IntegratedProjectionView', () => {
  it('apresenta receita PJ, disponibilidade, repasse, conta PF e cheque especial vindos do backend', () => {
    render(<IntegratedProjectionView projection={projection()} />)
    const table = screen.getByRole('table')
    const row = (name: RegExp) => text(within(table).getByRole('row', { name }))
    expect(row(/Receita PJ · Receita esperada/)).toContain('R$ 16.800,00')
    expect(row(/Receita PJ · Dias faturáveis/)).toContain('21 de 21 úteis')
    expect(row(/Receita PJ · Recebimento previsto/)).toContain('10/11/2026')
    expect(row(/Disponibilidade PJ · Reserva de tributos/)).toContain('R$ 1.848,00')
    expect(row(/Disponibilidade PJ · Disponível para repasse/)).toContain('R$ 13.192,00')
    expect(row(/Disponibilidade PJ · Repasse sugerido \(não cadastrado\)/)).toContain('R$ 13.192,00')
    expect(row(/Transferências PJ → PF · Prevista/)).toContain('R$ 3.000,00')
    expect(row(/Conta PF · Saldo final projetado/)).toContain('-R$ 14.000,00')
    expect(row(/Cheque especial · Recuperação no mês/)).toContain('-R$ 188,49')
    expect(row(/Cheque especial · Meta/)).toContain('Meta provisória (calculada, não gravada)')
  })

  it('projeção parcial é dita como parcial, com as fontes ausentes, e não como fechamento integral', () => {
    render(<IntegratedProjectionView projection={projection([month('2026-11', { quality: ['NoCardsRegistered', 'IncomeForecastNotConfigured'] })])} />)
    expect(text(screen.getByRole('note'))).toContain('Projeção parcial')
    expect(text(screen.getByRole('note'))).toContain('não representa o fluxo financeiro integral')
    const missing = screen.getByRole('list', { name: 'Fontes ausentes da projeção' })
    expect(text(missing)).toContain('Nenhum cartão cadastrado')
    expect(text(missing)).toContain('sem previsão aprovada')
    expect(text(within(screen.getByRole('table')).getByRole('row', { name: /Qualidade · Cobertura da projeção/ }))).toContain('Parcial')
  })

  it('projeção completa não mostra aviso de parcial; tributo cadastrado explica a reserva não aplicada', () => {
    const complete = month('2026-11', { complete: true, quality: [], pj: { ...month('2026-11').pj, taxObligationRegistered: true, taxReserve: '0.00' } })
    render(<IntegratedProjectionView projection={projection([complete])} />)
    expect(screen.queryByRole('note')).toBeNull()
    expect(text(within(screen.getByRole('table')).getByRole('row', { name: /Reserva de tributos/ }))).toContain('reserva não aplicada')
  })

  it('meta única de cartões: separa realizado, parcelas futuras, projetado, saldo da meta e excesso', () => {
    const over = month('2026-12', { cards: { ...month('2026-12').cards, target: '2000.00', projected: '3300.00', remaining: '0.00', excessProjected: '1300.00' } })
    render(<IntegratedProjectionView projection={projection([month('2026-11'), over])} />)
    const row = (name: RegExp) => text(within(screen.getByRole('table')).getByRole('row', { name }))
    expect(row(/Meta de cartões · Meta mensal \(todos os cartões\)/)).toContain('R$ 5.000,00')
    expect(row(/Meta de cartões · Consumo realizado/)).toContain('R$ 1.000,00')
    expect(row(/Meta de cartões · Parcelas e compras futuras comprometidas/)).toContain('R$ 2.300,00')
    expect(row(/Meta de cartões · Consumo projetado/)).toContain('R$ 3.300,00')
    expect(row(/Meta de cartões · Saldo disponível da meta/)).toContain('R$ 1.700,00')
    expect(row(/Meta de cartões · Acima da meta/)).toContain('R$ 1.300,00')
    expect(text(screen.getByText(/A meta de cartões é meta de consumo/))).toContain('não é fatura prevista nem débito')
  })

  it('sem cartões vinculados o consumo não vira R$ 0,00 nem cobertura completa', () => {
    const none = month('2026-11', { cards: { ...month('2026-11').cards, applicable: false, state: 'Unavailable', confirmed: '0.00', pendingKnown: '0.00', estimated: '0.00', projected: '0.00', remaining: null, excessProjected: null, reasons: ['NoCards'] } })
    render(<IntegratedProjectionView projection={projection([none])} />)
    const table = screen.getByRole('table')
    expect(text(within(table).getByRole('row', { name: /Meta de cartões · Consumo projetado/ }))).toContain('Sem cartões vinculados')
    expect(text(within(table).getByRole('row', { name: /Meta de cartões · Consumo projetado/ }))).not.toContain('R$ 0,00')
    expect(text(within(table).getByRole('row', { name: /Meta de cartões · Meta mensal/ }))).toContain('R$ 5.000,00')
    expect(text(within(table).getByRole('row', { name: /Qualidade · Cobertura da projeção/ }))).toContain('Parcial')
  })
})

const settings = (over: Partial<IncomeForecastSettings> = {}): IncomeForecastSettings => ({
  exists: true, isActive: false, accountId: 'pj', accountName: 'Itaú PJ', hourlyRate: '100.00', hoursPerDay: '8', paymentDay: 10, paymentOffsetMonths: 1, taxReservePercent: '11.00', operationalReserve: '0.00',
  months: [{ workedCompetence: '2026-10', paymentDate: '2026-11-10', businessDays: 21, billableDays: 21, grossAmount: '16800.00', taxReserve: '1848.00', status: 'Forecast', realizedAmount: '0.00', daysAdjusted: false, amountAdjusted: false, dateAdjusted: false }],
  accounts: [{ id: 'pj', name: 'Itaú PJ', scope: 'PJ' }, { id: 'pf', name: 'Itaú PF', scope: 'PF' }], ...over,
})

const stubApi = (initial: IncomeForecastSettings, onWrite?: (method: string, path: string, body: unknown) => IncomeForecastSettings) => {
  const fetchMock = vi.fn((input: string, init: RequestInit = {}) => {
    const url = new URL(String(input), 'http://x'); const method = init.method ?? 'GET'
    if (url.pathname === '/auth/csrf') return json({ token: 'csrf' })
    if (method === 'GET') return json(initial)
    return json(onWrite?.(method, url.pathname, init.body ? JSON.parse(String(init.body)) : undefined) ?? initial)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}
const writes = (fetchMock: ReturnType<typeof stubApi>) => fetchMock.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method && (init as RequestInit).method !== 'GET')

const openEditor = async (onChanged = vi.fn()) => {
  render(<IncomeForecastEditor workspaceId="w" from="2026-11" onChanged={onChanged} />)
  const details = document.querySelector('details') as HTMLDetailsElement
  details.open = true
  fireEvent(details, new Event('toggle'))
  await screen.findByLabelText('Valor por hora (R$)')
  return onChanged
}

describe('IncomeForecastEditor', () => {
  it('só carrega ao abrir: fechado, não faz nenhuma chamada', () => {
    const fetchMock = stubApi(settings())
    render(<IncomeForecastEditor workspaceId="w" from="2026-11" onChanged={vi.fn()} />)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('rascunho inicial avisa que nada foi salvo; aprovar e salvar envia os parâmetros ao backend e recarrega a projeção', async () => {
    const draft = settings({ exists: false, months: [] })
    const fetchMock = stubApi(draft, () => settings({ isActive: true }))
    const onChanged = await openEditor()
    expect(text(screen.getByText(/Valores iniciais sugeridos/))).toContain('nada entra na projeção')

    fireEvent.click(screen.getByLabelText(/Aprovar esta previsão/))
    fireEvent.click(screen.getByRole('button', { name: 'Salvar previsão' }))

    await waitFor(() => expect(onChanged).toHaveBeenCalled())
    const [call] = writes(fetchMock)
    expect(String(call[0])).toContain('/monthly-plan/income-forecast')
    expect((call[1] as RequestInit).method).toBe('PUT')
    expect(JSON.parse(String((call[1] as RequestInit).body))).toEqual({ accountId: 'pj', hourlyRate: '100.00', hoursPerDay: '8', paymentDay: 10, paymentOffsetMonths: 1, taxReservePercent: '11.00', operationalReserve: '0.00', isActive: true })
  })

  it('ajustar dias, descartar e restaurar chamam o backend e preservam os ajustes já existentes', async () => {
    const adjusted = settings({ months: [{ workedCompetence: '2026-10', paymentDate: '2026-11-15', businessDays: 21, billableDays: 21, grossAmount: '9999.90', taxReserve: '1099.99', status: 'Forecast', realizedAmount: '0.00', daysAdjusted: false, amountAdjusted: true, dateAdjusted: true }] })
    const fetchMock = stubApi(adjusted)
    await openEditor()

    fireEvent.change(screen.getByLabelText(/Dias faturáveis de/), { target: { value: '10' } })
    fireEvent.click(screen.getByRole('button', { name: 'Aplicar dias' }))
    await waitFor(() => expect(writes(fetchMock)).toHaveLength(1))
    expect(JSON.parse(String((writes(fetchMock)[0][1] as RequestInit).body))).toEqual({ billableDays: 10, expectedAmount: '9999.90', paymentDate: '2026-11-15', isDismissed: false })

    fireEvent.click(screen.getByRole('button', { name: 'Descartar' }))
    await waitFor(() => expect(writes(fetchMock)).toHaveLength(2))
    expect(JSON.parse(String((writes(fetchMock)[1][1] as RequestInit).body))).toMatchObject({ isDismissed: true, expectedAmount: '9999.90' })

    fireEvent.click(screen.getByRole('button', { name: 'Restaurar cálculo' }))
    await waitFor(() => expect(writes(fetchMock)).toHaveLength(3))
    expect((writes(fetchMock)[2][1] as RequestInit).method).toBe('DELETE')
  })

  it('erro de validação do backend aparece sem fechar o formulário', async () => {
    const fetchMock = vi.fn((input: string, init: RequestInit = {}) => {
      const url = new URL(String(input), 'http://x')
      if (url.pathname === '/auth/csrf') return json({ token: 'csrf' })
      if ((init.method ?? 'GET') === 'GET') return json(settings())
      return json({ title: 'Um ou mais erros de validação.', errors: { hourlyRate: ['Informe o valor por hora, maior que zero.'] } }, 400)
    })
    vi.stubGlobal('fetch', fetchMock)
    await openEditor()
    fireEvent.click(screen.getByRole('button', { name: 'Salvar previsão' }))
    expect(await screen.findByRole('alert')).toBeTruthy()
    expect(screen.getByLabelText('Valor por hora (R$)')).toBeTruthy()
  })
})
