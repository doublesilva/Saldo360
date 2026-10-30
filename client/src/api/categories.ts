import { CSRF_HEADER, getCsrfToken } from './csrf'

export type CategoryType = 'Income' | 'Expense'

export const CATEGORY_NAME_MAX_LENGTH = 100

/**
 * Categoria de receita ou despesa da família. O tipo não muda depois de criada. Há no máximo dois níveis: a principal e a subcategoria
 * (`parentCategoryId` preenchido, mesmo tipo da principal). `isAvailable` diz se pode receber NOVAS classificações (ativa e, numa
 * subcategoria, com a principal também ativa); registros já classificados continuam legíveis de qualquer forma.
 */
export interface FinancialCategory {
  id: string
  name: string
  type: CategoryType
  isActive: boolean
  createdAt: string
  parentCategoryId?: string | null
  parentName?: string | null
  isAvailable?: boolean
  /** "Categoria / Subcategoria", ou só o nome numa categoria principal. */
  path?: string
}

/** Erros por campo (400) ou nome duplicado (409, em `name`). */
export type SaveCategoryResult =
  | { ok: true; category: FinancialCategory }
  | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

function categoriesUrl(workspaceId: string, categoryId?: string): string {
  const base = `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/categories`
  return categoryId ? `${base}/${encodeURIComponent(categoryId)}` : base
}

export async function listCategories(
  workspaceId: string,
  includeInactive: boolean,
  signal?: AbortSignal,
): Promise<FinancialCategory[]> {
  const url = includeInactive ? `${categoriesUrl(workspaceId)}?includeInactive=true` : categoriesUrl(workspaceId)
  const response = await fetch(url, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar as categorias (HTTP ${response.status}).`)
  return (await response.json()) as FinancialCategory[]
}

async function send(method: 'POST' | 'PUT', url: string, body: unknown, expected: number): Promise<SaveCategoryResult> {
  const response = await fetch(url, {
    method,
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() },
    body: JSON.stringify(body),
  })

  if (response.status === expected) return { ok: true, category: (await response.json()) as FinancialCategory }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  if (response.status === 409) {
    const problem = (await response.json().catch(() => ({}))) as { title?: string }
    return { ok: false, errors: { name: [problem.title ?? 'Já existe uma categoria com esse nome neste nível.'] } }
  }
  if (response.status === 404) throw new Error('Categoria ou orçamento não encontrado.')
  throw new Error(`Falha ao salvar a categoria (HTTP ${response.status}).`)
}

/** Cria uma categoria principal ou, com `parentCategoryId`, uma subcategoria (o tipo é o da principal e não é enviado). */
export function createCategory(
  workspaceId: string,
  name: string,
  type: CategoryType,
  parentCategoryId?: string,
): Promise<SaveCategoryResult> {
  return send('POST', categoriesUrl(workspaceId), parentCategoryId ? { name, parentCategoryId } : { name, type }, 201)
}

/** Renomeia e/ou ativa/desativa. O tipo não é enviado: é imutável. */
export function updateCategory(
  workspaceId: string,
  categoryId: string,
  name: string,
  isActive: boolean,
): Promise<SaveCategoryResult> {
  return send('PUT', categoriesUrl(workspaceId, categoryId), { name, isActive }, 200)
}
