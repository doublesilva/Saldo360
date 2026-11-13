import type { FinancialDecision, OverviewAccounts } from '../../api/overview'
import type { PlanActualDashboard } from '../../api/planActual'
import type { PluggyConnection } from '../../api/pluggyPoc'
import type { Destination } from './priorityItems'
import { coverageMessage } from './decisionText'
import { money, monthTitle } from './overviewText'

// P1: guia da primeira configuração. Só descreve sinais que as APIs já entregam (contas, conexões, última importação, meta PF, cobertura da projeção);
// nada é gravado e nenhum valor é calculado aqui. Cada etapa tem estado em texto, e só a próxima etapa pendente oferece ação.

export type SetupStepKey = 'account' | 'data' | 'budget' | 'projection'
/** `done` conta para retirar o bloco; `partial` é utilizável com limitação; `blocked` precisa de decisão; `unknown` = sinal não pôde ser lido. */
export type SetupState = 'done' | 'pending' | 'running' | 'partial' | 'blocked' | 'unavailable' | 'unknown'

export interface SetupAction { label: string; to?: Destination; retry?: 'integration' | 'budget' }
export interface SetupStep { key: SetupStepKey; title: string; state: SetupState; stateLabel: string; detail: string; actions: SetupAction[] }
export interface SetupGuide { steps: SetupStep[]; next: SetupStep }

export type IntegrationSignal = { state: 'loading' } | { state: 'error' } | { state: 'loaded'; available: boolean; connections: PluggyConnection[]; /** Conexões com ao menos uma sincronização concluída (histórico da automação, independente do escopo da Visão Geral). */ imported: string[] }
/** Meta PF da competência atual: `targetDefined` distingue meta ausente de meta definida com valor zero. */
export type BudgetSignal = { state: 'loading' } | { state: 'error' } | { state: 'loaded'; targetDefined: boolean; target: string | null; competence: string }

export interface SetupInput {
  accounts: OverviewAccounts; decision: FinancialDecision | null; plan: PlanActualDashboard | null
  integration: IntegrationSignal; budget: BudgetSignal
  /** Competência e escopo que a Visão Geral está mostrando: é o que "Ver projeção" abre. */
  competence: string; scopeText: string; scope: { kind: 'primary' | 'all' | 'account'; accountId?: string }
}

const manual: SetupAction = { label: 'Usar conta manual', to: { section: 'accounts' } }
const badStates = new Set(['ActionRequired', 'ConsentExpired', 'Error'])

function accountStep({ accounts, integration }: SetupInput): SetupStep {
  const base = { key: 'account' as const, title: 'Conectar uma conta' }
  const connections = integration.state === 'loaded' ? integration.connections : []
  if (accounts.items.length > 0) {
    return { ...base, state: 'done', stateLabel: 'Concluída', actions: [],
      detail: connections.length > 0 ? 'Há uma conta conectada ao banco.' : 'Há uma conta cadastrada manualmente.' }
  }
  if (integration.state === 'error') {
    return { ...base, state: 'unknown', stateLabel: 'Integração não consultada', detail: 'Não foi possível consultar a integração bancária. Você pode tentar de novo ou cadastrar a conta manualmente.',
      actions: [{ label: 'Tentar novamente', retry: 'integration' }, manual] }
  }
  if (integration.state === 'loaded' && !integration.available) {
    return { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'A integração bancária não está disponível neste orçamento. Cadastre a conta manualmente.', actions: [manual] }
  }
  if (connections.length > 0) {
    return { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'A conexão foi criada, mas nenhuma conta foi vinculada ainda.',
      actions: [{ label: 'Ver conexão', to: { section: 'automation', context: { automationTab: 'connections' } } }, manual] }
  }
  return { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'Nenhuma conta conectada ou cadastrada.',
    actions: [{ label: 'Conectar banco', to: { section: 'automation', context: { automationTab: 'connections' } } }, manual] }
}

function dataStep({ accounts, integration }: SetupInput, hasAccount: boolean): SetupStep {
  const base = { key: 'data' as const, title: 'Conferir os dados' }
  if (!hasAccount) return { ...base, state: 'pending', stateLabel: 'Aguardando a conta', detail: 'Disponível depois que houver uma conta.', actions: [] }
  if (integration.state === 'error') {
    return { ...base, state: 'unknown', stateLabel: 'Integração não consultada', detail: 'Não foi possível confirmar o estado da conexão.', actions: [{ label: 'Tentar novamente', retry: 'integration' }] }
  }
  const connections = integration.state === 'loaded' ? integration.connections : []
  if (connections.length === 0) {
    // Percurso manual: o critério é o mesmo da projeção, saldo disponível em alguma conta.
    return accounts.items.some(a => a.balanceAvailable)
      ? { ...base, state: 'done', stateLabel: 'Concluída', detail: 'As contas cadastradas têm saldo para uma primeira projeção.', actions: [] }
      : { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'Nenhuma conta tem saldo disponível. Informe o saldo da conta para a primeira projeção.', actions: [{ label: 'Usar conta manual', to: { section: 'accounts' } }] }
  }
  const importedIds = new Set(integration.state === 'loaded' ? integration.imported : [])
  const synced = { get: (id: string) => importedIds.has(id) }
  const name = (c: PluggyConnection) => c.label ?? c.institution ?? 'Conexão'
  const bad = connections.filter(c => badStates.has(c.state))
  const open = (ids: string[], label: string): SetupAction => ({ label, to: { section: 'automation', context: { automationTab: 'connections', connectionIds: ids } } })
  if (bad.length > 0) {
    const ids = bad.map(c => c.id)
    const firstImportFailed = bad.some(c => !synced.get(c.id))
    return { ...base, state: 'blocked', stateLabel: 'Pendência', actions: [open(ids, 'Ver pendência')],
      detail: `${firstImportFailed ? 'A primeira importação não foi concluída' : 'Há uma pendência na conexão'}: ${bad.map(name).join(', ')}.` }
  }
  if (connections.some(c => !synced.get(c.id))) {
    const waiting = connections.filter(c => !synced.get(c.id))
    return { ...base, state: 'running', stateLabel: runningLabel(waiting.some(w => w.state === 'Updating')), actions: [open(waiting.map(w => w.id), 'Ver conexão')],
      detail: 'A conexão foi criada; os dados aparecem depois da primeira importação.' }
  }
  return { ...base, state: 'done', stateLabel: 'Concluída', detail: 'A primeira importação foi concluída.', actions: [] }
}

const runningLabel = (importing: boolean) => importing ? 'Importando' : 'Aguardando a primeira importação'

function budgetStep({ budget }: SetupInput, hasData: boolean): SetupStep {
  const base = { key: 'budget' as const, title: 'Definir o orçamento' }
  if (budget.state === 'error') return { ...base, state: 'unknown', stateLabel: 'Meta não consultada', detail: 'Não foi possível consultar a meta do orçamento PF.', actions: [{ label: 'Tentar novamente', retry: 'budget' }] }
  if (budget.state === 'loading') return { ...base, state: 'pending', stateLabel: 'Consultando', detail: 'Consultando a meta do orçamento PF.', actions: [] }
  const month = monthTitle(budget.competence)
  if (budget.targetDefined) {
    return { ...base, state: 'done', stateLabel: 'Concluída', actions: [],
      detail: budget.target === null ? `Há uma meta total PF para ${month}.` : `Meta total PF de ${money(budget.target)} para ${month}${/^0+\.0+$/.test(budget.target) ? ' (valor zero, definido por você)' : ''}.` }
  }
  return { ...base, state: 'pending', stateLabel: 'Meta ausente', detail: `Ainda não há meta total PF para ${month}.${hasData ? '' : ' Você pode defini-la enquanto os dados chegam.'}`,
    actions: [{ label: 'Configurar orçamento', to: { section: 'budgets', context: { competence: budget.competence } } }] }
}

function projectionStep({ decision, plan, competence, scopeText, scope }: SetupInput): SetupStep {
  const base = { key: 'projection' as const, title: 'Abrir a primeira projeção' }
  const where = `${monthTitle(competence)} · ${scopeText || 'escopo atual'}`
  if (!decision) return { ...base, state: 'pending', stateLabel: 'Consultando', detail: `Consultando a cobertura de ${where}.`, actions: [] }
  if (decision.coverage.state === 'Unavailable') {
    return { ...base, state: 'unavailable', stateLabel: 'Projeção indisponível', actions: [],
      detail: `${where}: ${coverageMessage(decision.coverage)}` }
  }
  const open: SetupAction = { label: 'Ver projeção', to: { section: 'planning', context: { planningTab: 'six', competence, recoveryScope: scope } } }
  const partial = decision.state === 'Partial' || (plan !== null && !plan.coverage.complete)
  return partial
    ? { ...base, state: 'partial', stateLabel: 'Projeção parcial', actions: [open], detail: `${where}: há dados incompletos, então a projeção tem limitações. ${decision.notes[0] ?? ''}`.trim() }
    : { ...base, state: 'done', stateLabel: 'Disponível', actions: [open], detail: `${where}: cobertura completa.` }
}

/** `null` = nada a configurar (todas as etapas aplicáveis concluídas) ou ainda sem sinais suficientes para decidir. */
export function buildSetupGuide(input: SetupInput): SetupGuide | null {
  const account = accountStep(input)
  const data = dataStep(input, account.state === 'done')
  const budget = budgetStep(input, data.state === 'done')
  const projection = projectionStep(input)
  const steps = [account, data, budget, projection]
  // Projeção parcial ainda não conclui a configuração: o bloco fica com "Ver projeção" e a limitação explícita até a cobertura ficar completa.
  if (steps.every(step => step.state === 'done')) return null
  const next = steps.find(step => step.state !== 'done') as SetupStep
  return { steps, next }
}
