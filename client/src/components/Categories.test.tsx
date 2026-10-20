import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialCategory } from '../api/categories'
import { Categories } from './Categories'

const salario: FinancialCategory = {
  id: 'c1',
  name: 'Salário',
  type: 'Income',
  isActive: true,
  createdAt: '2026-10-10T00:00:00Z',
}
const mercado: FinancialCategory = { ...salario, id: 'c2', name: 'Mercado', type: 'Expense' }
const academia: FinancialCategory = { ...salario, id: 'c3', name: 'Academia', type: 'Expense', isActive: false }

type Call = { url: string; init?: RequestInit }

/** API simulada: lista conforme includeInactive, CSRF, gravações e respostas de erro opcionais. */
function mockApi(options: { active?: FinancialCategory[]; saveStatus?: number; saveBody?: unknown } = {}) {
  const active = options.active ?? [salario, mercado]
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (init?.method === 'POST' || init?.method === 'PUT') {
        if (options.saveStatus) return Response.json(options.saveBody ?? {}, { status: options.saveStatus })
        const body = JSON.parse(String(init.body))
        return Response.json({ ...mercado, ...body }, { status: init.method === 'POST' ? 201 : 200 })
      }
      return Response.json(url.includes('includeInactive=true') ? [...active, academia] : active)
    }),
  )
  return calls
}

const saves = (calls: Call[]) => calls.filter((c) => c.init?.method === 'POST' || c.init?.method === 'PUT')

function section(title: 'Receitas' | 'Despesas') {
  return screen.getByRole('heading', { name: title }).closest('section')!
}

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('Categories', () => {
  it('separa receitas e despesas e começa sem criar nada', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)

    await screen.findByText('Salário')
    expect(within(section('Receitas')).getByText('Salário')).toBeTruthy()
    expect(within(section('Receitas')).queryByText('Mercado')).toBeNull()
    expect(within(section('Despesas')).getByText('Mercado')).toBeTruthy()
    expect(calls.map((c) => c.url)).toEqual(['/api/financial-workspaces/f1/categories'])
  })

  it('lista vazia não cria categorias automaticamente', async () => {
    const calls = mockApi({ active: [] })
    render(<Categories workspaceId="f1" />)

    expect(await screen.findByText('Nenhuma categoria de receita ativa cadastrada.')).toBeTruthy()
    expect(screen.getByText('Nenhuma categoria de despesa ativa cadastrada.')).toBeTruthy()
    expect(saves(calls)).toHaveLength(0)
  })

  it('mostra inativas somente quando solicitado', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)
    await screen.findByText('Mercado')
    expect(screen.queryByText('Academia')).toBeNull()

    fireEvent.click(screen.getByLabelText('Mostrar categorias inativas'))

    expect(await screen.findByText('Academia')).toBeTruthy()
    expect(calls.at(-1)!.url).toBe('/api/financial-workspaces/f1/categories?includeInactive=true')
    const item = screen.getByText('Academia').closest('li')!
    expect(within(item).getByText('(inativa)')).toBeTruthy()
    expect(within(item).getByRole('button', { name: 'Reativar' })).toBeTruthy()
  })

  it('cria despesa com CSRF, nome sem espaços nas pontas e tipo da seção', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)
    await screen.findByText('Mercado')

    fireEvent.change(screen.getByLabelText('Nova categoria de despesa'), { target: { value: '  Farmácia ' } })
    fireEvent.click(within(section('Despesas')).getByRole('button', { name: 'Adicionar' }))

    await waitFor(() => expect(saves(calls)).toHaveLength(1))
    const post = saves(calls)[0]
    expect(post.url).toBe('/api/financial-workspaces/f1/categories')
    expect((post.init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    expect(JSON.parse(String(post.init!.body))).toEqual({ name: 'Farmácia', type: 'Expense' })
  })

  it('cria receita pela seção de receitas', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)
    await screen.findByText('Salário')

    fireEvent.change(screen.getByLabelText('Nova categoria de receita'), { target: { value: 'Freelance' } })
    fireEvent.click(within(section('Receitas')).getByRole('button', { name: 'Adicionar' }))

    await waitFor(() => expect(saves(calls)).toHaveLength(1))
    expect(JSON.parse(String(saves(calls)[0].init!.body))).toEqual({ name: 'Freelance', type: 'Income' })
  })

  it('nome em branco não é enviado', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)
    await screen.findByText('Mercado')

    fireEvent.change(screen.getByLabelText('Nova categoria de despesa'), { target: { value: '   ' } })
    fireEvent.click(within(section('Despesas')).getByRole('button', { name: 'Adicionar' }))

    expect(await screen.findByText('Informe o nome da categoria.')).toBeTruthy()
    expect(saves(calls)).toHaveLength(0)
  })

  it('mostra a mensagem de duplicidade (409) devolvida pela API', async () => {
    mockApi({ saveStatus: 409, saveBody: { title: 'Já existe uma categoria com esse nome e tipo nesta família (ativa ou inativa).' } })
    render(<Categories workspaceId="f1" />)
    await screen.findByText('Mercado')

    fireEvent.change(screen.getByLabelText('Nova categoria de despesa'), { target: { value: 'mercado' } })
    fireEvent.click(within(section('Despesas')).getByRole('button', { name: 'Adicionar' }))

    expect(await screen.findByText(/Já existe uma categoria com esse nome e tipo/)).toBeTruthy()
    expect(screen.getByLabelText('Nova categoria de despesa').getAttribute('aria-invalid')).toBe('true')
  })

  it('renomeia enviando só nome e situação, nunca o tipo', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)
    const item = (await screen.findByText('Mercado')).closest('li')!

    fireEvent.click(within(item).getByRole('button', { name: 'Renomear' }))
    fireEvent.change(screen.getByLabelText('Novo nome de “Mercado”'), { target: { value: 'Supermercado' } })
    fireEvent.click(screen.getByRole('button', { name: 'Salvar' }))

    await waitFor(() => expect(saves(calls)).toHaveLength(1))
    const put = saves(calls)[0]
    expect(put.url).toBe('/api/financial-workspaces/f1/categories/c2')
    expect(put.init!.method).toBe('PUT')
    expect(JSON.parse(String(put.init!.body))).toEqual({ name: 'Supermercado', isActive: true })
  })

  it('cancelar a renomeação não envia nada', async () => {
    const calls = mockApi()
    render(<Categories workspaceId="f1" />)
    const item = (await screen.findByText('Mercado')).closest('li')!

    fireEvent.click(within(item).getByRole('button', { name: 'Renomear' }))
    fireEvent.click(screen.getByRole('button', { name: 'Cancelar' }))

    expect(await screen.findByText('Mercado')).toBeTruthy()
    expect(saves(calls)).toHaveLength(0)
  })

  it('desativar pede confirmação e envia isActive=false', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm')
    render(<Categories workspaceId="f1" />)
    const item = (await screen.findByText('Mercado')).closest('li')!

    confirm.mockReturnValueOnce(false)
    fireEvent.click(within(item).getByRole('button', { name: 'Desativar' }))
    expect(saves(calls)).toHaveLength(0)

    confirm.mockReturnValueOnce(true)
    fireEvent.click(within(item).getByRole('button', { name: 'Desativar' }))

    await waitFor(() => expect(saves(calls)).toHaveLength(1))
    expect(confirm).toHaveBeenLastCalledWith(expect.stringContaining('Desativar a categoria "Mercado"'))
    expect(JSON.parse(String(saves(calls)[0].init!.body))).toEqual({ name: 'Mercado', isActive: false })
  })

  it('reativar também pede confirmação', async () => {
    const calls = mockApi()
    const confirm = vi.spyOn(window, 'confirm')
    render(<Categories workspaceId="f1" />)
    await screen.findByText('Mercado')
    fireEvent.click(screen.getByLabelText('Mostrar categorias inativas'))
    const item = (await screen.findByText('Academia')).closest('li')!

    confirm.mockReturnValueOnce(false)
    fireEvent.click(within(item).getByRole('button', { name: 'Reativar' }))
    expect(saves(calls)).toHaveLength(0)

    confirm.mockReturnValueOnce(true)
    fireEvent.click(within(item).getByRole('button', { name: 'Reativar' }))

    await waitFor(() => expect(saves(calls)).toHaveLength(1))
    expect(confirm).toHaveBeenLastCalledWith('Reativar a categoria "Academia"?')
    expect(JSON.parse(String(saves(calls)[0].init!.body))).toEqual({ name: 'Academia', isActive: true })
  })
})
