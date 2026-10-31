import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialAccount } from '../api/accounts'
import type { FinancialTransfer, TransferPage } from '../api/transfers'
import { todayInSaoPaulo } from '../dates'
import { Transfers } from './Transfers'

const pluggy = vi.hoisted(() => ({ undoTransfer: vi.fn() }))
vi.mock('../api/pluggyIntegration', () => pluggy)

const account = (id: string, name: string, scope: 'PF' | 'PJ', isActive = true): FinancialAccount => ({
  id,
  name,
  institution: null,
  type: 'Checking',
  financialScope: scope,
  responsible: null,
  openingBalance: null,
  openingBalanceDate: null,
  isActive,
  currency: 'BRL',
  createdAt: '2026-10-01T00:00:00Z',
})

const empresas = account('acc-pj', 'Itaú Empresas', 'PJ')
const pessoal = account('acc-pf', 'Itaú PF', 'PF')
const antiga = account('acc-old', 'Conta antiga', 'PF', false)

const prolabore: FinancialTransfer = {
  id: 't1',
  amount: '500.00',
  transferDate: '2026-10-05',
  description: 'Pró-labore',
  sourceAccount: { id: empresas.id, name: empresas.name, financialScope: 'PJ', isActive: true },
  destinationAccount: { id: antiga.id, name: antiga.name, financialScope: 'PF', isActive: false },
  isCancelled: false,
  currency: 'BRL',
  createdAt: '2026-10-05T12:00:00Z',
  cancelledAt: null,
}

const cancelada: FinancialTransfer = { ...prolabore, id: 't2', description: 'Errada', isCancelled: true, cancelledAt: '2026-10-06T00:00:00Z' }

type Call = { url: string; init?: RequestInit }

function mockApi(
  options: { accounts?: FinancialAccount[]; page?: Partial<TransferPage>; saveStatus?: number; saveBody?: unknown } = {},
) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url.includes('/accounts')) return Response.json(options.accounts ?? [empresas, pessoal, antiga])
      if (init?.method === 'POST') {
        if (options.saveStatus) return Response.json(options.saveBody ?? {}, { status: options.saveStatus })
        if (url.endsWith('/cancel')) return Response.json({ ...prolabore, isCancelled: true })
        return Response.json(prolabore, { status: 201 })
      }
      const items = url.includes('includeCancelled=true') ? [prolabore, cancelada] : [prolabore]
      return Response.json({ items, page: 1, pageSize: 20, totalCount: items.length, ...options.page })
    }),
  )
  return calls
}

const writes = (calls: Call[]) => calls.filter((c) => c.init?.method === 'POST')
const listCalls = (calls: Call[]) => calls.filter((c) => c.url.includes('/transfers?'))

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

async function openForm(options?: Parameters<typeof mockApi>[0]) {
  const calls = mockApi(options)
  render(<Transfers workspaceId="f1" />)
  await screen.findByText(/Pró-labore/)
  fireEvent.click(screen.getByRole('button', { name: 'Nova transferência' }))
  await screen.findByLabelText('De (origem)')
  return calls
}

function fill(fields: Partial<Record<'De (origem)' | 'Para (destino)' | 'Valor (R$)' | 'Descrição', string>>) {
  for (const [label, value] of Object.entries(fields)) {
    fireEvent.change(screen.getByLabelText(label), { target: { value } })
  }
}

describe('Transfers — lista', () => {
  it('mostra "De [conta] para [conta]" com escopo, valor exato e conta inativa identificada', async () => {
    const calls = mockApi()
    render(<Transfers workspaceId="f1" />)

    const item = (await screen.findByText(/^De Itaú Empresas \(PJ\) para Conta antiga \(PF\) — inativa$/)).closest('li')!
    expect(item.textContent).toMatch(/R\$\s500,00/)
    expect(item.textContent).not.toMatch(/[+−]R\$/) // não é receita nem despesa
    expect(within(item).getByText('05/10/2026')).toBeTruthy()
    expect(screen.getByText(/não é receita nem despesa/i)).toBeTruthy()
    expect(listCalls(calls)[0].url).toBe('/api/financial-workspaces/f1/transfers?page=1&pageSize=20')
  })

  it('mostra canceladas só sob pedido, sem botão de cancelar', async () => {
    const calls = mockApi()
    render(<Transfers workspaceId="f1" />)
    await screen.findByText(/Pró-labore/)
    expect(screen.queryByText('Errada')).toBeNull()

    fireEvent.click(screen.getByLabelText('Mostrar canceladas'))

    const item = (await screen.findByText('Errada')).closest('li')!
    expect(within(item).getByText('Cancelada')).toBeTruthy()
    expect(within(item).queryByRole('button', { name: 'Cancelar transferência' })).toBeNull()
    expect(listCalls(calls).at(-1)!.url).toContain('includeCancelled=true')
  })

  it('filtra por período e conta (origem ou destino) e pagina', async () => {
    const calls = mockApi({ page: { totalCount: 45 } })
    render(<Transfers workspaceId="f1" />)
    expect(await screen.findByText(/Página 1 de 3 · 45 transferências/)).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Próxima' }))
    await waitFor(() => expect(listCalls(calls).at(-1)!.url).toContain('page=2'))

    fireEvent.change(screen.getByLabelText('De'), { target: { value: '2026-09-01' } })
    fireEvent.change(screen.getByLabelText('Até'), { target: { value: '2026-09-30' } })
    fireEvent.change(screen.getByLabelText('Conta (origem ou destino)'), { target: { value: pessoal.id } })

    await waitFor(() => expect(listCalls(calls).at(-1)!.url).toContain(`accountId=${pessoal.id}`))
    expect(Object.fromEntries(new URL(listCalls(calls).at(-1)!.url, 'http://x').searchParams)).toEqual({
      page: '1',
      pageSize: '20',
      from: '2026-09-01',
      to: '2026-09-30',
      accountId: pessoal.id,
    })
  })

  it('cancelar pede confirmação, usa CSRF e avisa os saldos', async () => {
    const calls = mockApi()
    const onChanged = vi.fn()
    const confirm = vi.spyOn(window, 'confirm')
    render(<Transfers workspaceId="f1" onChanged={onChanged} />)
    const item = (await screen.findByText(/Pró-labore/)).closest('li')!

    confirm.mockReturnValueOnce(false)
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar transferência' }))
    expect(writes(calls)).toHaveLength(0)

    confirm.mockReturnValueOnce(true)
    fireEvent.click(within(item).getByRole('button', { name: 'Cancelar transferência' }))

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1))
    expect(confirm).toHaveBeenLastCalledWith(expect.stringContaining('Os saldos das duas contas voltam'))
    expect(writes(calls)[0].url).toBe('/api/financial-workspaces/f1/transfers/t1/cancel')
    expect((writes(calls)[0].init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
  })
})

describe('Transfers — formulário', () => {
  it('cria PJ → PF com valor em texto exato, sem categoria, e avisa os saldos', async () => {
    const calls = mockApi()
    const onChanged = vi.fn()
    render(<Transfers workspaceId="f1" onChanged={onChanged} />)
    await screen.findByText(/Pró-labore/)
    fireEvent.click(screen.getByRole('button', { name: 'Nova transferência' }))
    await screen.findByLabelText('De (origem)')
    expect((screen.getByLabelText('Data da transferência') as HTMLInputElement).value).toBe(todayInSaoPaulo())
    expect(screen.queryByLabelText(/Categoria/)).toBeNull()

    fill({ 'De (origem)': empresas.id, 'Para (destino)': pessoal.id, 'Valor (R$)': '1.500,00', Descrição: ' Pró-labore ' })
    fireEvent.change(screen.getByLabelText('Data da transferência'), { target: { value: '2026-10-05' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1))
    const post = writes(calls)[0]
    expect(post.url).toBe('/api/financial-workspaces/f1/transfers')
    expect(JSON.parse(String(post.init!.body))).toEqual({
      sourceAccountId: empresas.id,
      destinationAccountId: pessoal.id,
      amount: '1500.00',
      transferDate: '2026-10-05',
      description: 'Pró-labore',
    })
  })

  it('só oferece contas ativas', async () => {
    await openForm()

    const options = Array.from((screen.getByLabelText('De (origem)') as HTMLSelectElement).options).map((o) => o.value)
    expect(options).toEqual(['', empresas.id, pessoal.id])
  })

  it.each([
    [{ 'De (origem)': 'acc-pj', 'Para (destino)': 'acc-pj', 'Valor (R$)': '10' }, 'A conta de destino deve ser diferente da origem.'],
    [{ 'De (origem)': 'acc-pj', 'Para (destino)': 'acc-pf', 'Valor (R$)': '0,00' }, 'O valor deve ser maior que zero.'],
    [{ 'De (origem)': 'acc-pj', 'Para (destino)': 'acc-pf', 'Valor (R$)': '-5' }, 'O valor deve ser maior que zero.'],
    [{ 'De (origem)': 'acc-pj', 'Para (destino)': 'acc-pf', 'Valor (R$)': '10,005' }, 'O valor deve ter no máximo duas casas decimais.'],
  ])('recusa %o sem enviar', async (fields, message) => {
    const calls = await openForm()

    fill({ ...fields, Descrição: 'X' })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText(message)).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)
  })

  it('recusa data futura em America/Sao_Paulo', async () => {
    const calls = await openForm()
    const [year, month, day] = todayInSaoPaulo().split('-').map(Number)
    const tomorrow = new Date(Date.UTC(year, month - 1, day + 1)).toISOString().slice(0, 10)

    fill({ 'De (origem)': empresas.id, 'Para (destino)': pessoal.id, 'Valor (R$)': '10', Descrição: 'X' })
    fireEvent.change(screen.getByLabelText('Data da transferência'), { target: { value: tomorrow } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('A data não pode ser futura: registre só transferências já realizadas.')).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)
  })

  it('orienta quando há menos de duas contas ativas', async () => {
    await openForm({ accounts: [empresas, antiga] })

    expect(screen.getByText(/São necessárias duas contas ativas/)).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Salvar' }) as HTMLButtonElement).disabled).toBe(true)
  })

  it('exibe erro da API e não avisa os saldos', async () => {
    mockApi({ saveStatus: 400, saveBody: { errors: { sourceAccountId: ['Conta de origem inválida.'] } } })
    const onChanged = vi.fn()
    render(<Transfers workspaceId="f1" onChanged={onChanged} />)
    await screen.findByText(/Pró-labore/)
    fireEvent.click(screen.getByRole('button', { name: 'Nova transferência' }))
    await screen.findByLabelText('De (origem)')

    fill({ 'De (origem)': empresas.id, 'Para (destino)': pessoal.id, 'Valor (R$)': '10', Descrição: 'X' })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    expect(await screen.findByText('Conta de origem inválida.')).toBeTruthy()
    expect(onChanged).not.toHaveBeenCalled()
  })
})

describe('Transfers — vínculo da integração bancária', () => {
  const integrated: FinancialTransfer = {
    ...prolabore, id: 't9', description: 'Pix enviado', sourceAccount: { id: empresas.id, name: 'Itaú PJ', financialScope: 'PJ', isActive: true },
    destinationAccount: { id: pessoal.id, name: 'Itaú PF', financialScope: 'PF', isActive: true }, integration: { postingId: 'post-1', origin: 'Inferred', ruleVersion: 'transfer-v2' },
  }

  it('identifica o vínculo inferido e confirmado, e apresenta recebida na PF e enviada na PJ quando há conta filtrada', async () => {
    mockApi({ page: { items: [integrated, { ...integrated, id: 't10', integration: { postingId: 'post-2', origin: 'UserConfirmed', ruleVersion: null } }], totalCount: 2 } })
    render(<Transfers workspaceId="f1" />)

    const items = (await screen.findAllByText(/^De Itaú PJ \(PJ\) para Itaú PF \(PF\)$/)).map((node) => node.closest('li')!)
    expect(within(items[0]).getByText('Integração bancária · Inferido pela regra automática')).toBeTruthy()
    expect(within(items[1]).getByText('Integração bancária · Confirmado por você')).toBeTruthy()
    expect(items[0].textContent).not.toMatch(/[+−]R\$/)

    fireEvent.change(screen.getByLabelText('Conta (origem ou destino)'), { target: { value: pessoal.id } })
    expect((await screen.findAllByText('Transferência recebida da Itaú PJ')).length).toBe(2)
    fireEvent.change(screen.getByLabelText('Conta (origem ou destino)'), { target: { value: empresas.id } })
    expect((await screen.findAllByText('Transferência enviada para Itaú PF')).length).toBe(2)
  })

  it('desfaz o vínculo pelo rastro da ponta, depois de confirmar, e recarrega a lista', async () => {
    const calls = mockApi({ page: { items: [integrated], totalCount: 1 } })
    pluggy.undoTransfer.mockResolvedValue({ outcome: 'Done', summary: null })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const onChanged = vi.fn()
    render(<Transfers workspaceId="f1" onChanged={onChanged} />)

    fireEvent.click(await screen.findByRole('button', { name: 'Desfazer vínculo' }))

    await waitFor(() => expect(pluggy.undoTransfer).toHaveBeenCalledWith('f1', 'post-1'))
    await waitFor(() => expect(listCalls(calls).length).toBeGreaterThan(1))
    expect(onChanged).toHaveBeenCalled()
  })

  it('transferência cadastrada à mão não oferece desfazer vínculo', async () => {
    mockApi()
    render(<Transfers workspaceId="f1" />)
    await screen.findByText(/Pró-labore/)
    expect(screen.queryByRole('button', { name: 'Desfazer vínculo' })).toBeNull()
  })
})
