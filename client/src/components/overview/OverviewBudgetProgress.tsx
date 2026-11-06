import type { BudgetControl, BudgetGroupStatus } from '../../api/overview'
import { useState } from 'react'
import { OverviewBudgetCardDetails } from './OverviewBudgetCardDetails'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { barWidth, money, percentNow, percentText } from './overviewText'

// Dashboard Folio — Orçamento mensal. Três barras (meta total, cartões e fora dos cartões) com o percentual PROJETADO, o total previsto sobre a meta e a diferença. Total previsto = efetivado +
// comprometido + estimado, e cartões e fora dos cartões são exclusivos: tudo isso, os percentuais, a margem e o excesso vêm prontos do servidor (resolvedor único de metas da T12.1).
// A meta de despesas fixas é uma perspectiva complementar dos mesmos gastos: aparece numa linha, nunca somada nem com barra própria.

const icons = {
  target: <svg className="ov-icon" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="6.5" /><circle cx="8" cy="8" r="3.75" /><circle cx="8" cy="8" r="1" /></svg>,
  card: <svg className="ov-icon" viewBox="0 0 16 16" aria-hidden="true"><rect x="1.75" y="3.5" width="12.5" height="9" rx="1.5" /><path d="M1.75 6.5h12.5M4.5 10h2.5" /></svg>,
  wallet: <svg className="ov-icon" viewBox="0 0 16 16" aria-hidden="true"><path d="M13 5.5V4a1.5 1.5 0 0 0-1.5-1.5h-8A1.5 1.5 0 0 0 2 4v8a1.5 1.5 0 0 0 1.5 1.5h8A1.5 1.5 0 0 0 13 12v-1.5" /><path d="M14.25 5.5H10.5a2.5 2.5 0 0 0 0 5h3.75z" /></svg>,
}

interface Row { key: string; title: string; icon: keyof typeof icons; status: BudgetGroupStatus; target: string | null; projected: string; projectedPercent: string | null; margin: string | null; excess: string | null }

export function OverviewBudgetProgress({ control, fixedTarget, scopeLabel, monthLabel, workspaceId, competence, loading, error, retry, onOpenBudget, onOpenCards }: {
  control: BudgetControl | null; fixedTarget: string | null; scopeLabel: string; monthLabel: string; workspaceId: string; competence: string; loading: boolean; error: unknown; retry: () => void; onOpenBudget: () => void; onOpenCards: () => void
}) {
  return <section className="ov-card ov-budget" aria-labelledby="ov-budget-title">
    <header className="ov-card__head"><div><h3 id="ov-budget-title">Orçamento mensal</h3><p className="ov-meta">{scopeLabel ? `${scopeLabel} · ` : ''}{monthLabel}</p></div>
      <button type="button" className="ov-link" onClick={onOpenBudget}>Ver detalhes ›</button></header>
    {error ? <OperationalError error={error} onRetry={retry} retryLabel="Recarregar" />
      : !control ? loading ? <OperationalLoading>Carregando o orçamento…</OperationalLoading> : <p className="ov-empty">O orçamento não está disponível.</p>
        : <Body control={control} fixedTarget={fixedTarget} onOpenBudget={onOpenBudget} workspaceId={workspaceId} competence={competence} onOpenCards={onOpenCards} />}
  </section>
}

function Body({ control, fixedTarget, onOpenBudget, workspaceId, competence, onOpenCards }: { control: BudgetControl; fixedTarget: string | null; onOpenBudget: () => void; workspaceId: string; competence: string; onOpenCards: () => void }) {
  const { total, cards, outsideCards } = control
  const rows: Row[] = [
    { ...total, key: 'total', title: 'Meta total', icon: 'target' },
    ...(cards.state === 'NoCards' ? [] : [{ ...cards, key: 'cards', title: 'Cartões de crédito', icon: 'card' as const }]),
    { ...outsideCards, key: 'outside', title: 'Fora dos cartões', icon: 'wallet' },
  ]
  // A meta é global: numa conta isolada só o consumo aparece, sem percentual de atingimento.
  const global = !control.targetsAttributable
  return <>
    <ul className="ov-goal-rows">{rows.map(row => <GoalRow key={row.key} row={row} global={global} details={row.key === 'cards' && !global ? { workspaceId, competence, onOpenCards } : null} />)}</ul>
    {global && <p className="ov-meta">A meta mensal vale para todas as contas: aqui aparece só o consumo desta conta.</p>}
    {!global && !total.targetDefined && <button type="button" onClick={onOpenBudget}>Configurar orçamento</button>}
    {!global && fixedTarget !== null && <p className="ov-budget__fixed"><span>Meta complementar: despesas fixas</span><strong className="ov-mono">{money(fixedTarget)}</strong></p>}
  </>
}

function GoalRow({ row, global, details }: { row: Row; global: boolean; details: { workspaceId: string; competence: string; onOpenCards: () => void } | null }) {
  const [open, setOpen] = useState(false)
  const panel = `ov-card-details-${row.key}`
  const tone = global || row.target === null ? 'none' : row.status === 'Exceeded' ? 'over' : 'within'
  const difference = global ? 'Meta global' : row.target === null ? 'Meta não configurada'
    : row.status === 'Exceeded' ? `${money(row.excess)} acima da meta` : `${money(row.margin)} abaixo da meta`
  return <li className={`ov-goal-row ov-goal-row--${tone}`} aria-label={row.title}>
    <span className="ov-goal-row__icon">{icons[row.icon]}</span>
    <div className="ov-goal-row__body">
      <div className="ov-goal-row__head">{details
        ? <button type="button" className="ov-goal-row__toggle" aria-expanded={open} aria-controls={panel} onClick={() => setOpen(value => !value)}><strong>{row.title}</strong><span className="ov-goal-row__chevron" aria-hidden="true">{open ? '▴' : '▾'}</span></button>
        : <strong>{row.title}</strong>}
        <span className="ov-goal-row__pct">{tone === 'none' ? '' : percentText(row.projectedPercent) ?? '—'}</span></div>
      <div className="ov-track ov-track--thick" role="progressbar" aria-label={`Total previsto de ${row.title}`} aria-valuemin={0} aria-valuemax={100}
        aria-valuenow={tone === 'none' ? 0 : percentNow(row.projectedPercent)} aria-valuetext={tone === 'none' ? difference : `${percentText(row.projectedPercent) ?? 'sem percentual'} da meta`}>
        {tone !== 'none' && <span className="ov-track__done" style={{ width: row.projectedPercent !== null ? barWidth(row.projectedPercent) : row.status === 'Exceeded' ? '100%' : '0%' }} />}</div>
      <div className="ov-goal-row__foot">
        <span className="ov-mono">{money(row.projected)}{tone !== 'none' && <> / {money(row.target)}</>}</span>
        <span className="ov-goal-row__diff">{difference}</span></div>
      {details && open && <OverviewBudgetCardDetails id={panel} workspaceId={details.workspaceId} competence={details.competence} onOpenCards={details.onOpenCards} />}
    </div>
  </li>
}
