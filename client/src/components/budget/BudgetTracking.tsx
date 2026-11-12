import { useCallback } from 'react'
import { getDashboardOverview, type BudgetControl } from '../../api/overview'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { monthTitle, money } from '../overview/overviewText'
import { useLoad } from '../overview/useLoad'

/**
 * Acompanhamento do mês: Meta, Consumido e Disponível do orçamento PF (todas as contas PF), na ordem em que se lê a pergunta "quanto posso gastar?".
 * Nada é calculado aqui: meta, realizado, previsto, disponível e estouro vêm prontos do servidor (resolvedor único de metas). O previsto (realizado + comprometido + estimado)
 * é uma projeção e aparece separado do consumo realizado. Competência e valores vêm da MESMA resposta; trocar de mês mostra o carregamento, nunca valores do mês anterior.
 */
export function BudgetTracking({ workspaceId, competence, refreshKey = 0 }: { workspaceId: string; competence: string; refreshKey?: number }) {
  const { load, retry } = useLoad(`${workspaceId}|${competence}|${refreshKey}`, useCallback((signal: AbortSignal) => getDashboardOverview(workspaceId, competence, { kind: 'all' }, signal), [workspaceId, competence]))
  const control = load.state === 'loaded' ? load.data.pfBudgetControl ?? null : null
  return <section className="budget-tracking" aria-labelledby="budget-tracking-title">
    <div className="budget-tracking__head">
      <h4 id="budget-tracking-title">Acompanhamento do mês</h4>
      <p className="meta">Todas as contas PF · {control ? monthTitle(control.competence) : monthTitle(competence)}</p>
    </div>
    {load.state === 'loading' && <OperationalLoading>Carregando o acompanhamento…</OperationalLoading>}
    {load.state === 'error' && <OperationalError error={load.error} onRetry={retry} />}
    {load.state === 'loaded' && !control && <p className="meta" role="status">O acompanhamento do orçamento PF não está disponível para este mês.</p>}
    {control && <Body control={control} />}
  </section>
}

function Body({ control }: { control: BudgetControl }) {
  const { total } = control
  const defined = total.targetDefined && total.target !== null
  const exceeded = defined && total.status === 'Exceeded'
  return <>
    <ul className="budget-tracking__figures">
      <li className="budget-figure">
        <span className="budget-figure__label">Meta</span>
        <strong className="budget-figure__value">{defined ? money(total.target) : 'Meta não definida'}</strong>
        <span className="meta">{defined ? 'Limite de gastos do mês' : 'Defina a meta em "Orçamento padrão", abaixo.'}</span>
      </li>
      <li className="budget-figure">
        <span className="budget-figure__label">Consumido</span>
        <strong className="budget-figure__value">{money(total.realized)}</strong>
        <span className="meta">Realizado até agora, sem projeção</span>
      </li>
      <li className={`budget-figure${exceeded ? ' budget-figure--over' : ''}`}>
        <span className="budget-figure__label">Disponível</span>
        <strong className="budget-figure__value">{!defined ? 'Indisponível' : exceeded ? 'Meta estourada' : money(total.margin)}</strong>
        <span className="meta">{!defined ? 'Sem meta, não há quanto restar.' : exceeded ? `${money(total.excess)} acima da meta, considerando o previsto.` : 'Meta menos o total previsto do mês.'}</span>
      </li>
    </ul>
    <p className="budget-tracking__projected meta">
      <strong>Projetado:</strong> total previsto de {money(total.projected)} (realizado + comprometido + estimado). Isto é uma projeção, não é consumo realizado.
    </p>
    {control.state === 'Partial' && <p className="notice notice--warning" role="status">
      Cobertura parcial: parte dos dados deste mês ainda não pôde ser calculada, então os valores podem mudar.{control.notes.length > 0 ? ` ${control.notes.join(' ')}` : ''}</p>}
  </>
}
