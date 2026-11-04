import { useState, type FormEvent } from 'react'
import { confirmCompetenceAmount } from '../api/recurrences'
import { decimalToInput, formatBrlDecimal, parseAmount } from '../money'

/** Confirma o valor real de UMA competência de despesa fixa. Não altera a recorrência-base, outras competências nem paga a obrigação. */
export function CompetenceAmountForm({ workspaceId, recurrenceId, competence, currentAmount, baseAmount, onSaved, onCancel }: {
  workspaceId: string
  recurrenceId: string
  /** yyyy-MM */
  competence: string
  currentAmount: string
  baseAmount: string | null | undefined
  onSaved: () => void
  onCancel: () => void
}) {
  const [value, setValue] = useState(decimalToInput(currentAmount))
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const parsed = parseAmount(value, 'O valor real')
    if (parsed.kind === 'empty') return setError('Informe o valor real.')
    if (parsed.kind === 'invalid') return setError(parsed.message)
    setBusy(true); setError(null)
    try { await confirmCompetenceAmount(workspaceId, recurrenceId, competence, parsed.text); onSaved() }
    catch (e: unknown) { setError(e instanceof Error ? e.message : 'Não foi possível confirmar o valor.'); setBusy(false) }
  }

  return (
    <form className="form" onSubmit={(event) => void submit(event)} aria-label="Confirmar valor real da competência">
      <p className="notice">
        O valor vale somente para {competence.slice(5)}/{competence.slice(0, 4)}.
        {baseAmount ? ` O valor-base da recorrência (${formatBrlDecimal(baseAmount)}) e as outras competências não mudam.` : ''} Confirmar não é pagar.
      </p>
      <label>Valor real confirmado
        <input inputMode="decimal" value={value} onChange={(event) => setValue(event.target.value)} disabled={busy} />
      </label>
      {error && <p className="field-error" role="alert">{error}</p>}
      <div className="form__actions">
        <button type="submit" className="button--primary" disabled={busy}>{busy ? 'Salvando…' : 'Confirmar valor'}</button>
        <button type="button" onClick={onCancel} disabled={busy}>Voltar</button>
      </div>
    </form>
  )
}
