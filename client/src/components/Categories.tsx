import { useEffect, useState, type FormEvent } from 'react'
import {
  CATEGORY_NAME_MAX_LENGTH,
  createCategory,
  listCategories,
  updateCategory,
  type CategoryType,
  type FinancialCategory,
} from '../api/categories'
import { groupCategories, isAvailable, type CategoryGroup } from '../categoryTree'

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
 * Categorias da família, separadas em receitas e despesas, com até dois níveis: a categoria principal e suas subcategorias (exibidas como
 * "Categoria / Subcategoria" nos formulários). Todos os integrantes veem e editam as mesmas. Nada é criado automaticamente: a lista começa vazia.
 * Uma categoria por vez: não há criação em lote, e desativar não apaga nem move nada.
 */
export function Categories({ workspaceId }: { workspaceId: string }) {
  const [list, setList] = useState<ListState>({ kind: 'loading' })
  const [includeInactive, setIncludeInactive] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    listCategories(workspaceId, includeInactive, controller.signal)
      .then((categories) => setList({ kind: 'loaded', categories }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setList({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, includeInactive, reloadKey])

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
              workspaceId={workspaceId}
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
  workspaceId,
  type,
  title,
  singular,
  categories,
  includeInactive,
  onChanged,
}: {
  workspaceId: string
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
    const invalid = nameProblem(trimmed)
    if (invalid) {
      setError(invalid)
      return
    }

    setBusy(true)
    setError(null)
    try {
      const result = await createCategory(workspaceId, trimmed, type)
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

  const handleToggle = async (category: FinancialCategory, hasSubcategories = false) => {
    const question = category.isActive
      ? `Desativar a categoria "${category.name}"? Ela deixa de aparecer na lista, mas o cadastro é preservado.` +
        (hasSubcategories ? ' As subcategorias ficam indisponíveis para novos registros; os registros existentes não mudam.' : '')
      : `Reativar a categoria "${category.name}"?`
    if (!window.confirm(question)) return

    setItemError(null)
    try {
      const result = await updateCategory(workspaceId, category.id, category.name, !category.isActive)
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
          {groupCategories(categories).map((group) => (
            <CategoryGroupItem
              key={group.parentId}
              workspaceId={workspaceId}
              group={group}
              type={type}
              renamingId={renamingId}
              setRenamingId={setRenamingId}
              onToggle={handleToggle}
              onChanged={onChanged}
            />
          ))}
        </ul>
      )}
    </section>
  )
}

/** Nome de categoria: um por vez, sem listas separadas por vírgula ou ponto e vírgula. */
function nameProblem(name: string): string | null {
  if (name.length === 0) return 'Informe o nome da categoria.'
  if (/[,;]/.test(name)) return 'Cadastre uma categoria por vez: não use vírgula nem ponto e vírgula.'
  return null
}

function CategoryGroupItem({
  workspaceId,
  group,
  type,
  renamingId,
  setRenamingId,
  onToggle,
  onChanged,
}: {
  workspaceId: string
  group: CategoryGroup
  type: CategoryType
  renamingId: string | null
  setRenamingId: (id: string | null) => void
  onToggle: (category: FinancialCategory, hasSubcategories?: boolean) => Promise<void>
  onChanged: () => void
}) {
  const { main, subs } = group
  const [adding, setAdding] = useState(false)

  const row = (category: FinancialCategory, isSub: boolean) =>
    renamingId === category.id ? (
      <RenameForm
        workspaceId={workspaceId}
        category={category}
        onDone={() => {
          setRenamingId(null)
          onChanged()
        }}
        onCancel={() => setRenamingId(null)}
      />
    ) : (
      <>
        <span className="category__name">
          {category.name}
          {!category.isActive && <span className="meta"> (inativa)</span>}
          {category.isActive && isSub && !isAvailable(category) && (
            <span className="meta"> (indisponível: categoria principal inativa)</span>
          )}
        </span>
        <span className="category__actions">
          <button type="button" onClick={() => setRenamingId(category.id)}>
            Renomear
          </button>
          <button type="button" onClick={() => void onToggle(category, !isSub && subs.length > 0)}>
            {category.isActive ? 'Desativar' : 'Reativar'}
          </button>
          {!isSub && category.isActive && (
            <button type="button" aria-expanded={adding} onClick={() => setAdding((open) => !open)}>
              Nova subcategoria
            </button>
          )}
        </span>
      </>
    )

  return (
    <li className="category-group">
      {main ? (
        <div className={main.isActive ? 'category' : 'category category--inactive'}>{row(main, false)}</div>
      ) : (
        <div className="category category--inactive">
          <span className="category__name">
            {group.parentName}
            <span className="meta"> (principal inativa)</span>
          </span>
        </div>
      )}
      {main && adding && (
        <SubcategoryForm
          workspaceId={workspaceId}
          parent={main}
          type={type}
          onDone={() => {
            setAdding(false)
            onChanged()
          }}
          onCancel={() => setAdding(false)}
        />
      )}
      {subs.length > 0 && (
        <ul className="subcategory-list" aria-label={`Subcategorias de ${group.parentName}`}>
          {subs.map((sub) => (
            <li key={sub.id} className={sub.isActive ? 'category category--sub' : 'category category--sub category--inactive'}>
              {row(sub, true)}
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

/** Cria uma subcategoria sob uma principal identificada na tela; o tipo é o da principal. */
function SubcategoryForm({
  workspaceId,
  parent,
  type,
  onDone,
  onCancel,
}: {
  workspaceId: string
  parent: FinancialCategory
  type: CategoryType
  onDone: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const inputId = `subcategory-new-${parent.id}`

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const trimmed = name.trim()
    const invalid = nameProblem(trimmed)
    if (invalid) {
      setError(invalid)
      return
    }
    setBusy(true)
    setError(null)
    try {
      const result = await createCategory(workspaceId, trimmed, type, parent.id)
      if (result.ok) onDone()
      else setError(firstError(result.errors))
    } catch (failure: unknown) {
      setError(errorMessage(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="category-add category-add--sub" onSubmit={handleSubmit} noValidate>
      <p className="meta category-add__parent">
        Subcategoria de <strong>{parent.name}</strong>
      </p>
      <label htmlFor={inputId} className="visually-hidden">
        Nova subcategoria de {parent.name}
      </label>
      <input
        id={inputId}
        value={name}
        onChange={(event) => setName(event.target.value)}
        maxLength={CATEGORY_NAME_MAX_LENGTH}
        placeholder={`Nova subcategoria de ${parent.name}`}
        autoComplete="off"
        autoFocus
        aria-invalid={Boolean(error)}
        aria-describedby={error ? `${inputId}-error` : undefined}
      />
      <span className="category__actions">
        <button type="submit" className="button--primary" disabled={busy}>
          {busy ? 'Adicionando…' : 'Adicionar subcategoria'}
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

function RenameForm({
  workspaceId,
  category,
  onDone,
  onCancel,
}: {
  workspaceId: string
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
    const invalid = nameProblem(trimmed)
    if (invalid) {
      setError(invalid)
      return
    }

    setBusy(true)
    setError(null)
    try {
      const result = await updateCategory(workspaceId, category.id, trimmed, category.isActive)
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
