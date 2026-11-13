import { useCallback } from 'react'
import { getBudgetSummary, type DashboardOverview } from '../../api/overview'
import type { PlanActualDashboard, PlanActualScopeChoice } from '../../api/planActual'
import { isPluggyIntegrationAvailable, listIntegrationConnections } from '../../api/pluggyIntegration'
import type { Go } from './Overview'
import { buildSetupGuide, type BudgetSignal, type IntegrationSignal, type SetupAction, type SetupStep } from './setupGuide'
import { useLoad } from './useLoad'
import './OverviewSetup.css'

// P1: "Configure seu orçamento". Leitura somente: abrir o bloco nunca conecta, cadastra nem grava. As ações só navegam para as telas existentes; ao voltar à Visão Geral
// ela é montada de novo e todos os sinais são lidos outra vez, então o progresso reflete o que está confirmado no servidor.

export function OverviewSetupGuide({ workspaceId, competence, scope, scopeText, data, plan, refreshKey, onNavigate }: {
  workspaceId: string; competence: string; scope: PlanActualScopeChoice; scopeText: string; data: DashboardOverview | null; plan: PlanActualDashboard | null; refreshKey: number; onNavigate: Go
}) {
  const integrationLoad = useLoad(`${workspaceId}|${refreshKey}`, useCallback(async (signal: AbortSignal) => {
    if (!await isPluggyIntegrationAvailable(workspaceId, signal)) return { available: false, connections: [] }
    return { available: true, connections: await listIntegrationConnections(workspaceId, signal) }
  }, [workspaceId]))

  // A meta é a da competência atual, não a navegada. Quando coincidem, reaproveita o orçamento PF já carregado pela Visão Geral (sem nova consulta).
  const current = data?.decision?.context.currentCompetence ?? competence
  const reused = data && current === competence ? data.pfBudgetControl ?? null : null
  const budgetLoad = useLoad(`${workspaceId}|${current}|${data ? 'ready' : 'wait'}|${reused ? 'reuse' : 'fetch'}|${refreshKey}`, useCallback(async (signal: AbortSignal) => {
    if (!data || reused) return null
    return getBudgetSummary(workspaceId, current, signal)
  }, [workspaceId, current, data, reused]))

  if (!data) return null

  const integration: IntegrationSignal = integrationLoad.load.state === 'loaded' ? { state: 'loaded', ...integrationLoad.load.data } : { state: integrationLoad.load.state }
  let budget: BudgetSignal
  if (reused) budget = { state: 'loaded', targetDefined: reused.total.targetDefined, target: reused.total.target, competence: current }
  else if (budgetLoad.load.state === 'loaded' && budgetLoad.load.data) {
    const total = budgetLoad.load.data.total
    budget = budgetLoad.load.data.state === 'Unavailable' || !total ? { state: 'error' } : { state: 'loaded', targetDefined: total.targetDefined, target: total.target, competence: current }
  } else budget = { state: budgetLoad.load.state === 'error' ? 'error' : 'loading' }

  // Enquanto o estado da integração carrega não se mostra nada: evita piscar o bloco para quem já terminou a configuração.
  if (integration.state === 'loading') return null
  const guide = buildSetupGuide({ accounts: data.accounts, decision: data.decision ?? null, plan, integration, budget, competence, scopeText, scope: { kind: scope.kind, accountId: scope.accountId } })
  if (!guide) return null

  const run = (action: SetupAction) => {
    if (action.retry === 'integration') integrationLoad.retry()
    else if (action.retry === 'budget') budgetLoad.retry()
    else if (action.to) onNavigate(action.to.section, action.to.context)
  }
  const button = (action: SetupAction, primary: boolean) => <button key={action.label} type="button" className={primary ? 'button--primary' : 'ov-link'} onClick={() => run(action)}>{action.label}</button>
  const stepItem = (step: SetupStep, index: number) => <li key={step.key} className={`ov-setup__step ov-setup__step--${step.state}${step === guide.next ? ' ov-setup__step--next' : ''}`} aria-current={step === guide.next ? 'step' : undefined}>
    <p className="ov-setup__head"><span className="ov-setup__index">Etapa {index + 1}</span> <strong>{step.title}</strong> <span className="ov-setup__state">{step.stateLabel}</span></p>
    <p className="ov-setup__detail">{step.detail}</p>
    {step === guide.next && step.actions.length > 0 && <div className="ov-setup__actions">{step.actions.map((action, i) => button(action, i === 0))}</div>}
  </li>

  return <section className="ov-setup" aria-labelledby="ov-setup-title">
    <h3 id="ov-setup-title" className="ov-setup__title">Configure seu orçamento</h3>
    <p className="ov-setup__next"><span className="ov-setup__label">Próxima etapa</span> <strong>{guide.next.title}</strong></p>
    <ol className="ov-setup__steps">{guide.steps.map(stepItem)}</ol>
  </section>
}
