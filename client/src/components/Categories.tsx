import { useEffect, useState, type FormEvent } from 'react'
import {
  CATEGORY_NAME_MAX_LENGTH,
  createCategory,
  listCategories,
  updateCategory,
  type CategoryType,
  type FinancialCategory,
} from '../api/categories'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

function firstError(errors: Record<string, string[]>): string {
  return Object.values(errors).flat()[0] ?? 'Não foi possível salvar a categoria.'
}

type ListState =
  | { kind: 'loading' }
  | { kind: 'loaded'; categories: FinancialCategory[] }
  | { kind: 'error'; message: string }

const sections: { type: CategoryType; title: string; singular: string }[] = [
  { type: 'Income', title: 'Receitas', singular: 'receita' },
  { type: 'Expense', title: 'Despesas', singular: 'despesa' },
]

/**
 * Categorias da família, separadas em receitas e despesas. Todos os integrantes veem e editam as mesmas.
 * Nada é criado automaticamente: a lista começa vazia.
 */
export function Categories({ familyId }: { familyId: string }) {
  const [list, setList] = useState<ListState>({ kind: 'loading' })
  const [includeInactive, setIncludeInactive] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    listCategories(familyId, includeInactive, controller.signal)
      .then((categories) => setList({ kind: 'loaded', categories }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setList({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, includeInactive, reloadKey])

  const reload = () => setReloadKey((n) => n + 1)

  return (
    <div className="categories">
      <h3>Categorias</h3>

      <label className="checkbox">
        <input type="checkbox" checked={includeInactive} onChange={(event) => setIncludeInactive(event.target.checked)} />
        Mostrar categorias inativas
      </label>

      {list.kind === 'loading' && <p className="status status--pending">Carregando categorias…</p>}
      {list.kind === 'error' && <p className="status status--down">{list.message}</p>}
      {list.kind === 'loaded' && (
        <div className="categories__sections">
          {sections.map((section) => (
            <CategorySection
              key={section.type}
              familyId={familyId}
              {...section}
              categories={list.categories.filter((category) => category.type === section.type)}
              includeInactive={includeInactive}
              onChanged={reload}
            />
          ))}
        </div>
      )}
    </div>
  )
}

function CategorySection({
  familyId,
  type,
  title,
  singular,
  categories,
  includeInactive,
  onChanged,
}: {
  familyId: string
  type: CategoryType
  title: string
  singular: string
  categories: FinancialCategory[]
  includeInactive: boolean
  onChanged: () => void
}) {
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [renamingId, setRenamingId] = useState<string | null>(null)
  const [itemError, setItemError] = useState<string | null>(null)
  const inputId = `category-new-${type}`

  const handleCreate = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const trimmed = name.trim()
    if (trimmed.length === 0) {
      setError('Informe o nome da categoria.')
      return
    }

    setBusy(true)
    setError(null)
    try {
      const result = await createCategory(familyId, trimmed, type)
      if (result.ok) {
        setName('')
        onChanged()
      } else setError(firstError(result.errors))
    } catch (failure: unknown) {
      setError(errorMessage(failure))
    } finally {
      setBusy(false)
    }
  }

  const handleToggle = async (category: FinancialCategory) => {
    const question = category.isActive
      ? `Desativar a categoria "${category.name}"? Ela deixa de aparecer na lista, mas o cadastro é preservado.`
      : `Reativar a categoria "${category.name}"?`
    if (!window.confirm(question)) return

    setItemError(null)
    try {
      const result = await updateCategory(familyId, category.id, category.name, !category.isActive)
      if (!result.ok) setItemError(firstError(result.errors))
      onChanged()
    } catch (failure: unknown) {
      setItemError(errorMessage(failure))
    }
  }

  return (
    <section className="category-section" aria-labelledby={`category-section-${type}`}>
      <h4 id={`category-section-${type}`}>{title}</h4>

      <form className="category-add" onSubmit={handleCreate} noValidate>
        <label htmlFor={inputId} className="visually-hidden">
          Nova categoria de {singular}
        </label>
        <input
          id={inputId}
          value={name}
          onChange={(event) => setName(event.target.value)}
          maxLength={CATEGORY_NAME_MAX_LENGTH}
          placeholder={`Nova categoria de ${singular}`}
          autoComplete="off"
          aria-invalid={Boolean(error)}
          aria-describedby={error ? `${inputId}-error` : undefined}
        />
        <button type="submit" className="button--primary" disabled={busy}>
          {busy ? 'Adicionando…' : 'Adicionar'}
        </button>
      </form>
      {error && (
        <p id={`${inputId}-error`} className="field-error" role="alert">
          {error}
        </p>
      )}
      {itemError && (
        <p className="field-error" role="alert">
          {itemError}
        </p>
      )}

      {categories.length === 0 ? (
        <p className="meta">Nenhuma categoria de {singular} {includeInactive ? '' : 'ativa '}cadastrada.</p>
      ) : (
        <ul className="category-list">
          {categories.map((category) =>
            renamingId === category.id ? (
              <li key={category.id} className="category">
                <RenameForm
                  familyId={familyId}
                  category={category}
                  onDone={() => {
                    setRenamingId(null)
                    onChanged()
                  }}
                  onCancel={() => setRenamingId(null)}
                />
              </li>
            ) : (
              <li key={category.id} className={category.isActive ? 'category' : 'category category--inactive'}>
                <span className="category__name">
                  {category.name}
                  {!category.isActive && <span className="meta"> (inativa)</span>}
                </span>
                <span className="category__actions">
                  <button type="button" onClick={() => setRenamingId(category.id)}>
                    Renomear
                  </button>
                  <button type="button" onClick={() => void handleToggle(category)}>
                    {category.isActive ? 'Desativar' : 'Reativar'}
                  </button>
                </span>
              </li>
            ),
          )}
        </ul>
      )}
    </section>
  )
}

function RenameForm({
  familyId,
  category,
  onDone,
  onCancel,
}: {
  familyId: string
  category: FinancialCategory
  onDone: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState(category.name)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const inputId = `category-rename-${category.id}`

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const trimmed = name.trim()
    if (trimmed.length === 0) {
      setError('Informe o nome da categoria.')
      return
    }

    setBusy(true)
    setError(null)
    try {
      const result = await updateCategory(familyId, category.id, trimmed, category.isActive)
      if (result.ok) onDone()
      else setError(firstError(result.errors))
    } catch (failure: unknown) {
      setError(errorMessage(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="category-rename" onSubmit={handleSubmit} noValidate>
      <label htmlFor={inputId} className="visually-hidden">
        Novo nome de “{category.name}”
      </label>
      <input
        id={inputId}
        value={name}
        onChange={(event) => setName(event.target.value)}
        maxLength={CATEGORY_NAME_MAX_LENGTH}
        autoComplete="off"
        autoFocus
        aria-invalid={Boolean(error)}
        aria-describedby={error ? `${inputId}-error` : undefined}
      />
      <span className="category__actions">
        <button type="submit" className="button--primary" disabled={busy}>
          {busy ? 'Salvando…' : 'Salvar'}
        </button>
        <button type="button" onClick={onCancel} disabled={busy}>
          Cancelar
        </button>
      </span>
      {error && (
        <p id={`${inputId}-error`} className="field-error" role="alert">
          {error}
        </p>
      )}
    </form>
  )
}
