import type { CategoryType, FinancialCategory } from './api/categories'

/** Pode receber NOVAS classificações: ativa e, numa subcategoria, com a principal também ativa. */
export function isAvailable(category: FinancialCategory): boolean {
  return category.isAvailable ?? category.isActive
}

export function isSubcategory(category: FinancialCategory): boolean {
  return Boolean(category.parentCategoryId)
}

/** "Categoria / Subcategoria", ou só o nome numa categoria principal. */
export function categoryPath(category: Pick<FinancialCategory, 'name' | 'path' | 'parentName'>): string {
  return category.path ?? (category.parentName ? `${category.parentName} / ${category.name}` : category.name)
}

const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name, 'pt-BR', { sensitivity: 'base' })

export interface CategoryGroup {
  /** A principal; null quando ela não veio na lista (principal inativa oculta), caso em que `parentName` identifica o grupo. */
  main: FinancialCategory | null
  parentId: string
  parentName: string
  subs: FinancialCategory[]
}

/** Principais em ordem alfabética, cada uma com as suas subcategorias. Subcategoria de principal ausente da lista forma grupo próprio. */
export function groupCategories(categories: FinancialCategory[], type?: CategoryType): CategoryGroup[] {
  const scoped = type ? categories.filter((category) => category.type === type) : categories
  const groups = new Map<string, CategoryGroup>()
  for (const main of scoped.filter((category) => !isSubcategory(category)).sort(byName)) {
    groups.set(main.id, { main, parentId: main.id, parentName: main.name, subs: [] })
  }
  for (const sub of scoped.filter(isSubcategory).sort(byName)) {
    const parentId = sub.parentCategoryId!
    const group = groups.get(parentId) ?? { main: null, parentId, parentName: sub.parentName ?? 'Categoria', subs: [] }
    group.subs.push(sub)
    groups.set(parentId, group)
  }
  return [...groups.values()].sort((a, b) => a.parentName.localeCompare(b.parentName, 'pt-BR', { sensitivity: 'base' }))
}

/** Lista plana ordenada (principal, depois suas subcategorias). */
export function orderCategories(categories: FinancialCategory[], type?: CategoryType): FinancialCategory[] {
  return groupCategories(categories, type).flatMap((group) => (group.main ? [group.main, ...group.subs] : group.subs))
}

/**
 * Categorias que o usuário pode escolher num formulário: as disponíveis do tipo, mais a que o registro já usa (`keepId`), mesmo indisponível,
 * para que uma edição não obrigue a trocar a classificação.
 */
export function selectableCategories(categories: FinancialCategory[], type: CategoryType, keepId?: string | null): FinancialCategory[] {
  return orderCategories(categories, type).filter((category) => isAvailable(category) || category.id === keepId)
}

export function hasAvailableCategory(categories: FinancialCategory[], type: CategoryType): boolean {
  return categories.some((category) => category.type === type && isAvailable(category))
}
