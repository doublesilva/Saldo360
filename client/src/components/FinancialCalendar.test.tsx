import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type {
  FinancialCalendarBalanceResponse,
  FinancialCalendarItem,
  FinancialCalendarMonthlyProjectionResponse,
  FinancialCalendarResponse,
} from '../api/financialCalendar'
import type { FamilyDetails } from '../api/families'
import { addCalendarMonths } from '../dates'
import { Families } from './Families'
import { FinancialCalendarPage } from './FinancialCalendar'

type Call = { url: string; init?: RequestInit }

const account = (id: string, name: string, isActive = true): FinancialAccount => ({
  id,
  name,
  institution: null,
  type: 'Checking',
  financialScope: 'PF',
  responsible: null,
  openingBalance: null,
  openingBalanceDate: null,
  isActive,
  currency: 'BRL',
  createdAt: '2026-01-01T00:00:00Z',
})

const itau = account('acc-itau', 'Itaú PF')
const nubank = account('acc-nubank', 'Nubank', false)

const item = (overrides: Partial<FinancialCalendarItem> = {}): FinancialCalendarItem => ({
  id: 'tx-income',
  source: 'Transaction',
  status: 'Realized',
  type: 'Income',
  amount: '9999999999999.99',
  date: '2026-12-15',
  description: 'Salário recebido',
  account: { id: itau.id, name: itau.name, isActive: true },
  category: { id: 'cat-income', name: 'Salário', isActive: true },
  currency: 'BRL',
  plannedTransactionId: null,
  ...overrides,
})

const items: FinancialCalendarItem[] = [
  item(),
  item({
    id: 'planned-overdue',
    source: 'PlannedTransaction',
    status: 'Overdue',
    type: 'Expense',
    amount: '10.05',
    date: '2026-12-10',
    description: 'Conta vencida',
    category: { id: 'cat-bills', name: 'Contas', isActive: true },
  }),
  item({
    id: 'planned-pending',
    source: 'PlannedTransaction',
    status: 'Pending',
    type: 'Expense',
    amount: '1234.56',
    date: '2026-12-20',
    description: 'Aluguel previsto',
    account: { id: nubank.id, name: nubank.name, isActive: false },
    category: null,
  }),
]

const summary = {
  realizedIncome: '9999999999999.99',
  realizedExpense: '200.00',
  pendingIncome: '500.00',
  pendingExpense: '1234.56',
  overdueIncome: '0.00',
  overdueExpense: '10.05',
}

function response(overrides: Partial<FinancialCalendarResponse> = {}): FinancialCalendarResponse {
  return {
    from: '2026-12-01',
    through: '2026-12-31',
    summary,
    items,
    ...overrides,
  }
}

function balanceResponse(overrides: Partial<FinancialCalendarBalanceResponse> = {}): FinancialCalendarBalanceResponse {
  return {
    from: '2026-12-01',
    through: '2026-12-31',
    accountId: null,
    openingBalance: '900.00',
    unavailableReason: null,
    days: [
      {
        date: '2026-12-14',
        kind: 'Realized',
        income: '100.00',
        expense: '0.00',
        transferIn: '0.00',
        transferOut: '0.00',
        closingBalance: '1000.00',
        hasOverduePending: false,
        isNegative: false,
        chartPosition: 0,
        unavailableReason: null,
      },
      {
        date: '2026-12-15',
        kind: 'Projected',
        income: '0.00',
        expense: '1050.00',
        transferIn: '0.00',
        transferOut: '0.00',
        closingBalance: '-50.00',
        hasOverduePending: true,
        isNegative: true,
        chartPosition: 1000,
        unavailableReason: null,
      },
      {
        date: '2026-12-20',
        kind: 'Projected',
        income: '550.00',
        expense: '0.00',
        transferIn: '0.00',
        transferOut: '0.00',
        closingBalance: '500.00',
        hasOverduePending: true,
        isNegative: false,
        chartPosition: 476,
        unavailableReason: null,
      },
    ],
    risk: {
      minimumProjectedBalance: '-50.00',
      firstNegativeProjectedDate: '2026-12-15',
      endingProjectedBalance: '500.00',
    },
    ...overrides,
  }
}

function mockApi(
  calendar: (url: string) => Response | Promise<Response> = () => Response.json(response()),
  balances: (url: string) => Response | Promise<Response> = () => Response.json(balanceResponse()),
  monthly: (url: string) => Response | Promise<Response> = () => Response.json(monthlyResponse()),
) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (/\/accounts(?:\?|$)/.test(url)) return Response.json([itau, nubank])
      if (url.includes('/financial-calendar/monthly-projection?')) return monthly(url)
      if (url.includes('/financial-calendar/balances?')) return balances(url)
      if (url.includes('/financial-calendar?')) return calendar(url)
      throw new Error(`URL inesperada: ${url}`)
    }),
  )
  return calls
}

function monthlyResponse(): FinancialCalendarMonthlyProjectionResponse {
  return {
    from: '2026-12',
    months: Array.from({ length: 6 }, (_, index) => {
      const month = addCalendarMonths('2026-12-01', index).slice(0, 7)
      return {
        month,
        realizedIncome: '0.00', realizedExpense: '0.00', pendingIncome: '0.00', pendingExpense: '0.00',
        overdueIncome: '0.00', overdueExpense: '0.00', openingBalance: '100.00', closingBalance: '100.00',
        minimumProjectedBalance: '100.00', firstNegativeProjectedDate: null, unavailableReason: null,
      }
    }),
  }
}

function calendarCalls(calls: Call[]): Call[] {
  return calls.filter((call) => call.url.includes('/financial-calendar?'))
}

function balanceCalls(calls: Call[]): Call[] {
  return calls.filter((call) => call.url.includes('/financial-calendar/balances?'))
}

function monthlyCalls(calls: Call[]): Call[] {
  return calls.filter((call) => call.url.includes('/financial-calendar/monthly-projection?'))
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  vi.setSystemTime(new Date('2026-12-15T15:00:00Z'))
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('FinancialCalendarPage — mês, calendário e compromissos', () => {
  it('abre no mês atual de São Paulo, consulta o intervalo correto e destaca hoje', async () => {
    const calls = mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)

    expect(await screen.findByText('Dezembro de 2026')).toBeTruthy()
    await waitFor(() => expect(calendarCalls(calls)).toHaveLength(1))
    const query = new URL(calendarCalls(calls)[0].url, 'http://x').searchParams
    expect(Object.fromEntries(query)).toEqual({ from: '2026-12-01', through: '2026-12-31' })
    const today = screen.getByRole('gridcell', { name: /15\/12\/2026/ })
    expect(today.getAttribute('aria-current')).toBe('date')
    expect(today.getAttribute('aria-pressed')).toBe('true')
  })

  it('mostra resumo vindo da API sem perder precisão e itens realizados, pendentes e vencidos', async () => {
    mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)

    const monthlySummary = await screen.findByLabelText('Resumo financeiro do mês')
    expect(within(monthlySummary).getByText(/↑ R\$\s9\.999\.999\.999\.999,99/)).toBeTruthy()
    expect(within(screen.getByText('Receitas vencidas').parentElement!).getByText(/↑ R\$\s0,00/)).toBeTruthy()
    expect(within(screen.getByText('Despesas vencidas').parentElement!).getByText(/↓ R\$\s10,05/)).toBeTruthy()

    let events = screen.getByLabelText(/Compromissos de 15\/12\/2026/)
    expect(within(events).getByText('Salário recebido')).toBeTruthy()
    expect(within(events).getByText('Realizado')).toBeTruthy()

    fireEvent.click(screen.getByRole('gridcell', { name: /20\/12\/2026/ }))
    events = screen.getByLabelText(/Compromissos de 20\/12\/2026/)
    expect(within(events).getByText('Aluguel previsto')).toBeTruthy()
    expect(within(events).getByText('Pendente')).toBeTruthy()
    expect(events.textContent).toMatch(/−R\$\s1\.234,56/)
    expect(events.textContent).toContain('Nubank (inativa)')
    expect(within(events).getByRole('button', { name: 'Ver detalhes de Aluguel previsto' })).toBeTruthy()

    fireEvent.click(screen.getByRole('gridcell', { name: /10\/12\/2026/ }))
    expect(within(screen.getByLabelText(/Compromissos de 10\/12\/2026/)).getByText('Vencido')).toBeTruthy()
  })

  it('permite selecionar dia vazio e anuncia ausência de acontecimentos', async () => {
    mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')

    fireEvent.click(screen.getByRole('gridcell', { name: /16\/12\/2026: sem acontecimentos/ }))

    expect(screen.getByText('Compromissos de 16/12/2026')).toBeTruthy()
    expect(screen.getByText('Nenhum acontecimento nesta data.')).toBeTruthy()
  })

  it('exibe mês sem acontecimentos com os seis totais zerados fornecidos pela API', async () => {
    const zeroSummary = {
      realizedIncome: '0.00',
      realizedExpense: '0.00',
      pendingIncome: '0.00',
      pendingExpense: '0.00',
      overdueIncome: '0.00',
      overdueExpense: '0.00',
    }
    mockApi(() => Response.json(response({ summary: zeroSummary, items: [] })))
    render(<FinancialCalendarPage familyId="family-1" />)

    const monthlySummary = await screen.findByLabelText('Resumo financeiro do mês')
    expect(within(monthlySummary).getAllByText(/R\$\s0,00/)).toHaveLength(6)
    for (const label of [
      'Receitas realizadas',
      'Despesas realizadas',
      'Receitas pendentes',
      'Despesas pendentes',
      'Receitas vencidas',
      'Despesas vencidas',
    ]) expect(within(monthlySummary).getByText(label)).toBeTruthy()
    expect(screen.getByText('Nenhum acontecimento nesta data.')).toBeTruthy()
  })
})

describe('FinancialCalendarPage — navegação e filtros', () => {
  it('navega entre meses atravessando o ano e retorna ao mês atual', async () => {
    const calls = mockApi((url) => {
      const query = new URL(url, 'http://x').searchParams
      const from = query.get('from')!
      return Response.json(response({
        from,
        through: query.get('through')!,
        items: [],
        summary: from === '2027-01-01' ? { ...summary, realizedIncome: '101.01' } : summary,
      }))
    })
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Dezembro de 2026')

    fireEvent.click(screen.getByRole('button', { name: 'Próximo mês' }))
    expect(await screen.findByText('Janeiro de 2027')).toBeTruthy()
    await waitFor(() => expect(calendarCalls(calls).at(-1)!.url).toContain('from=2027-01-01&through=2027-01-31'))
    await waitFor(() => expect(balanceCalls(calls).at(-1)!.url).toContain('from=2027-01-01&through=2027-01-31'))
    await waitFor(() => expect(within(screen.getByText('Receitas realizadas').parentElement!).getByText(/↑ R\$\s101,01/)).toBeTruthy())

    fireEvent.click(screen.getByRole('button', { name: 'Mês anterior' }))
    expect(await screen.findByText('Dezembro de 2026')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Mês anterior' }))
    expect(await screen.findByText('Novembro de 2026')).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Mês atual' }))
    expect(await screen.findByText('Dezembro de 2026')).toBeTruthy()
    expect(screen.getByRole('gridcell', { name: /15\/12\/2026/ }).getAttribute('aria-pressed')).toBe('true')
  })

  it('consulta novamente ao filtrar por conta, receita e despesa', async () => {
    const calls = mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')

    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: itau.id } })
    await waitFor(() => expect(calendarCalls(calls).at(-1)!.url).toContain(`accountId=${itau.id}`))
    await waitFor(() => expect(balanceCalls(calls).at(-1)!.url).toContain(`accountId=${itau.id}`))

    fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Income' } })
    await waitFor(() => expect(calendarCalls(calls).at(-1)!.url).toContain('type=Income'))
    expect(calendarCalls(calls).at(-1)!.url).toContain(`accountId=${itau.id}`)

    fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Expense' } })
    await waitFor(() => expect(calendarCalls(calls).at(-1)!.url).toContain('type=Expense'))
    expect((screen.getByRole('option', { name: 'Nubank (inativa)' }) as HTMLOptionElement).value).toBe(nubank.id)
  })

  it('atualiza os totais usando a nova resposta da API', async () => {
    mockApi((url) =>
      Response.json(
        response({
          summary: url.includes('type=Expense')
            ? { ...summary, realizedIncome: '0.00', realizedExpense: '765.43' }
            : summary,
        }),
      ),
    )
    render(<FinancialCalendarPage familyId="family-1" />)
    const monthlySummary = await screen.findByLabelText('Resumo financeiro do mês')
    expect(within(monthlySummary).getByText(/↑ R\$\s9\.999\.999\.999\.999,99/)).toBeTruthy()

    fireEvent.change(screen.getByLabelText('Tipo'), { target: { value: 'Expense' } })

    await waitFor(() => expect(within(screen.getByText('Despesas realizadas').parentElement!).getByText(/↓ R\$\s765,43/)).toBeTruthy())
    expect(within(screen.getByText('Receitas realizadas').parentElement!).getByText(/↑ R\$\s0,00/)).toBeTruthy()
  })

  it('abre próximos meses, respeita a conta e retorna ao calendário da competência escolhida', async () => {
    const calls = mockApi()
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')

    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: itau.id } })
    fireEvent.click(screen.getByRole('button', { name: 'Próximos meses' }))
    expect(await screen.findAllByRole('article')).toHaveLength(6)
    await waitFor(() => expect(monthlyCalls(calls).at(-1)!.url).toContain(`from=2026-12&months=6&accountId=${itau.id}`))

    fireEvent.click(screen.getByRole('button', { name: /Janeiro de 2027/ }))
    expect(await screen.findByText('Janeiro de 2027')).toBeTruthy()
    await waitFor(() => expect(calendarCalls(calls).at(-1)!.url).toContain('from=2027-01-01&through=2027-01-31'))
  })
})

describe('FinancialCalendarPage — erros, família e concorrência', () => {
  it('trata falha da API sem manter o calendário anterior', async () => {
    mockApi(() => new Response(null, { status: 503 }))
    render(<FinancialCalendarPage familyId="family-1" />)

    expect(await screen.findByRole('alert')).toHaveProperty(
      'textContent',
      'Falha ao carregar a agenda financeira (HTTP 503).',
    )
    expect(screen.queryByRole('grid')).toBeNull()
  })

  it('troca a família ativa, limpa filtros e consulta a nova família', async () => {
    const calls = mockApi()
    const view = render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByText('Salário recebido')
    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: itau.id } })
    await waitFor(() => expect(calendarCalls(calls).at(-1)!.url).toContain(`accountId=${itau.id}`))

    view.rerender(<FinancialCalendarPage key="family-2" familyId="family-2" />)

    await waitFor(() => expect(calendarCalls(calls).at(-1)!.url).toContain('/families/family-2/financial-calendar'))
    await waitFor(() => expect((screen.getByLabelText('Conta') as HTMLSelectElement).value).toBe(''))
  })

  it('descarta resposta obsoleta após troca rápida de família', async () => {
    let resolveOld!: (response: Response) => void
    const oldResponse = new Promise<Response>((resolve) => {
      resolveOld = resolve
    })
    mockApi((url) =>
      url.includes('/families/family-old/')
        ? oldResponse
        : Response.json(response({ items: [item({ id: 'new', description: 'Resposta nova' })] })),
    )
    const view = render(<FinancialCalendarPage familyId="family-old" />)
    await screen.findByText('Carregando agenda…')

    view.rerender(<FinancialCalendarPage familyId="family-new" />)
    expect(await screen.findByText('Resposta nova')).toBeTruthy()
    resolveOld(Response.json(response({ items: [item({ id: 'old', description: 'Resposta antiga' })] })))

    await Promise.resolve()
    expect(screen.queryByText('Resposta antiga')).toBeNull()
    expect(screen.getByText('Resposta nova')).toBeTruthy()
  })

  it('expõe Agenda na navegação principal sem remover as demais opções', async () => {
    const family: FamilyDetails = {
      id: 'family-1',
      name: 'Família Silva',
      createdAt: '2026-01-01T00:00:00Z',
      myRole: 'Member',
      members: [{ memberId: 'm1', displayName: 'Diego', role: 'Member', joinedAt: '2026-01-01T00:00:00Z', isCurrentUser: true }],
    }
    const calls: Call[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        calls.push({ url, init })
        if (url === '/api/families') return Response.json([])
        if (url.includes('/accounts')) return Response.json([itau])
        if (url.includes('/financial-calendar/balances')) return Response.json(balanceResponse())
        if (url.includes('/financial-calendar')) return Response.json(response())
        throw new Error(`URL inesperada: ${url}`)
      }),
    )
    render(<Families initialFamily={family} />)

    const navigation = screen.getByRole('navigation', { name: 'Navegação principal da família' })
    expect(within(navigation).getAllByRole('button').map((button) => button.textContent)).toEqual([
      'Família', 'Agenda', 'Orçamentos', 'Cartões', 'Recorrências', 'Planejamento', 'Dívidas', 'Contas', 'Categorias', 'Lançamentos', 'Transferências', 'Previsões',
    ])
    fireEvent.click(within(navigation).getByRole('button', { name: 'Agenda' }))

    expect(await screen.findByText('Agenda Financeira')).toBeTruthy()
    expect(calendarCalls(calls)).toHaveLength(1)
  })
})

describe('FinancialCalendarPage — evolução diária do saldo', () => {
  it('exibe gráfico, indicadores e tabela acessível distinguindo realizado, projetado, negativo e vencidas', async () => {
    mockApi()
    const rendered = render(<FinancialCalendarPage familyId="family-1" />)

    expect(await screen.findByRole('img', { name: /Gráfico da evolução diária/ })).toBeTruthy()
    expect(screen.getByText('Histórico realizado')).toBeTruthy()
    expect(screen.getAllByText(/Projetado/).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/inclui pendências vencidas/).length).toBeGreaterThan(0)
    expect(within(screen.getByText('Menor saldo projetado').parentElement!).getByText(/-R\$\s50,00/)).toBeTruthy()
    expect(screen.getByText('15/12/2026 (selecionado)')).toBeTruthy()
    expect(rendered.container.querySelector('.balance-chart__line--projected')).toBeTruthy()
    expect(rendered.container.querySelector('.balance-chart__point--negative')).toBeTruthy()
  })

  it('destaca na evolução o dia selecionado no calendário', async () => {
    mockApi()
    const rendered = render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByRole('img', { name: /Gráfico da evolução diária/ })

    fireEvent.click(screen.getByRole('gridcell', { name: /20\/12\/2026/ }))

    expect(screen.getByText('20/12/2026 (selecionado)')).toBeTruthy()
    const selected = rendered.container.querySelector('.balance-chart__point--selected')
    expect(selected?.querySelector('title')?.textContent).toMatch(/^20\/12\/2026:/)
  })

  it('preserva carregamento, apresenta erro próprio e não remove a agenda', async () => {
    let resolveBalances!: (response: Response) => void
    const pending = new Promise<Response>((resolve) => { resolveBalances = resolve })
    mockApi(undefined, () => pending)
    render(<FinancialCalendarPage familyId="family-1" />)

    expect(await screen.findByText('Calculando evolução do saldo…')).toBeTruthy()
    expect(await screen.findByText('Salário recebido')).toBeTruthy()
    resolveBalances(new Response(null, { status: 503 }))
    expect(await screen.findByRole('alert')).toHaveProperty(
      'textContent',
      'Falha ao carregar a evolução do saldo (HTTP 503).',
    )
    expect(screen.getByRole('grid')).toBeTruthy()
  })

  it('explica saldo indisponível sem apresentar total consolidado parcial', async () => {
    mockApi(undefined, () => Response.json(balanceResponse({
      openingBalance: null,
      unavailableReason: 'OpeningBalanceNotConfigured',
      days: balanceResponse().days.map((day) => ({
        ...day,
        income: null,
        expense: null,
        transferIn: null,
        transferOut: null,
        closingBalance: null,
        chartPosition: null,
        unavailableReason: 'OpeningBalanceNotConfigured',
      })),
      risk: { minimumProjectedBalance: null, firstNegativeProjectedDate: null, endingProjectedBalance: null },
    })))
    render(<FinancialCalendarPage familyId="family-1" />)

    expect(await screen.findByText(/existe conta sem saldo inicial configurado/)).toBeTruthy()
    expect(screen.getByText(/Nenhum total parcial/)).toBeTruthy()
    expect(screen.queryByRole('img', { name: /Gráfico da evolução diária/ })).toBeNull()
  })

  it('descarta resposta obsoleta ao trocar rapidamente a conta', async () => {
    let resolveOld!: (response: Response) => void
    const oldResponse = new Promise<Response>((resolve) => { resolveOld = resolve })
    mockApi(undefined, (url) => url.includes(`accountId=${itau.id}`)
      ? oldResponse
      : Response.json(url.includes(`accountId=${nubank.id}`)
        ? balanceResponse({ openingBalance: '222.00' })
        : balanceResponse()))
    render(<FinancialCalendarPage familyId="family-1" />)
    await screen.findByRole('img', { name: /Gráfico da evolução diária/ })

    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: itau.id } })
    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: nubank.id } })
    await waitFor(() => expect(within(screen.getByText('Saldo no início do período').parentElement!).getByText(/R\$\s222,00/)).toBeTruthy())
    resolveOld(Response.json(balanceResponse({ openingBalance: '111.00' })))

    await Promise.resolve()
    expect(screen.queryByText(/R\$\s111,00/)).toBeNull()
  })
})
