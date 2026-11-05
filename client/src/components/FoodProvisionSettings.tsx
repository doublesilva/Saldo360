import { useEffect, useState } from 'react'
import { toUserMessage } from '../api/apiErrors'
import { listCategories, type FinancialCategory } from '../api/categories'
import { getFoodProvision, removeFoodProvisionMonth, saveFoodProvision, saveFoodProvisionMonth, type FoodProvision } from '../api/projection'
import { formatMonthYear } from '../dates'
import { decimalToInput, parseAmount } from '../money'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import { money } from './monthlyPlanText'

type Load = { state: 'loading' } | { state: 'error'; error: unknown } | { state: 'loaded'; provision: FoodProvision; categories: FinancialCategory[] }

/**
 * Provisão de alimentação no cartão (Entrega 2): valor mensal padrão, categorias alimentares por identificador e ajuste por competência. É configuração do
 * orçamento: não cria lançamento nem altera saldo. Nada é gravado ao carregar; o valor de referência da API só vira configuração ao salvar.
 */
export function FoodProvisionSettings({ workspaceId, competence, onSaved }: { workspaceId: string; competence: string; onSaved: () => void }) {
  const [load, setLoad] = useState<Load>({ state: 'loading' })
  const [reload, setReload] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    Promise.all([getFoodProvision(workspaceId, controller.signal), listCategories(workspaceId, false, controller.signal)])
      .then(([provision, categories]) => { if (!controller.signal.aborted) setLoad({ state: 'loaded', provision, categories: categories.filter(category => category.type === 'Expense') }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoad({ state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, reload])

  return <details className="proj-food" open>
    <summary>Provisão de alimentação no cartão</summary>
    {load.state === 'loading' && <OperationalLoading>Carregando a provisão de alimentação…</OperationalLoading>}
    {load.state === 'error' && <OperationalError error={load.error} onRetry={() => setReload(value => value + 1)} />}
    {load.state === 'loaded' && <FoodForm key={JSON.stringify(load.provision)} workspaceId={workspaceId} competence={competence} provision={load.provision} categories={load.categories}
      onSaved={provision => { setLoad({ ...load, provision }); onSaved() }} />}
  </details>
}

function FoodForm({ workspaceId, competence, provision, categories, onSaved }: {
  workspaceId: string; competence: string; provision: FoodProvision; categories: FinancialCategory[]; onSaved: (provision: FoodProvision) => void
}) {
  const [amount, setAmount] = useState(decimalToInput(provision.monthlyAmount))
  const [selected, setSelected] = useState<Set<string>>(() => new Set(provision.categoryIds))
  const adjusted = provision.monthAmounts[competence]
  const [monthAmount, setMonthAmount] = useState(decimalToInput(adjusted ?? provision.monthlyAmount))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const title = formatMonthYear(`${competence}-01`)

  const run = async (action: () => Promise<FoodProvision>) => {
    setBusy(true); setError(null)
    try { onSaved(await action()) } catch (failure) { setError(toUserMessage(failure)) } finally { setBusy(false) }
  }
  const parsed = (value: string, subject: string) => {
    const result = parseAmount(value, subject)
    if (result.kind === 'ok' && !result.text.startsWith('-')) return result.text
    setError(result.kind === 'invalid' ? result.message : `${subject}: informe zero ou um valor positivo.`)
    return null
  }
  const saveDefault = () => { const text = parsed(amount, 'O valor mensal'); if (text) void run(() => saveFoodProvision(workspaceId, text, [...selected])) }
  const saveMonth = () => { const text = parsed(monthAmount, 'O valor da competência'); if (text) void run(() => saveFoodProvisionMonth(workspaceId, competence, text)) }
  const toggle = (id: string) => setSelected(current => { const next = new Set(current); if (!next.delete(id)) next.add(id); return next })

  return <div className="proj-food__body">
    <p className="meta">Estimativa de planejamento dentro do teto dos cartões: só a diferença ainda não reconhecida na fatura da competência. Não cria lançamentos.</p>
    {!provision.amountConfigured && <p className="meta" role="note">Valor de referência {money(provision.monthlyAmount)}, ainda não gravado: ele só vira configuração quando você salvar.</p>}
    {provision.categoriesInferred && <p className="meta" role="note">Categorias sugeridas pelo nome; elas só passam a valer por identificador quando você salvar.</p>}
    {provision.categoryIds.length === 0 && !provision.categoriesInferred && <p className="meta" role="note">Nenhuma categoria alimentar configurada: a provisão não é aplicada.</p>}
    <label>Valor mensal padrão (R$)<input inputMode="decimal" aria-label="Valor mensal padrão da provisão" value={amount} onChange={event => setAmount(event.target.value)} /></label>
    <fieldset><legend>Categorias alimentares</legend>
      {categories.length === 0 ? <p className="meta">Nenhuma categoria de despesa cadastrada.</p>
        : <div className="proj-food__categories">{categories.map(category => <label key={category.id}><input type="checkbox" checked={selected.has(category.id)} onChange={() => toggle(category.id)} />{category.path ?? category.name}</label>)}</div>}
    </fieldset>
    <div className="monthly-plan__actions"><button type="button" className="button--primary" disabled={busy || selected.size === 0} onClick={saveDefault}>Salvar provisão</button></div>
    {selected.size === 0 && <p className="meta">Selecione ao menos uma categoria para salvar.</p>}
    <hr />
    <label>Ajuste de {title} (R$)<input inputMode="decimal" aria-label={`Valor da provisão em ${title}`} value={monthAmount} onChange={event => setMonthAmount(event.target.value)} /></label>
    <p className="meta">{adjusted !== undefined ? `Há um ajuste para ${title}: ${money(adjusted)}.` : `${title} usa o valor padrão.`}</p>
    <div className="monthly-plan__actions">
      <button type="button" disabled={busy} onClick={saveMonth}>Ajustar competência</button>
      <button type="button" disabled={busy || adjusted === undefined} onClick={() => void run(() => removeFoodProvisionMonth(workspaceId, competence))}>Voltar ao valor padrão</button>
    </div>
    {error && <p className="field-error" role="alert">{error}</p>}
  </div>
}
