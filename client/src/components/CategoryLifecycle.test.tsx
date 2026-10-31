import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CategoryImpact, DefaultCategoriesPreview, FinancialCategory } from '../api/categories'
import { Categories } from './Categories'

const base = '/api/financial-workspaces/f1/categories'
const moradia: FinancialCategory = { id: 'c1', name: 'Moradia', type: 'Expense', isActive: true, createdAt: '2026-10-10T00:00:00Z' }
const energia: FinancialCategory = {
  id: 'c2',
  name: 'Energia',
  type: 'Expense',
  isActive: true,
  createdAt: '2026-10-10T00:00:00Z',
  parentCategoryId: 'c1',
  parentName: 'Moradia',
}
const lazer: FinancialCategory = { ...moradia, id: 'c3', name: 'Lazer' }

const noImpact = (category: FinancialCategory, subcategoryCount = 0): CategoryImpact => ({
  categoryId: category.id,
  name: category.name,
  isSubcategory: Boolean(category.parentCategoryId),
  subcategoryCount,
  references: [],
  activeDependencies: [],
  totalReferences: 0,
  canDelete: true,
  canDeactivate: true,
})

type Call = { url: string; init?: RequestInit }

interface Options {
  categories?: FinancialCategory[]
  impacts?: Record<string, CategoryImpact>
  deleteResponse?: () => Response
  putResponse?: () => Response
  preview?: DefaultCategoriesPreview
}

function mockApi(options: Options = {}) {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url.endsWith('/impact')) return Response.json(options.impacts?.[url.split('/').at(-2)!])
      if (url.endsWith('/defaults') && init?.method === 'POST')
        return Response.json({ categoriesCreated: 12, subcategoriesCreated: 30, applied: options.preview })
      if (url.endsWith('/defaults')) return Response.json(options.preview)
      if (init?.method === 'DELETE') return options.deleteResponse?.() ?? new Response(null, { status: 204 })
      if (init?.method === 'PUT') return options.putResponse?.() ?? Response.json({ ...moradia, isActive: false })
      return Response.json(options.categories ?? [moradia, energia, lazer])
    }),
  )
  return calls
}

const writes = (calls: Call[]) => calls.filter((c) => ['POST', 'PUT', 'DELETE'].includes(c.init?.method ?? ''))
const itemOf = async (name: string) => (await screen.findByText(name)).closest('li')!

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('Categories: excluir e inativar com proteção de vínculos', () => {
  it('exclui uma categoria sem vínculos após confirmar, com CSRF', async () => {
    const calls = mockApi({ impacts: { c3: noImpact(lazer) } })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Categories workspaceId="f1" />)

    fireEvent.click(within(await itemOf('Lazer')).getByRole('button', { name: 'Excluir' }))

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Excluir "Lazer"?'))
    const [request] = writes(calls)
    expect(request.url).toBe(`${base}/c3`)
    expect(request.init!.method).toBe('DELETE')
    expect((request.init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
  })

  it('não exclui quando o usuário não confirma', async () => {
    const calls = mockApi({ impacts: { c3: noImpact(lazer) } })
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<Categories workspaceId="f1" />)

    fireEvent.click(within(await itemOf('Lazer')).getByRole('button', { name: 'Excluir' }))

    await waitFor(() => expect(calls.some((c) => c.url.endsWith('/c3/impact'))).toBe(true))
    expect(writes(calls)).toHaveLength(0)
  })

  it('mostra o impacto nas subcategorias e só então envia a confirmação explícita', async () => {
    const calls = mockApi({ impacts: { c1: noImpact(moradia, 1) } })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Categories workspaceId="f1" />)

    fireEvent.click(within(await itemOf('Moradia')).getAllByRole('button', { name: 'Excluir' })[0])

    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('e as suas 1 subcategorias'))
    expect(writes(calls)[0].url).toBe(`${base}/c1?confirmSubcategories=true`)
  })

  it('com vínculos explica o impedimento, lista os registros e oferece inativar sem apagar nada', async () => {
    const used: CategoryImpact = {
      ...noImpact(lazer),
      references: [
        { source: 'transactions', label: 'Lançamentos', count: 12 },
        { source: 'plannedTransactions', label: 'Previsões', count: 3 },
      ],
      totalReferences: 15,
      canDelete: false,
    }
    const calls = mockApi({ impacts: { c3: used } })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Categories workspaceId="f1" />)

    fireEvent.click(within(await itemOf('Lazer')).getByRole('button', { name: 'Excluir' }))

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('Não é possível excluir "Lazer"')
    expect(alert.textContent).toContain('Os registros não são apagados nem reclassificados')
    expect(within(alert).getByText('Lançamentos: 12')).toBeTruthy()
    expect(within(alert).getByText('Previsões: 3')).toBeTruthy()
    expect(writes(calls)).toHaveLength(0)

    fireEvent.click(within(alert).getByRole('button', { name: 'Inativar "Lazer"' }))
    await waitFor(() => expect(writes(calls)).toHaveLength(1))
    expect(writes(calls)[0].init!.method).toBe('PUT')
    expect(JSON.parse(String(writes(calls)[0].init!.body))).toEqual({ name: 'Lazer', isActive: false })
  })

  it('inativação impedida por recorrência ativa mostra as configurações que precisam ser resolvidas', async () => {
    const blocked: CategoryImpact = {
      ...noImpact(lazer),
      activeDependencies: [{ kind: 'Recurrence', id: 'r1', description: 'Clube mensal' }],
      canDeactivate: false,
    }
    mockApi({
      putResponse: () =>
        Response.json(
          { code: 'CATEGORY_HAS_ACTIVE_DEPENDENCIES', title: 'Há configurações ativas que usam esta categoria.', impact: blocked },
          { status: 409 },
        ),
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Categories workspaceId="f1" />)

    fireEvent.click(within(await itemOf('Lazer')).getByRole('button', { name: 'Desativar' }))

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('Há configurações ativas que usam esta categoria.')
    expect(within(alert).getByText('Recorrência: Clube mensal')).toBeTruthy()
  })

  it('um vínculo surgido durante a exclusão (409) mostra o impedimento em vez de falhar em silêncio', async () => {
    const used: CategoryImpact = {
      ...noImpact(lazer),
      references: [{ source: 'transactions', label: 'Lançamentos', count: 1 }],
      totalReferences: 1,
      canDelete: false,
    }
    mockApi({
      impacts: { c3: noImpact(lazer) },
      deleteResponse: () => Response.json({ code: 'CATEGORY_IN_USE', title: 'Em uso', impact: used }, { status: 409 }),
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<Categories workspaceId="f1" />)

    fireEvent.click(within(await itemOf('Lazer')).getByRole('button', { name: 'Excluir' }))

    expect((await screen.findByRole('alert')).textContent).toContain('Lançamentos: 1')
  })
})

describe('Categories: Adicionar categorias padrão', () => {
  const preview: DefaultCategoriesPreview = {
    catalogVersion: 1,
    categoriesToCreate: 12,
    subcategoriesToCreate: 30,
    categories: [
      {
        name: 'Alimentação',
        type: 'Expense',
        action: 'Exists',
        existingId: 'x',
        subcategories: [
          { name: 'Mercado e feira', action: 'Exists', existingId: 'y' },
          { name: 'Refeições fora e delivery', action: 'Create' },
        ],
      },
      {
        name: 'Moradia',
        type: 'Expense',
        action: 'ExistsInactive',
        existingId: 'z',
        subcategories: [{ name: 'Contas da casa', action: 'SkippedParentInactive' }],
      },
      { name: 'Rendimentos', type: 'Income', action: 'Create', subcategories: [{ name: 'Rendimentos financeiros', action: 'Create' }] },
    ],
  }

  it('mostra antes o que será adicionado e não grava nada até confirmar', async () => {
    const calls = mockApi({ preview })
    render(<Categories workspaceId="f1" />)
    await itemOf('Lazer')

    fireEvent.click(screen.getByRole('button', { name: 'Adicionar categorias padrão' }))

    const region = await screen.findByRole('region', { name: 'Categorias padrão a adicionar' })
    expect(region.textContent).toContain('Serão adicionadas 12 categorias e 30 subcategorias')
    expect(within(region).getByText('Mercado e feira').closest('li')!.textContent).toContain('já existe')
    expect(within(region).getByText('Moradia').closest('li')!.textContent).toContain('inativa (não será reativada)')
    expect(within(region).getByText('Refeições fora e delivery').closest('li')!.textContent).toContain('será adicionada')
    expect(within(region).getByText('Rendimentos').closest('li')!.textContent).toContain('será adicionada')
    expect(writes(calls)).toHaveLength(0)
  })

  it('aplica só após o clique, com CSRF, informa o resultado e recarrega a lista', async () => {
    const calls = mockApi({ preview })
    render(<Categories workspaceId="f1" />)
    await itemOf('Lazer')
    fireEvent.click(screen.getByRole('button', { name: 'Adicionar categorias padrão' }))
    const region = await screen.findByRole('region', { name: 'Categorias padrão a adicionar' })

    fireEvent.click(within(region).getByRole('button', { name: 'Adicionar ao orçamento' }))

    expect((await screen.findByRole('status')).textContent).toContain('Adicionadas 12 categorias e 30 subcategorias')
    const posts = writes(calls)
    expect(posts).toHaveLength(1)
    expect(posts[0].url).toBe(`${base}/defaults`)
    expect((posts[0].init!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    expect(calls.filter((c) => c.url === base)).toHaveLength(2)
  })

  it('sem nada a adicionar, informa e não permite aplicar', async () => {
    mockApi({
      preview: {
        catalogVersion: 1,
        categoriesToCreate: 0,
        subcategoriesToCreate: 0,
        categories: [{ name: 'Alimentação', type: 'Expense', action: 'Exists', subcategories: [] }],
      },
    })
    render(<Categories workspaceId="f1" />)
    await itemOf('Lazer')

    fireEvent.click(screen.getByRole('button', { name: 'Adicionar categorias padrão' }))

    const region = await screen.findByRole('region', { name: 'Categorias padrão a adicionar' })
    expect(region.textContent).toContain('Nada será adicionado')
    expect((within(region).getByRole('button', { name: 'Adicionar ao orçamento' }) as HTMLButtonElement).disabled).toBe(true)
  })
})
