import { useState, type FormEvent } from 'react'
import { archiveBudgetTarget, updateBudgetTarget, type FinancialBudgetTarget, type FinancialBudgetTargetExecution } from '../../api/budgets'
import { decimalToInput, formatBrlDecimal, parseAmount } from '../../money'
import { barWidth, percentNow, percentText } from '../overview/overviewText'
import { BudgetComposition } from './BudgetComposition'
import { groupHints, statusIcons, statusLabels } from './budgetText'

function firstError(errors: Record<string, string[]>): string {
  return Object.values(errors).flat()[0] ?? 'Verifique os dados informados.'
}

/**
 * Uma meta: limite, consumido e quanto está disponível (ou excedido). Previsto adicional e projeção ficam numa linha complementar. O estado vem em texto
 * (nunca só por cor) e dados incompletos são ditos, em vez de virarem "zero".
 */
export function BudgetTargetCard({ workspaceId, budgetId, target, run, title, canEdit, open, version, onToggle, onChanged, onNotice, onReviewClassification }: {
  workspaceId: string; budgetId: string; target: FinancialBudgetTarget; run: FinancialBudgetTargetExecution; title: string; canEdit: boolean
  open: boolean; version: number; onToggle: () => void; onChanged: () => void; onNotice: (notice: string | null) => void; onReviewClassification?: () => void
}) {
  const [editing, setEditing] = useState(false)
  const [busy, setBusy] = useState(false)
  const exceeded = run.status === 'Exceeded'
  const noMovement = run.consumed === '0.00' && run.committed === '0.00'
  const zeroLimit = run.planned === '0.00'
  const projectedOver = !exceeded && run.projectedStatus === 'Exceeded'

  return <article className={`budget-target${exceeded ? ' budget-target--exceeded' : ''}`} aria-label={`Meta ${title}`}>
    <div className="budget-target__header">
      <div><h5>{title}{target.category && !target.category.isActive && <span className="meta"> (inativa)</span>}</h5><span className="meta">Limite {formatBrlDecimal(run.planned)}</span></div>
      {run.coverage.noCards
        ? <span className="budget-badge">Sem cartões cadastrados</span>
        : <span className={`budget-badge budget-badge--${run.status}`}><span aria-hidden="true">{statusIcons[run.status]}</span> {statusLabels[run.status]}</span>}
    </div>
    <div className="budget-target__values">
      <span>Consumido <strong>{formatBrlDecimal(run.consumed)}</strong></span>
      <span>{exceeded ? 'Excedido em' : 'Disponível'} <strong>{formatBrlDecimal(exceeded ? run.exceededAmount : run.available)}</strong></span>
    </div>
    <div className="budget-progress" role="progressbar" aria-label={`Consumo de ${title}`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percentNow(run.consumptionPercentage)}
      aria-valuetext={run.consumptionPercentage === null ? 'sem referência percentual' : `${percentText(run.consumptionPercentage)} consumido`}>
      <div className="budget-progress__bar" style={{ width: barWidth(run.consumptionPercentage) }} />
      <span>{run.consumptionPercentage === null ? 'Sem referência' : `${percentText(run.consumptionPercentage)} consumido`}</span>
    </div>
    {zeroLimit && <p className="meta">Limite zero: qualquer consumo excede o limite.</p>}
    {run.coverage.noCards
      ? <p className="notice" role="status">Nenhum cartão cadastrado: não há consumo de cartões para medir (isto não é um zero confirmado).</p>
      : noMovement && <p className="meta">Sem movimento nesta competência.</p>}
    {(run.committed !== '0.00' || projectedOver) && <p className="budget-target__extra meta">
      Previsto adicional {formatBrlDecimal(run.committed)} · projetado {formatBrlDecimal(run.projectedConsumption)}
      {run.projectedStatus === 'Exceeded' ? ` · excederia em ${formatBrlDecimal(run.exceededProjected)}` : ` · disponível projetado ${formatBrlDecimal(run.availableProjected)}`}
    </p>}
    {run.coverage.estimatedCompetenceCount > 0 && <p className="notice" role="status">Dados incompletos: {run.coverage.estimatedCompetenceCount} compra(s) de cartão com competência estimada (sem a fatura do banco). Abra a composição para identificá-las.</p>}
    {run.coverage.undefinedNatureCount > 0 && <p className="notice" role="status">
      {run.coverage.undefinedNatureCount} despesa(s) do mês sem tipo definido ({formatBrlDecimal(run.coverage.undefinedNatureAmount)}) não entram nas fixas.{' '}
      {onReviewClassification && <button type="button" className="button--link" onClick={onReviewClassification}>Revisar classificação</button>}
    </p>}
    <p className="meta budget-target__hint">{groupHints[target.groupingType]}</p>
    {editing && canEdit
      ? <TargetEditForm workspaceId={workspaceId} budgetId={budgetId} target={target} title={title} onDone={() => { setEditing(false); onChanged() }} onCancel={() => setEditing(false)} onError={onNotice} />
      : <div className="budget-target__actions">
        <button type="button" aria-expanded={open} onClick={onToggle}>{open ? 'Fechar composição' : 'Abrir composição'}</button>
        {canEdit && <button type="button" onClick={() => setEditing(true)}>Editar</button>}
        {canEdit && <button type="button" disabled={busy} onClick={() => {
          if (!window.confirm(`Arquivar a meta ${title} (${formatBrlDecimal(target.plannedAmount)})? Isso retira a meta do orçamento, sem cancelar despesas.`)) return
          setBusy(true)
          void archiveBudgetTarget(workspaceId, budgetId, target.id).then((result) => result.ok ? onChanged() : onNotice(result.message)).finally(() => setBusy(false))
        }}>Arquivar meta</button>}
      </div>}
    {open && <BudgetComposition workspaceId={workspaceId} budgetId={budgetId} targetId={target.id} version={version} onReviewClassification={onReviewClassification} />}
  </article>
}

function TargetEditForm({ workspaceId, budgetId, target, title, onDone, onCancel, onError }: {
  workspaceId: string; budgetId: string; target: FinancialBudgetTarget; title: string; onDone: () => void; onCancel: () => void; onError: (message: string) => void
}) {
  const [amount, setAmount] = useState(() => decimalToInput(target.plannedAmount))
  const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const parsed = parseAmount(amount, 'O limite')
    if (parsed.kind !== 'ok') return onError(parsed.kind === 'invalid' ? parsed.message : 'Informe o valor do limite.')
    setBusy(true)
    try {
      const result = await updateBudgetTarget(workspaceId, budgetId, target.id, target.name, parsed.text)
      if (result.ok) onDone(); else onError(firstError(result.errors))
    } catch (failure: unknown) { onError(failure instanceof Error ? failure.message : 'Erro desconhecido.') } finally { setBusy(false) }
  }
  return <form className="budget-target-edit" onSubmit={submit} noValidate>
    <label htmlFor={`target-${target.id}`}>Valor do limite de {title}</label>
    <input id={`target-${target.id}`} inputMode="decimal" value={amount} onChange={(event) => setAmount(event.target.value)} />
    <button type="submit" className="button--primary" disabled={busy}>Salvar</button>
    <button type="button" onClick={onCancel} disabled={busy}>Cancelar</button>
  </form>
}
