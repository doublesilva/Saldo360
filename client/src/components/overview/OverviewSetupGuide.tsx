import { useCallback, useEffect, useState } from 'react'
import { listAccounts } from '../../api/accounts'
import { getBudgetSummary } from '../../api/overview'
import type { PlanActualScopeChoice } from '../../api/planActual'
import { listAutomationRuns } from '../../api/pluggyAutomation'
import { isPluggyIntegrationAvailable, listIntegrationConnections } from '../../api/pluggyIntegration'
import type { Go } from './Overview'
import { buildSetupGuide, type Signal, type SetupAction, type SetupQuery, type SetupStep } from './setupGuide'
import { useLoad, type Load } from './useLoad'
import type { SixMonthLoad } from './useSixMonthPlan'
import './OverviewSetup.css'

// P1: "Configure seu orçamento". Cada etapa tem a sua consulta (contas, integração, importações, meta PF da competência atual e planejamento de seis meses do recorte mostrado), todas
// em paralelo e independentes do resumo da Visão Geral: a falha ou a lentidão de uma não esconde as demais e cada uma tem o seu "Tentar novamente". Leitura somente: abrir o bloco
// nunca conecta, cadastra nem grava. Ao voltar de outra tela a Visão Geral é montada de novo e tudo é relido. Respostas de outro orçamento, mês ou escopo são descartadas por `useLoad`.

const signal = <T,>(load: Load<T>): Signal<T> => load.state === 'loaded' ? { state: 'loaded', data: load.data } : { state: load.state }

const currentCompetence = () => { const now = new Date(); return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}` }

// Conclusão já confirmada neste navegador: uma falha transitória ao reler não reabre o guia. Só conveniência por visitante; sem armazenamento o guia continua correto.
const doneKey = (workspaceId: string) => `saldo360.setup-done.${workspaceId}`
const wasDone = (workspaceId: string) => { try { return window.localStorage.getItem(doneKey(workspaceId)) === '1' } catch { return false } }
const markDone = (workspaceId: string, done: boolean) => { try { if (done) window.localStorage.setItem(doneKey(workspaceId), '1'); else window.localStorage.removeItem(doneKey(workspaceId)) } catch { /* sem armazenamento */ } }

export function OverviewSetupGuide({ workspaceId, competence, scope, refreshKey, plan, onNavigate }: {
  workspaceId: string; competence: string; scope: PlanActualScopeChoice; refreshKey: number; plan: SixMonthLoad; onNavigate: Go
}) {
  const [current] = useState(currentCompetence)
  const base = `${workspaceId}|${refreshKey}`
  const accounts = useLoad(base, useCallback((s: AbortSignal) => listAccounts(workspaceId, false, s), [workspaceId]))
  const integration = useLoad(base, useCallback(async (s: AbortSignal) => {
    if (!await isPluggyIntegrationAvailable(workspaceId, s)) return { available: false, connections: [] }
    return { available: true, connections: await listIntegrationConnections(workspaceId, s) }
  }, [workspaceId]))
  // Primeira importação = sincronização concluída no histórico (por conexão; execução sem conexão cobre todas). Não depende do escopo da Visão Geral nem do dashboard.
  const imports = useLoad(base, useCallback(async (s: AbortSignal) => {
    const runs = await listAutomationRuns(workspaceId, { kind: 'Sync', pageSize: 100 }, s)
    const done = runs.items.filter(run => run.state === 'Completed' || run.state === 'CompletedWithPending')
    return done.some(run => !run.connectionId) ? ['*'] : done.map(run => run.connectionId as string)
  }, [workspaceId]))
  const budget = useLoad(`${base}|${current}`, useCallback(async (s: AbortSignal) => {
    const summary = await getBudgetSummary(workspaceId, current, s)
    if (summary.state === 'Unavailable' || !summary.total) throw new Error('Meta indisponível')
    return { targetDefined: summary.total.targetDefined, target: summary.total.target, competence: current }
  }, [workspaceId, current]))
  // Planejamento de seis meses: leitura compartilhada com a Evolução (mesmo recorte); o guia só procura nela o mês da competência.
  const projection = plan

  const retries: Record<SetupQuery, () => void> = { accounts: accounts.retry, integration: integration.retry, imports: imports.retry, budget: budget.retry, projection: projection.retry }
  const importedSignal = signal(imports.load)
  const guide = buildSetupGuide({
    accounts: signal(accounts.load), integration: signal(integration.load), competence, scope: { kind: scope.kind, accountId: scope.accountId }, budget: signal(budget.load), projection: signal(projection.load),
    // "*" = execução sem conexão: vale para todas as conexões conhecidas.
    imports: importedSignal.state === 'loaded' && importedSignal.data.includes('*') && integration.load.state === 'loaded'
      ? { state: 'loaded', data: integration.load.data.connections.map(c => c.id) } : importedSignal,
  })

  const allConfirmed = guide === null
  const everyone = [accounts.load, integration.load, imports.load, budget.load, projection.load]
  // Só grava a conclusão quando todas as consultas deste ciclo responderam.
  useEffect(() => { if (allConfirmed && everyone.every(load => load.state === 'loaded')) markDone(workspaceId, true); else if (guide?.applicable) markDone(workspaceId, false) })

  if (!guide) return null
  // Concluído antes e agora só há consulta em andamento ou com falha (nenhuma etapa confirmada pendente): não reabre o guia.
  if (!guide.applicable && wasDone(workspaceId)) return null
  // Sem nenhuma etapa confirmada pedindo algo, só se mostra o guia para oferecer nova tentativa de uma consulta que falhou; consulta em andamento não basta como evidência.
  if (!guide.applicable && !guide.steps.some(step => step.state === 'unknown')) return null

  const run = (action: SetupAction) => {
    if (action.retry) retries[action.retry]()
    else if (action.to) onNavigate(action.to.section, action.to.context)
  }
  const button = (action: SetupAction, primary: boolean) => <button key={action.label} type="button" className={primary ? 'button--primary' : 'ov-link'} onClick={() => run(action)}>{action.label}</button>
  // Navegação só na próxima etapa; "Tentar novamente" aparece em qualquer etapa cuja consulta falhou, e atualiza só essa consulta.
  const shownActions = (step: SetupStep) => step === guide.next ? step.actions : step.actions.filter(action => action.retry)
  const stepItem = (step: SetupStep, index: number) => <li key={step.key} className={`ov-setup__step ov-setup__step--${step.state}${step === guide.next ? ' ov-setup__step--next' : ''}`} aria-current={step === guide.next ? 'step' : undefined}>
    <p className="ov-setup__head"><span className="ov-setup__index">Etapa {index + 1}</span> <strong>{step.title}</strong> <span className="ov-setup__state">{step.stateLabel}</span></p>
    <p className="ov-setup__detail">{step.detail}</p>
    {shownActions(step).length > 0 && <div className="ov-setup__actions">{shownActions(step).map((action, i) => button(action, i === 0 && step === guide.next))}</div>}
  </li>

  return <section className="ov-setup" aria-labelledby="ov-setup-title" aria-busy={guide.steps.some(step => step.state === 'checking') || undefined}>
    <h3 id="ov-setup-title" className="ov-setup__title">Configure seu orçamento</h3>
    <p className="ov-setup__next"><span className="ov-setup__label">Próxima etapa</span> <strong>{guide.next.title}</strong></p>
    <ol className="ov-setup__steps">{guide.steps.map(stepItem)}</ol>
  </section>
}
