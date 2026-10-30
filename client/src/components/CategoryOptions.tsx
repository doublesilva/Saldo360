import type { CategoryType, FinancialCategory } from '../api/categories'
import { categoryPath, isAvailable, isSubcategory, orderCategories, selectableCategories } from '../categoryTree'

/** Opções "Categoria" e "Categoria / Subcategoria" para um <select> de formulário (só as disponíveis, mais a que o registro já usa). */
export function CategoryOptions({
  categories,
  type,
  keepId,
}: {
  categories: FinancialCategory[]
  type: CategoryType
  keepId?: string | null
}) {
  return (
    <>
      {selectableCategories(categories, type, keepId).map((category) => (
        <option key={category.id} value={category.id}>
          {categoryPath(category)}
          {isAvailable(category) ? '' : ' — inativa'}
        </option>
      ))}
    </>
  )
}

/**
 * Opções de um filtro de categoria. Escolher uma principal inclui as suas subcategorias (dito no rótulo); escolher uma subcategoria traz só os
 * registros dela.
 */
export function CategoryFilterOptions({ categories, type }: { categories: FinancialCategory[]; type?: CategoryType | '' }) {
  const ordered = orderCategories(categories, type || undefined)
  const withSubs = new Set(ordered.filter(isSubcategory).map((category) => category.parentCategoryId))
  return (
    <>
      {ordered.map((category) => (
        <option key={category.id} value={category.id}>
          {categoryPath(category)}
          {withSubs.has(category.id) ? ' (com subcategorias)' : ''}
          {category.isActive ? '' : ' (inativa)'}
        </option>
      ))}
    </>
  )
}
