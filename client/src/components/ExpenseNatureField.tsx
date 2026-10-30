import { EXPENSE_NATURE_HINT, expenseNatureLabels, expenseNatures, type ExpenseNature } from '../api/expenseNature'

/** Campo "Tipo de despesa" dos formulários de despesa. "Não definida" é explícito e é o padrão: nada é assumido como variável. */
export function ExpenseNatureField({
  id,
  value,
  onChange,
  disabled,
  error,
}: {
  id: string
  value: ExpenseNature
  onChange: (value: ExpenseNature) => void
  disabled?: boolean
  error?: string
}) {
  return (
    <>
      <label htmlFor={id}>Tipo de despesa</label>
      <select
        id={id}
        value={value}
        disabled={disabled}
        onChange={(event) => onChange(event.target.value as ExpenseNature)}
        aria-invalid={Boolean(error)}
        aria-describedby={`${id}-hint${error ? ` ${id}-error` : ''}`}
      >
        {expenseNatures.map((nature) => (
          <option key={nature} value={nature}>
            {expenseNatureLabels[nature]}
          </option>
        ))}
      </select>
      <p id={`${id}-hint`} className="meta">
        {EXPENSE_NATURE_HINT}
      </p>
      {error && (
        <p id={`${id}-error`} className="field-error" role="alert">
          {error}
        </p>
      )}
    </>
  )
}

/** Mostra o tipo da despesa em texto (nunca só por cor). Receita não tem o conceito e não mostra nada. */
export function ExpenseNatureBadge({ nature }: { nature?: ExpenseNature }) {
  const value = nature ?? 'Undefined'
  return (
    <span className={`badge badge--nature badge--nature-${value.toLowerCase()}`}>
      Tipo de despesa: {expenseNatureLabels[value]}
    </span>
  )
}
