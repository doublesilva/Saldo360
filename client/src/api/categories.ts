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

/** Quantos registros de uma origem (ex.: "Lançamentos") usam a categoria ou alguma das suas subcategorias. */
export interface CategoryReference {
  source: string
  label: string
  count: number
}

/** Configuração ainda ativa que depende da categoria (hoje, recorrências ativas ou pausadas). */
export interface CategoryDependency {
  kind: string
  id: string
  description: string
}

/** Efeito de excluir ou inativar. Numa principal, os números incluem as suas subcategorias. */
export interface CategoryImpact {
  categoryId: string
  name: string
  isSubcategory: boolean
  subcategoryCount: number
  references: CategoryReference[]
  activeDependencies: CategoryDependency[]
  totalReferences: number
  canDelete: boolean
  canDeactivate: boolean
}

/** Erros por campo (400), nome duplicado (409, em `name`) ou inativação impedida por configurações ativas (409, com `blocked`). */
export type SaveCategoryResult =
  | { ok: true; category: FinancialCategory }
  | { ok: false; errors: Record<string, string[]>; blocked?: CategoryImpact }

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
    const problem = (await response.json().catch(() => ({}))) as { title?: string; code?: string; impact?: CategoryImpact }
    if (problem.code === 'CATEGORY_HAS_ACTIVE_DEPENDENCIES' && problem.impact) {
      return { ok: false, errors: { isActive: [problem.title ?? 'Há configurações ativas que usam esta categoria.'] }, blocked: problem.impact }
    }
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

/** Efeito de excluir ou inativar a categoria (registros vinculados e configurações ativas dependentes). */
export async function getCategoryImpact(workspaceId: string, categoryId: string): Promise<CategoryImpact> {
  const response = await fetch(`${categoriesUrl(workspaceId, categoryId)}/impact`, { headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao verificar o uso da categoria (HTTP ${response.status}).`)
  return (await response.json()) as CategoryImpact
}

/**
 * Exclusão: só ocorre sem vínculos. `in-use` traz o impacto (a alternativa é inativar); `confirmation-required` indica uma principal com
 * subcategorias sem `confirmSubcategories`.
 */
export type DeleteCategoryResult =
  | { kind: 'deleted' }
  | { kind: 'in-use'; impact: CategoryImpact }
  | { kind: 'confirmation-required'; impact: CategoryImpact }

export async function deleteCategory(
  workspaceId: string,
  categoryId: string,
  confirmSubcategories = false,
): Promise<DeleteCategoryResult> {
  const url = `${categoriesUrl(workspaceId, categoryId)}${confirmSubcategories ? '?confirmSubcategories=true' : ''}`
  const response = await fetch(url, { method: 'DELETE', headers: { ...jsonHeaders, [CSRF_HEADER]: await getCsrfToken() } })
  if (response.status === 204) return { kind: 'deleted' }
  if (response.status === 409) {
    const problem = (await response.json().catch(() => ({}))) as { code?: string; impact?: CategoryImpact }
    if (problem.impact) {
      return { kind: problem.code === 'CATEGORY_DELETE_CONFIRMATION_REQUIRED' ? 'confirmation-required' : 'in-use', impact: problem.impact }
    }
  }
  if (response.status === 404) throw new Error('Categoria ou orçamento não encontrado.')
  throw new Error(`Falha ao excluir a categoria (HTTP ${response.status}).`)
}

/** O que acontece com um item do catálogo: criado, já existente (reutilizado) ou mantido inativo/ignorado. */
export type DefaultCategoryAction = 'Create' | 'Exists' | 'ExistsInactive' | 'SkippedParentInactive'

export interface DefaultSubcategoryPlan {
  name: string
  action: DefaultCategoryAction
  existingId?: string | null
}

export interface DefaultCategoryPlan {
  name: string
  type: CategoryType
  action: DefaultCategoryAction
  existingId?: string | null
  subcategories: DefaultSubcategoryPlan[]
}

export interface DefaultCategoriesPreview {
  catalogVersion: number
  categoriesToCreate: number
  subcategoriesToCreate: number
  categories: DefaultCategoryPlan[]
}

export interface DefaultCategoriesApplied {
  categoriesCreated: number
  subcategoriesCreated: number
  applied: DefaultCategoriesPreview
}

/** Pré-visualização de "Adicionar categorias padrão": não altera nada. */
export async function previewDefaultCategories(workspaceId: string, signal?: AbortSignal): Promise<DefaultCategoriesPreview> {
  const response = await fetch(`${categoriesUrl(workspaceId)}/defaults`, { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar as categorias padrão (HTTP ${response.status}).`)
  return (await response.json()) as DefaultCategoriesPreview
}

/** Acrescenta o que falta do catálogo; repetir não cria duplicatas e nada existente é alterado. */
export async function applyDefaultCategories(workspaceId: string): Promise<DefaultCategoriesApplied> {
  const response = await fetch(`${categoriesUrl(workspaceId)}/defaults`, {
    method: 'POST',
    headers: { ...jsonHeaders, [CSRF_HEADER]: await getCsrfToken() },
  })
  if (!response.ok) throw new Error(`Falha ao adicionar as categorias padrão (HTTP ${response.status}).`)
  return (await response.json()) as DefaultCategoriesApplied
}
