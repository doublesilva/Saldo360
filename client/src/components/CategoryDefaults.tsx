import { useState } from 'react'
import {
  applyDefaultCategories,
  previewDefaultCategories,
  type CategoryType,
  type DefaultCategoriesPreview,
  type DefaultCategoryAction,
  type DefaultCategoryPlan,
} from '../api/categories'

const actionText: Record<DefaultCategoryAction, string> = {
  Create: 'será adicionada',
  Exists: 'já existe',
  ExistsInactive: 'já existe, inativa (não será reativada)',
  SkippedParentInactive: 'Não adicionada: categoria principal inativa (reative-a para adicionar)',
}

const sections: { type: CategoryType; title: string }[] = [
  { type: 'Expense', title: 'Despesas' },
  { type: 'Income', title: 'Receitas' },
]

type State =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'preview'; preview: DefaultCategoriesPreview }
  | { kind: 'applying'; preview: DefaultCategoriesPreview }
  | { kind: 'error'; message: string }

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

/**
 * Ação explícita "Adicionar categorias padrão" para orçamentos que já existiam. Mostra antes exatamente o que será adicionado e o que já existe
 * (equivalência por tipo e nome, ignorando espaços e maiúsculas); nada existente é renomeado, reativado ou reclassificado, e repetir é seguro.
 */
export function CategoryDefaults({ workspaceId, onApplied }: { workspaceId: string; onApplied: () => void }) {
  const [state, setState] = useState<State>({ kind: 'idle' })
  const [result, setResult] = useState<string | null>(null)

  const open = async () => {
    setResult(null)
    setState({ kind: 'loading' })
    try {
      setState({ kind: 'preview', preview: await previewDefaultCategories(workspaceId) })
    } catch (failure: unknown) {
      setState({ kind: 'error', message: errorMessage(failure) })
    }
  }

  const apply = async (preview: DefaultCategoriesPreview) => {
    setState({ kind: 'applying', preview })
    try {
      const applied = await applyDefaultCategories(workspaceId)
      setResult(
        `Adicionadas ${plural(applied.categoriesCreated, 'categoria', 'categorias')} e ${plural(applied.subcategoriesCreated, 'subcategoria', 'subcategorias')}. ` +
          'O que já existia não foi alterado.',
      )
      setState({ kind: 'idle' })
      onApplied()
    } catch (failure: unknown) {
      setState({ kind: 'error', message: errorMessage(failure) })
    }
  }

  const expanded = state.kind !== 'idle'

  return (
    <div className="category-defaults">
      <button type="button" aria-expanded={expanded} onClick={() => (expanded ? setState({ kind: 'idle' }) : void open())}>
        Adicionar categorias padrão
      </button>
      {result && (
        <p className="status status--ok" role="status">
          {result}
        </p>
      )}
      {state.kind === 'loading' && <p className="status status--pending">Carregando categorias padrão…</p>}
      {state.kind === 'error' && (
        <p className="field-error" role="alert">
          {state.message}
        </p>
      )}
      {(state.kind === 'preview' || state.kind === 'applying') && (
        <DefaultsPreview preview={state.preview} busy={state.kind === 'applying'} onApply={() => void apply(state.preview)} onClose={() => setState({ kind: 'idle' })} />
      )}
    </div>
  )
}

function DefaultsPreview({
  preview,
  busy,
  onApply,
  onClose,
}: {
  preview: DefaultCategoriesPreview
  busy: boolean
  onApply: () => void
  onClose: () => void
}) {
  const nothingToAdd = preview.categoriesToCreate + preview.subcategoriesToCreate === 0
  return (
    <div className="category-defaults__preview" role="region" aria-label="Categorias padrão a adicionar">
      <p>
        {nothingToAdd
          ? 'Todas as categorias padrão já existem neste orçamento. Nada será adicionado.'
          : `Serão adicionadas ${plural(preview.categoriesToCreate, 'categoria', 'categorias')} e ${plural(preview.subcategoriesToCreate, 'subcategoria', 'subcategorias')}. ` +
            'Categorias com o mesmo tipo e nome (ignorando espaços e maiúsculas) são reaproveitadas; nada do que existe é renomeado, reativado ou reclassificado.'}
      </p>
      {sections.map((section) => (
        <section key={section.type} aria-label={`Padrão de ${section.title.toLowerCase()}`}>
          <h5>{section.title}</h5>
          <ul className="category-defaults__list">
            {preview.categories
              .filter((category) => category.type === section.type)
              .map((category) => (
                <DefaultCategoryItem key={`${category.type}-${category.name}`} category={category} />
              ))}
          </ul>
        </section>
      ))}
      <div className="category__actions">
        <button type="button" className="button--primary" onClick={onApply} disabled={busy || nothingToAdd}>
          {busy ? 'Adicionando…' : 'Adicionar ao orçamento'}
        </button>
        <button type="button" onClick={onClose} disabled={busy}>
          Fechar
        </button>
      </div>
    </div>
  )
}

function DefaultCategoryItem({ category }: { category: DefaultCategoryPlan }) {
  return (
    <li>
      <strong>{category.name}</strong> <span className="meta">({actionText[category.action]})</span>
      <ul>
        {category.subcategories.map((sub) => (
          <li key={sub.name}>
            {sub.name} <span className="meta">({actionText[sub.action]})</span>
          </li>
        ))}
      </ul>
    </li>
  )
}
