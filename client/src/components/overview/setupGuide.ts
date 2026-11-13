import type { FinancialAccount } from '../../api/accounts'
import type { SixMonthPlan } from '../../api/monthlyPlan'
import type { PluggyConnection } from '../../api/pluggyPoc'
import type { Destination } from './priorityItems'
import { money, monthTitle, scopeLabel } from './overviewText'

// P1: guia da primeira configuração. Cada etapa vem de uma consulta própria (contas, integração, importações, meta PF e planejamento de seis meses), sem depender do resumo
// completo da Visão Geral. Só descreve sinais que as APIs já entregam; nada é gravado nem calculado aqui. Uma etapa só é dada como pendente com evidência: consulta em
// andamento é "Consultando" e consulta com falha é "não consultada" com nova tentativa — nunca se presume pendente.

export type SetupStepKey = 'account' | 'data' | 'budget' | 'projection'
/** `done` conta para retirar o bloco; `checking` = consultando; `unknown` = a consulta falhou; os demais são estados confirmados que ainda pedem algo. */
export type SetupState = 'done' | 'pending' | 'running' | 'partial' | 'blocked' | 'unavailable' | 'checking' | 'unknown'
export type SetupQuery = 'accounts' | 'integration' | 'imports' | 'budget' | 'projection'

export interface SetupAction { label: string; to?: Destination; retry?: SetupQuery }
export interface SetupStep { key: SetupStepKey; title: string; state: SetupState; stateLabel: string; detail: string; actions: SetupAction[] }
export interface SetupGuide {
  steps: SetupStep[]; next: SetupStep
  /** Alguma etapa confirmada ainda pede algo (não é só consulta em andamento ou com falha). */
  applicable: boolean
}

export type Signal<T> = { state: 'loading' } | { state: 'error' } | { state: 'loaded'; data: T }
export interface IntegrationData { available: boolean; connections: PluggyConnection[] }
/** Meta PF da competência atual: `targetDefined` distingue meta ausente de meta definida com valor zero. */
export interface BudgetData { targetDefined: boolean; target: string | null; competence: string }

export interface SetupInput {
  accounts: Signal<FinancialAccount[]>
  integration: Signal<IntegrationData>
  /** Conexões com ao menos uma sincronização concluída (histórico da automação, independente do escopo da Visão Geral). */
  imports: Signal<string[]>
  budget: Signal<BudgetData>
  /** Planejamento de seis meses da competência e do escopo mostrados: é o destino de "Ver projeção". */
  projection: Signal<SixMonthPlan>
  competence: string; scope: { kind: 'primary' | 'all' | 'account'; accountId?: string }
}

const manual: SetupAction = { label: 'Usar conta manual', to: { section: 'accounts' } }
const connect: SetupAction = { label: 'Conectar banco', to: { section: 'automation', context: { automationTab: 'connections' } } }
const badStates = new Set(['ActionRequired', 'ConsentExpired', 'Error'])
const retry = (query: SetupQuery): SetupAction => ({ label: 'Tentar novamente', retry: query })

function accountStep({ accounts, integration }: SetupInput): SetupStep {
  const base = { key: 'account' as const, title: 'Conectar uma conta' }
  if (accounts.state === 'loading') return { ...base, state: 'checking', stateLabel: 'Consultando', detail: 'Consultando as contas do orçamento.', actions: [] }
  if (accounts.state === 'error') return { ...base, state: 'unknown', stateLabel: 'Contas não consultadas', detail: 'Não foi possível consultar as contas. Você pode tentar de novo ou cadastrar a conta manualmente.', actions: [retry('accounts'), manual] }
  const list = accounts.data
  if (list.length > 0) {
    const connected = list.some(a => a.origin === 'Pluggy') || (integration.state === 'loaded' && integration.data.connections.length > 0)
    return { ...base, state: 'done', stateLabel: 'Concluída', actions: [], detail: connected ? 'Há uma conta conectada ao banco.' : 'Há uma conta cadastrada manualmente.' }
  }
  if (integration.state === 'loading') return { ...base, state: 'checking', stateLabel: 'Consultando', detail: 'Nenhuma conta cadastrada. Consultando a integração bancária.', actions: [] }
  if (integration.state === 'error') {
    return { ...base, state: 'unknown', stateLabel: 'Integração não consultada', detail: 'Nenhuma conta cadastrada e não foi possível consultar a integração bancária. Tente de novo ou cadastre a conta manualmente.', actions: [retry('integration'), manual] }
  }
  if (!integration.data.available) {
    return { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'A integração bancária não está disponível neste orçamento. Cadastre a conta manualmente.', actions: [manual] }
  }
  if (integration.data.connections.length > 0) {
    return { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'A conexão foi criada, mas nenhuma conta foi vinculada ainda.',
      actions: [{ label: 'Ver conexão', to: { section: 'automation', context: { automationTab: 'connections' } } }, manual] }
  }
  return { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'Nenhuma conta conectada ou cadastrada.', actions: [connect, manual] }
}

const runningLabel = (importing: boolean) => importing ? 'Importando' : 'Aguardando a primeira importação'

function dataStep({ accounts, integration, imports }: SetupInput, hasAccount: boolean): SetupStep {
  const base = { key: 'data' as const, title: 'Conferir os dados' }
  if (!hasAccount) return { ...base, state: accounts.state === 'loaded' ? 'pending' : 'checking', stateLabel: accounts.state === 'loaded' ? 'Aguardando a conta' : 'Consultando', detail: 'Disponível depois que houver uma conta.', actions: [] }
  if (integration.state === 'loading') return { ...base, state: 'checking', stateLabel: 'Consultando', detail: 'Consultando o estado da conexão.', actions: [] }
  if (integration.state === 'error') return { ...base, state: 'unknown', stateLabel: 'Integração não consultada', detail: 'Não foi possível confirmar o estado da conexão.', actions: [retry('integration')] }
  const connections = integration.data.connections
  if (connections.length === 0) {
    // Percurso manual: saldo inicial informado em alguma conta (mesmo critério da projeção); saldo observado pelo banco também vale.
    const list = accounts.state === 'loaded' ? accounts.data : []
    return list.some(a => a.openingBalance !== null || a.integration?.observedBalance != null)
      ? { ...base, state: 'done', stateLabel: 'Concluída', detail: 'As contas cadastradas têm saldo para uma primeira projeção.', actions: [] }
      : { ...base, state: 'pending', stateLabel: 'Pendente', detail: 'Nenhuma conta tem saldo informado. Informe o saldo da conta para a primeira projeção.', actions: [{ label: 'Usar conta manual', to: { section: 'accounts' } }] }
  }
  const name = (c: PluggyConnection) => c.label ?? c.institution ?? 'Conexão'
  const open = (ids: string[], label: string): SetupAction => ({ label, to: { section: 'automation', context: { automationTab: 'connections', connectionIds: ids } } })
  const bad = connections.filter(c => badStates.has(c.state))
  // Importação desconhecida nunca é presumida pendente: enquanto não se sabe, a etapa fica "Consultando" (ou "não consultada" com nova tentativa).
  if (imports.state === 'error') {
    return bad.length > 0 ? { ...base, state: 'blocked', stateLabel: 'Pendência', actions: [open(bad.map(c => c.id), 'Ver pendência')], detail: `Há uma pendência na conexão: ${bad.map(name).join(', ')}.` }
      : { ...base, state: 'unknown', stateLabel: 'Importação não consultada', detail: 'Não foi possível confirmar a primeira importação.', actions: [retry('imports')] }
  }
  if (imports.state === 'loading') return { ...base, state: 'checking', stateLabel: 'Consultando', detail: 'Consultando a primeira importação.', actions: [] }
  const imported = new Set(imports.data)
  if (bad.length > 0) {
    const firstImportFailed = bad.some(c => !imported.has(c.id))
    return { ...base, state: 'blocked', stateLabel: 'Pendência', actions: [open(bad.map(c => c.id), 'Ver pendência')],
      detail: `${firstImportFailed ? 'A primeira importação não foi concluída' : 'Há uma pendência na conexão'}: ${bad.map(name).join(', ')}.` }
  }
  const waiting = connections.filter(c => !imported.has(c.id))
  if (waiting.length > 0) {
    return { ...base, state: 'running', stateLabel: runningLabel(waiting.some(w => w.state === 'Updating')), actions: [open(waiting.map(w => w.id), 'Ver conexão')],
      detail: 'A conexão foi criada; os dados aparecem depois da primeira importação.' }
  }
  return { ...base, state: 'done', stateLabel: 'Concluída', detail: 'A primeira importação foi concluída.', actions: [] }
}

function budgetStep({ budget }: SetupInput, hasData: boolean): SetupStep {
  const base = { key: 'budget' as const, title: 'Definir o orçamento' }
  if (budget.state === 'error') return { ...base, state: 'unknown', stateLabel: 'Meta não consultada', detail: 'Não foi possível consultar a meta do orçamento PF.', actions: [retry('budget')] }
  if (budget.state === 'loading') return { ...base, state: 'checking', stateLabel: 'Consultando', detail: 'Consultando a meta do orçamento PF.', actions: [] }
  const { targetDefined, target, competence } = budget.data
  const month = monthTitle(competence)
  if (targetDefined) {
    return { ...base, state: 'done', stateLabel: 'Concluída', actions: [],
      detail: target === null ? `Há uma meta total PF para ${month}.` : `Meta total PF de ${money(target)} para ${month}${/^0+\.0+$/.test(target) ? ' (valor zero, definido por você)' : ''}.` }
  }
  return { ...base, state: 'pending', stateLabel: 'Meta ausente', detail: `Ainda não há meta total PF para ${month}.${hasData ? '' : ' Você pode defini-la enquanto os dados chegam.'}`,
    actions: [{ label: 'Configurar orçamento', to: { section: 'budgets', context: { competence } } }] }
}

function projectionStep({ projection, competence, scope }: SetupInput): SetupStep {
  const base = { key: 'projection' as const, title: 'Abrir a primeira projeção' }
  if (projection.state === 'loading') return { ...base, state: 'checking', stateLabel: 'Consultando', detail: `Consultando a projeção de ${monthTitle(competence)}.`, actions: [] }
  if (projection.state === 'error') return { ...base, state: 'unknown', stateLabel: 'Projeção não consultada', detail: `Não foi possível consultar a projeção de ${monthTitle(competence)}.`, actions: [retry('projection')] }
  const plan = projection.data
  const where = `${monthTitle(competence)} · ${scopeLabel(plan.scope)}`
  const month = plan.months.find(m => m.competence === competence) ?? plan.months[0]
  // Destino: Seis meses (detalhado), que aplica a mesma competência e o mesmo escopo.
  const open: SetupAction = { label: 'Ver projeção', to: { section: 'planning', context: { planningTab: 'six', competence, recoveryScope: scope } } }
  if (!month || (!month.coverage.dataComplete && month.closingBalance === null && month.minimumProjectedBalance === null)) {
    return { ...base, state: 'unavailable', stateLabel: 'Projeção indisponível', actions: [], detail: `${where}: os dados atuais não permitem projetar este recorte.` }
  }
  return month.coverage.dataComplete
    ? { ...base, state: 'done', stateLabel: 'Disponível', actions: [open], detail: `${where}: cobertura completa.` }
    : { ...base, state: 'partial', stateLabel: 'Projeção parcial', actions: [open], detail: `${where}: há dados incompletos, então a projeção tem limitações.` }
}

const confirmedOpen = new Set<SetupState>(['pending', 'running', 'partial', 'blocked', 'unavailable'])

/** `null` = todas as etapas aplicáveis concluídas. */
export function buildSetupGuide(input: SetupInput): SetupGuide | null {
  const account = accountStep(input)
  const data = dataStep(input, account.state === 'done')
  const budget = budgetStep(input, data.state === 'done')
  const projection = projectionStep(input)
  const steps = [account, data, budget, projection]
  // Projeção parcial ainda não conclui a configuração: o bloco fica com "Ver projeção" e a limitação explícita até a cobertura ficar completa.
  if (steps.every(step => step.state === 'done')) return null
  const next = steps.find(step => step.state !== 'done') as SetupStep
  return { steps, next, applicable: steps.some(step => confirmedOpen.has(step.state)) }
}
