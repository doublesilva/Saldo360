import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialProjection, FoodProvision } from '../api/projection'
import { PlanningHub } from './PlanningHub'
import { foodProvision, projection } from './projectionFixtures'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
const categories = [
  { id: 'cat-f', name: 'Alimentação', type: 'Expense', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
  { id: 'cat-m', name: 'Mercado', type: 'Expense', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
  { id: 'cat-r', name: 'Salário', type: 'Income', isActive: true, createdAt: '2026-01-01T00:00:00Z' },
]
const forecastSettings = { exists: false, isActive: false, accountId: null, accountName: null, hourlyRate: '0.00', hoursPerDay: '8', paymentDay: 5, paymentOffsetMonths: 1, taxReservePercent: '0.00', operationalReserve: '0.00', months: [], accounts: [] }

interface Stubs { projection?: (url: URL) => FinancialProjection | Response; food?: FoodProvision; write?: (url: URL, init: RequestInit) => FoodProvision }
const stub = (stubs: Stubs = {}) => {
  let food = stubs.food ?? foodProvision
  const fetchMock = vi.fn((input: string, init: RequestInit = {}) => {
    const url = new URL(String(input), 'http://x')
    const method = init.method ?? 'GET'
    if (url.pathname === '/auth/csrf') return json({ token: 'csrf' })
    if (url.pathname.endsWith('/categories')) return json(categories)
    if (url.pathname.endsWith('/monthly-plan/projection')) {
      const value = stubs.projection ? stubs.projection(url) : projection(url.searchParams.get('from')!, Number(url.searchParams.get('months')))
      return value instanceof Response ? Promise.resolve(value) : json(value)
    }
    if (url.pathname.endsWith('/monthly-plan/income-forecast')) return json(forecastSettings)
    if (url.pathname.includes('/monthly-plan/food-provision')) {
      if (method !== 'GET') food = stubs.write?.(url, init) ?? food
      return json(food)
    }
    return json({}, 404)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}
const calls = (fetchMock: ReturnType<typeof stub>, method: string, part: string) =>
  fetchMock.mock.calls.filter(([input, init]) => (init?.method ?? 'GET') === method && new URL(String(input), 'http://x').pathname.includes(part))
const projectionQueries = (fetchMock: ReturnType<typeof stub>) => calls(fetchMock, 'GET', '/monthly-plan/projection').map(([input]) => new URL(String(input), 'http://x').searchParams)

const open = async (stubs: Stubs = {}) => {
  const fetchMock = stub(stubs)
  render(<PlanningHub workspaceId="w" initialTab="projection" />)
  await screen.findByLabelText('Indicadores do cenário Atual')
  return fetchMock
}
const indicators = (name: 'Atual' | 'Tetos') => screen.getByLabelText(`Indicadores do cenário ${name}`)

describe('Projeção financeira no Planejamento', () => {
  it('abre na competência corrente, com 6 meses e os dois cenários comparados, consultando a rota de projeção', async () => {
    const fetchMock = await open()
    const query = projectionQueries(fetchMock)[0]
    expect(query.get('from')).toMatch(/^\d{4}-\d{2}$/)
    expect(query.get('months')).toBe('6')
    expect((screen.getByLabelText('Horizonte') as HTMLSelectElement).value).toBe('6')
    expect((screen.getByLabelText('Cenário') as HTMLSelectElement).value).toBe('both')
    expect(text(indicators('Atual'))).toContain('Atual')
    expect(text(indicators('Tetos'))).toContain('Tetos')
  })

  it('a aba Projeção financeira substitui "Seis meses" como principal e a visão antiga e os cenários configuráveis continuam acessíveis', async () => {
    stub()
    render(<PlanningHub workspaceId="w" />)
    const tabs = screen.getAllByRole('tab').map(tab => tab.textContent)
    expect(tabs).toEqual(['Plano mensal', 'Projeção financeira', 'Seis meses (detalhado)', 'Dias úteis', 'Cenários configuráveis'])
  })

  it.each([13, 24, 36])('troca o horizonte para %i meses e refaz a consulta', async months => {
    const fetchMock = await open()
    fireEvent.change(screen.getByLabelText('Horizonte'), { target: { value: String(months) } })
    await waitFor(() => expect(projectionQueries(fetchMock).at(-1)?.get('months')).toBe(String(months)))
    await waitFor(() => expect(screen.getByLabelText('Mês em detalhe').querySelectorAll('option')).toHaveLength(months))
  })

  it('altera a competência inicial e consulta de novo a partir dela', async () => {
    const fetchMock = await open()
    fireEvent.change(screen.getByLabelText('Competência'), { target: { value: '2027-02' } })
    await waitFor(() => expect(projectionQueries(fetchMock).at(-1)?.get('from')).toBe('2027-02'))
    expect(await screen.findByRole('heading', { name: /Detalhamento · Fevereiro de 2027/ })).toBeTruthy()
  })

  it('compara saldo e utilização do limite dos dois cenários, sem recalcular nada', async () => {
    await open()
    expect(text(indicators('Atual'))).toMatch(/Saldo final PF do mês\s*R\$ 500,00/)
    expect(text(indicators('Tetos'))).toMatch(/Saldo final PF do mês\s*-R\$ 1\.500,00/)
    expect(text(indicators('Atual'))).toMatch(/Resultado financeiro do mês\s*R\$ 1\.500,00/)
    expect(text(indicators('Tetos'))).toMatch(/Resultado financeiro do mês\s*-R\$ 500,00/)
    expect(text(indicators('Atual'))).toMatch(/Limite do cheque especial utilizado\s*R\$ 0,00/)
    expect(text(indicators('Tetos'))).toMatch(/Limite do cheque especial utilizado\s*R\$ 1\.500,00/)
  })

  it('mostra recuperação negativa como piora, com valor negativo e aviso', async () => {
    await open()
    expect(text(indicators('Tetos'))).toMatch(/Recuperação mensal do limite\s*-R\$ 500,00/)
    expect(text(indicators('Tetos'))).toMatch(/Recuperação acumulada\s*-R\$ 500,00/)
    expect(text(indicators('Tetos'))).toContain('A utilização do limite aumentou neste mês.')
    expect(text(indicators('Atual'))).not.toContain('aumentou')
  })

  it('mostra saldo positivo e ausência de utilização do limite', async () => {
    await open({ projection: url => projection(url.searchParams.get('from')!, 6, () => ({
      overdraft: { limit: '3000.00', usedStart: '0.00', usedEnd: '0.00', available: '3000.00', monthlyRecovery: '0.00', cumulativeRecovery: '0.00', positiveBalance: '1500.00' },
    })) })
    expect(text(indicators('Atual'))).toMatch(/Saldo positivo\s*R\$ 1\.500,00/)
    expect(text(indicators('Atual'))).toMatch(/Limite do cheque especial utilizado\s*R\$ 0,00/)
    expect(text(indicators('Atual'))).not.toContain('aumentou')
  })

  it('apresenta valores nulos como indisponíveis, nunca como R$ 0,00, e sem ponto no gráfico', async () => {
    await open({ projection: url => projection(url.searchParams.get('from')!, 6, (_competence, _scenario, index) => index === 0
      ? { overdraft: null, complete: false, quality: ['OverdraftChargesNotEstimated'], pf: { opening: null, entries: '0.00', outsideCardsExpenses: '0.00', outsideCardsComplement: '0.00', cardConsumption: '0.00', cardConsumptionComplement: '0.00', cardPaymentsInCash: '0.00', transfersIn: '0.00', result: null, closing: null } }
      : {}) })
    const atual = text(indicators('Atual'))
    expect(atual).toMatch(/Saldo final PF do mês\s*Indisponível/)
    expect(atual).toMatch(/Resultado financeiro do mês\s*Indisponível/)
    expect(atual).toMatch(/Limite do cheque especial utilizado\s*Indisponível/)
    expect(atual).toContain('Projeção parcial')
    const balance = screen.getByRole('img', { name: /Evolução do saldo PF/ })
    expect(balance.querySelectorAll('.proj-series--Current circle')).toHaveLength(5)
  })

  it('agrupa avisos repetidos por código e permite consultar as competências em que ocorrem', async () => {
    await open({ projection: url => ({ ...projection(url.searchParams.get('from')!, 6, (_c, scenario, index) => index >= 3 && scenario === 'Ceilings'
      ? { complete: false, quality: ['CardsCeilingExceeded', 'IncomeReceiptAmbiguous'] } : {}), notes: ['FoodKnownFromCategorizedPurchasesOnly', 'FoodCategoriesInferred', 'OverdraftChargesNotEstimated'] }) })
    const section = screen.getByRole('region', { name: 'Qualidade dos dados' })
    expect(text(section).match(/O consumo de cartões já excede o teto/g)).toHaveLength(1)
    expect(text(section)).toContain('Mais de uma competência disputa o mesmo recebimento')
    expect(text(section)).toContain('Projeção parcial · A alimentação já reconhecida considera só compras de cartão com categoria alimentar')
    expect(text(section)).toContain('Informação · As categorias de alimentação foram inferidas pelo nome')
    expect(text(section)).toContain('juros e IOF futuros não são estimados')
    const ceiling = within(section).getAllByText(/Competências \(3\)/)[0]
    expect(text(ceiling.closest('details'))).toMatch(/Fevereiro de 2027, Março de 2027, Abril de 2027 · somente no cenário Tetos/)
  })

  it('mostra o detalhamento do mês escolhido, diferenciando consumo de cartão de desembolso bancário', async () => {
    await open()
    fireEvent.change(screen.getByLabelText('Mês em detalhe'), { target: { value: '2026-12' } })
    const detail = screen.getByRole('heading', { name: /Detalhamento · Dezembro de 2026/ }).closest('section')!
    for (const heading of ['Receita PJ', 'Orçamento PF', 'Fluxo de caixa PF', 'Cheque especial']) expect(within(detail).getByRole('heading', { name: heading })).toBeTruthy()
    const row = (label: string) => text(within(detail).getByRole('rowheader', { name: label }).closest('tr'))
    expect(row('Repasse PJ → PF projetado')).toContain('R$ 6.000,00')
    expect(row('Pagamentos de faturas')).toContain('R$ 2.500,00')
    expect(row('Consumo dos cartões')).toContain('R$ 3.000,00')
    expect(row('Margem dos cartões')).toContain('R$ 1.500,00')
    expect(text(detail)).toContain('o consumo do cartão não é desembolso bancário')
  })

  it('selecionar um mês no gráfico atualiza indicadores e detalhamento', async () => {
    await open()
    fireEvent.click(screen.getAllByRole('button', { name: 'Selecionar Novembro de 2026' })[0])
    expect(await screen.findByRole('heading', { name: /Detalhamento · Novembro de 2026/ })).toBeTruthy()
    expect((screen.getByLabelText('Mês em detalhe') as HTMLSelectElement).value).toBe('2026-11')
  })

  it('exibe um único cenário quando escolhido', async () => {
    await open()
    fireEvent.change(screen.getByLabelText('Cenário'), { target: { value: 'Ceilings' } })
    expect(screen.queryByLabelText('Indicadores do cenário Atual')).toBeNull()
    expect(screen.getByLabelText('Indicadores do cenário Tetos')).toBeTruthy()
  })

  it('tem gráficos escaláveis e tabelas contidas, sem larguras fixas que forcem rolagem da página', async () => {
    await open()
    for (const svg of document.querySelectorAll('.proj-chart svg')) { expect(svg.getAttribute('viewBox')).toBeTruthy(); expect(svg.getAttribute('width')).toBeNull() }
    for (const table of document.querySelectorAll('.proj-summary, .proj-detail__table')) expect(table.closest('.monthly-plan__table-wrap')).toBeTruthy()
  })

  it('só faz leituras ao abrir e ao mudar filtros: nenhuma alteração financeira', async () => {
    const fetchMock = await open()
    fireEvent.change(screen.getByLabelText('Horizonte'), { target: { value: '13' } })
    await waitFor(() => expect(projectionQueries(fetchMock)).toHaveLength(2))
    expect(fetchMock.mock.calls.filter(([, init]) => (init?.method ?? 'GET') !== 'GET')).toEqual([])
  })

  it('mostra erro e permite tentar novamente', async () => {
    let attempt = 0
    await Promise.resolve(stub({ projection: url => (attempt++ === 0 ? new Response('{}', { status: 500 }) : projection(url.searchParams.get('from')!, 6)) }))
    render(<PlanningHub workspaceId="w" initialTab="projection" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByLabelText('Indicadores do cenário Atual')).toBeTruthy()
  })
})

describe('Provisão de alimentação', () => {
  it('carrega o valor de referência sem gravar nada e sinaliza que ainda não foi configurado', async () => {
    const fetchMock = await open()
    await screen.findByLabelText('Valor mensal padrão da provisão')
    expect((screen.getByLabelText('Valor mensal padrão da provisão') as HTMLInputElement).value).toBe('2000,00')
    expect(screen.getByText(/ainda não gravado/)).toBeTruthy()
    expect((screen.getByRole('checkbox', { name: 'Alimentação' }) as HTMLInputElement).checked).toBe(true)
    expect(screen.queryByRole('checkbox', { name: 'Salário' })).toBeNull()
    expect(fetchMock.mock.calls.filter(([, init]) => (init?.method ?? 'GET') !== 'GET')).toEqual([])
  })

  it('salva valor e categorias por identificador e atualiza a projeção', async () => {
    const fetchMock = await open({ write: () => ({ monthlyAmount: '2500.00', amountConfigured: true, categoryIds: ['cat-f', 'cat-m'], categoriesInferred: false, monthAmounts: {} }) })
    await screen.findByLabelText('Valor mensal padrão da provisão')
    fireEvent.change(screen.getByLabelText('Valor mensal padrão da provisão'), { target: { value: '2.500,00' } })
    fireEvent.click(screen.getByRole('checkbox', { name: 'Mercado' }))
    const before = projectionQueries(fetchMock).length
    fireEvent.click(screen.getByRole('button', { name: 'Salvar provisão' }))
    await waitFor(() => expect(calls(fetchMock, 'PUT', '/food-provision')).toHaveLength(1))
    expect(JSON.parse(String(calls(fetchMock, 'PUT', '/food-provision')[0][1]!.body))).toEqual({ monthlyAmount: '2500.00', categoryIds: ['cat-f', 'cat-m'] })
    await waitFor(() => expect(projectionQueries(fetchMock).length).toBeGreaterThan(before))
  })

  it('ajusta uma competência e restaura o padrão', async () => {
    const fetchMock = await open({ write: (url, init) => init.method === 'DELETE'
      ? { ...foodProvision, amountConfigured: true, monthAmounts: {} }
      : { ...foodProvision, amountConfigured: true, monthAmounts: { [decodeURIComponent(url.pathname.split('/').pop()!)]: '3000.00' } } })
    await screen.findByLabelText('Valor mensal padrão da provisão')
    const month = (screen.getByLabelText('Mês em detalhe') as HTMLSelectElement).value
    const input = screen.getByLabelText(/Valor da provisão em/)
    fireEvent.change(input, { target: { value: '3000,00' } })
    fireEvent.click(screen.getByRole('button', { name: 'Ajustar competência' }))
    await waitFor(() => expect(calls(fetchMock, 'PUT', `/food-provision/months/${month}`)).toHaveLength(1))
    expect(JSON.parse(String(calls(fetchMock, 'PUT', `/food-provision/months/${month}`)[0][1]!.body))).toEqual({ monthlyAmount: '3000.00' })
    const restore = await screen.findByRole('button', { name: 'Voltar ao valor padrão' })
    await waitFor(() => expect((restore as HTMLButtonElement).disabled).toBe(false))
    fireEvent.click(restore)
    await waitFor(() => expect(calls(fetchMock, 'DELETE', `/food-provision/months/${month}`)).toHaveLength(1))
  })

  it('recusa valor inválido sem chamar a API e exige ao menos uma categoria', async () => {
    const fetchMock = await open()
    await screen.findByLabelText('Valor mensal padrão da provisão')
    fireEvent.change(screen.getByLabelText('Valor mensal padrão da provisão'), { target: { value: 'abc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar provisão' }))
    expect((await screen.findByRole('alert')).textContent).toContain('Valor inválido')
    expect(calls(fetchMock, 'PUT', '/food-provision')).toHaveLength(0)
    fireEvent.click(screen.getByRole('checkbox', { name: 'Alimentação' }))
    expect((screen.getByRole('button', { name: 'Salvar provisão' }) as HTMLButtonElement).disabled).toBe(true)
  })
})
