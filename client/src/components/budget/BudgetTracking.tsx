import { useCallback } from 'react'
import { getBudgetSummary, type BudgetSummary } from '../../api/overview'
import { OperationalLoading } from '../OperationalFeedback'
import { monthTitle, money } from '../overview/overviewText'
import { useLoad } from '../overview/useLoad'
import './BudgetTracking.css'

/**
 * Acompanhamento do mês: Meta, Consumido e Disponível do orçamento PF (todas as contas PF), na ordem em que se lê a pergunta "quanto posso gastar?".
 * Nada é calculado aqui: meta, realizado, previsto, disponível e estouro vêm prontos do servidor (resolvedor único de metas). O previsto (realizado + comprometido + estimado)
 * é uma projeção e aparece separado do consumo realizado. Competência e valores vêm da MESMA resposta; trocar de mês mostra o carregamento, nunca valores do mês anterior.
 */
export function BudgetTracking({ workspaceId, competence, refreshKey = 0 }: { workspaceId: string; competence: string; refreshKey?: number }) {
  const { load, retry } = useLoad(`${workspaceId}|${competence}|${refreshKey}`, useCallback((signal: AbortSignal) => getBudgetSummary(workspaceId, competence, signal), [workspaceId, competence]))
  const summary = load.state === 'loaded' && load.data.total ? { ...load.data, total: load.data.total } : null
  return <section className="budget-tracking" aria-labelledby="budget-tracking-title">
    <div className="budget-tracking__head">
      <h4 id="budget-tracking-title">Acompanhamento do mês</h4>
      <p className="meta">Todas as contas PF · {summary ? monthTitle(summary.competence) : monthTitle(competence)}</p>
    </div>
    {load.state === 'loading' && <OperationalLoading>Carregando o acompanhamento…</OperationalLoading>}
    {load.state === 'error' && <div className="operational-feedback operational-feedback--error" role="alert"><p>Não foi possível carregar o acompanhamento deste mês.</p><button type="button" onClick={retry}>Tentar novamente</button></div>}
    {load.state === 'loaded' && !summary && <p className="meta" role="status">O acompanhamento do orçamento PF não está disponível para este mês.</p>}
    {summary && <Body summary={summary} />}
  </section>
}

function Body({ summary }: { summary: BudgetSummary & { total: NonNullable<BudgetSummary['total']> } }) {
  const { total } = summary
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
    {summary.state === 'Partial' && <p className="notice notice--warning" role="status">
      Cobertura parcial: parte dos dados deste mês ainda não pôde ser calculada, então os valores podem mudar.{summary.notes.length > 0 ? ` ${summary.notes.join(' ')}` : ''}</p>}
  </>
}
