import { describe, expect, it } from 'vitest'
import type { FinancialCategory } from './api/categories'
import { categoryPath, groupCategories, hasAvailableCategory, orderCategories, selectableCategories } from './categoryTree'

const category = (id: string, name: string, extra: Partial<FinancialCategory> = {}): FinancialCategory => ({
  id,
  name,
  type: 'Expense',
  isActive: true,
  createdAt: '2026-10-01T00:00:00Z',
  ...extra,
})

const moradia = category('moradia', 'Moradia')
const energia = category('energia', 'Energia', { parentCategoryId: 'moradia', parentName: 'Moradia', isAvailable: true, path: 'Moradia / Energia' })
const agua = category('agua', 'Água', { parentCategoryId: 'moradia', parentName: 'Moradia', isAvailable: true })
const lazer = category('lazer', 'Lazer')
const salario = category('salario', 'Salário', { type: 'Income' })

describe('categoryTree', () => {
  it('ordena principais em ordem alfabética, cada uma seguida das suas subcategorias', () => {
    const ordered = orderCategories([lazer, energia, moradia, agua, salario], 'Expense')

    expect(ordered.map((c) => c.id)).toEqual(['lazer', 'moradia', 'agua', 'energia'])
  })

  it('mostra "Categoria / Subcategoria" mesmo quando a API não manda o path', () => {
    expect(categoryPath(energia)).toBe('Moradia / Energia')
    expect(categoryPath(agua)).toBe('Moradia / Água')
    expect(categoryPath(moradia)).toBe('Moradia')
  })

  it('subcategoria cuja principal inativa ficou fora da lista forma um grupo identificado', () => {
    const [group] = groupCategories([energia], 'Expense')

    expect(group.main).toBeNull()
    expect(group.parentName).toBe('Moradia')
    expect(group.subs.map((c) => c.id)).toEqual(['energia'])
  })

  it('só oferece categorias disponíveis, mais a que o registro já usa mesmo indisponível', () => {
    const indisponivel = { ...energia, isAvailable: false }
    const inativa = category('velha', 'Velha', { isActive: false })

    const options = selectableCategories([moradia, indisponivel, inativa, lazer], 'Expense')
    const kept = selectableCategories([moradia, indisponivel, inativa, lazer], 'Expense', 'energia')

    expect(options.map((c) => c.id)).toEqual(['lazer', 'moradia'])
    expect(kept.map((c) => c.id)).toEqual(['lazer', 'moradia', 'energia'])
    expect(hasAvailableCategory([inativa, indisponivel], 'Expense')).toBe(false)
  })

  it('separa receitas de despesas', () => {
    expect(selectableCategories([moradia, salario], 'Income').map((c) => c.id)).toEqual(['salario'])
  })
})
