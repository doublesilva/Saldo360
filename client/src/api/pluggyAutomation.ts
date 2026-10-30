import { requestJson } from './apiErrors'
import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { ExpenseNature } from './expenseNature'

/**
 * R04: central de automação Pluggy + agente de IA. Tudo vem do banco e da configuração do servidor: ler nunca dispara sincronização nem chamada à IA.
 * Só contagens, estados, datas e códigos curtos — nenhum token, payload, descrição ou valor de lançamento passa pelo histórico.
 */
export type RunKind = 'Sync' | 'Categorization'
export type RunState = 'Running' | 'Completed' | 'CompletedWithPending' | 'Failed'
export type RunTrigger = 'Manual' | 'Scheduled' | 'AfterSync'

export interface AiView { enabled: boolean; available: boolean; model: string | null; reason: string | null }
export interface ServerView {
  integrationAvailable: boolean; importEnabled: boolean; schedulerEnabled: boolean; defaultIntervalHours: number; windowStartHour: number; windowEndHour: number
  timeZone: string; intervalOptions: number[]; ai: AiView
}
export interface AutomationSettings { syncPaused: boolean; syncIntervalHours: number; syncIntervalIsDefault: boolean; categorizeAfterSync: boolean; categorizeConsentAt: string | null }
export interface SyncCounts { accountsTotal: number; accountsSynced: number; seen: number; created: number; updated: number; unchanged: number; missing: number; errors: number; errorSummary: string | null }
export interface CategorizationCounts {
  considered: number; classifiedLocal: number; classifiedAi: number; pending: number; skippedChanged: number; aiItemsSent: number; aiCalls: number
  usedAi: boolean; model: string | null; aiStoppedBecause: string | null
}
export interface AutomationRun {
  id: string; kind: RunKind; trigger: RunTrigger; state: RunState; startedAt: string; finishedAt: string | null; durationSeconds: number | null
  connectionId: string | null; scope: string | null; relatedRunId: string | null; failureCode: string | null; interrupted: boolean
  sync: SyncCounts | null; categorization: CategorizationCounts | null
}
export type SyncAutomationState = 'Unavailable' | 'NoConnections' | 'Running' | 'SchedulerOff' | 'Paused' | 'Scheduled'
export type CategorizationAutomationState = 'Running' | 'AiUnavailable' | 'NothingToDo' | 'Ready'
export interface AutomationOverview {
  now: string; server: ServerView; settings: AutomationSettings
  sync: { state: SyncAutomationState; connections: number; connectionsNeedingAttention: number; running: boolean; nextRunAt: string | null; lastRun: AutomationRun | null }
  categorization: { state: CategorizationAutomationState; eligible: number; needsReview: number; notEvaluated: number; running: boolean; lastRun: AutomationRun | null }
}
export interface AutomationUpdate { syncPaused?: boolean; syncIntervalHours?: number; categorizeAfterSync?: boolean; confirmExternal?: boolean }
export interface RunFilter { kind?: RunKind | ''; state?: RunState | ''; from?: string; to?: string; page?: number; pageSize?: number }
export interface RunPage { items: AutomationRun[]; total: number; page: number; pageSize: number }

export type ReviewKind = 'nature' | 'category'
export type ReviewAction = 'Transaction' | 'CardPurchase' | 'Planned' | 'None'
export interface ReviewItem {
  action: ReviewAction; recordId: string; cardId: string | null; date: string; description: string; amount: string; categoryId: string | null; categoryPath: string | null
  expenseNature: ExpenseNature; cardName: string | null; accountName: string | null; imported: boolean; canChangeCategory: boolean; canChangeNature: boolean; scope: string
}
export interface ClassificationReview {
  kind: ReviewKind; from: string | null; to: string | null; wholeBudget: boolean; items: ReviewItem[]; total: number; page: number; pageSize: number; unclassifiableCount: number
}

const base = (workspaceId: string) => `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/pluggy-integration`
const get = <T,>(url: string, fallback: string, signal?: AbortSignal) => requestJson<T>(url, { headers: { Accept: 'application/json' }, signal }, { fallback, timeoutMs: 60_000 })
const send = async <T,>(method: 'PUT' | 'POST', url: string, body: unknown, fallback: string) => requestJson<T>(url, {
  method, headers: { Accept: 'application/json', 'Content-Type': 'application/json', [CSRF_HEADER]: await getCsrfToken() }, body: JSON.stringify(body),
}, { fallback, timeoutMs: 60_000 })

export const getAutomationOverview = (workspaceId: string, signal?: AbortSignal) =>
  get<AutomationOverview>(`${base(workspaceId)}/automation`, 'Falha ao carregar a central de automação.', signal)

export const updateAutomation = (workspaceId: string, update: AutomationUpdate) =>
  send<AutomationSettings>('PUT', `${base(workspaceId)}/automation`, update, 'Falha ao salvar a preferência.')

export function listAutomationRuns(workspaceId: string, filter: RunFilter, signal?: AbortSignal) {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(filter)) if (value !== undefined && value !== '') query.set(key, String(value))
  return get<RunPage>(`${base(workspaceId)}/automation/runs?${query}`, 'Falha ao carregar o histórico.', signal)
}

export const getAutomationRun = (workspaceId: string, kind: RunKind, id: string, signal?: AbortSignal) =>
  get<AutomationRun>(`${base(workspaceId)}/automation/runs/${kind}/${encodeURIComponent(id)}`, 'Falha ao carregar a execução.', signal)

export function getClassificationReview(workspaceId: string, filter: { kind: ReviewKind; from?: string; to?: string; page?: number; pageSize?: number }, signal?: AbortSignal) {
  const query = new URLSearchParams()
  for (const [key, value] of Object.entries(filter)) if (value !== undefined && value !== '') query.set(key, String(value))
  return get<ClassificationReview>(`${base(workspaceId)}/classification-review?${query}`, 'Falha ao carregar a revisão da classificação.', signal)
}

/** Corrige só a categoria de uma compra de cartão (inclusive importada): valor, parcelas e fatura não mudam; a escolha sobrevive às sincronizações. */
export const changePurchaseCategory = (workspaceId: string, cardId: string, purchaseId: string, categoryId: string) =>
  send<unknown>('PUT', `/api/financial-workspaces/${encodeURIComponent(workspaceId)}/credit-cards/${encodeURIComponent(cardId)}/purchases/${encodeURIComponent(purchaseId)}/category`, { categoryId }, 'Falha ao salvar a categoria da compra.')

export const runCategorizationNow = (workspaceId: string, useAi: boolean) =>
  send<unknown>('POST', `${base(workspaceId)}/categorization/run`, { useAi, confirmExternal: useAi }, 'Falha ao categorizar os lançamentos.')

export const runStateLabels: Record<RunState, string> = {
  Running: 'Em andamento', Completed: 'Concluída', CompletedWithPending: 'Concluída com pendências', Failed: 'Falhou',
}
export const runKindLabels: Record<RunKind, string> = { Sync: 'Sincronização bancária', Categorization: 'Categorização com IA' }
export const runTriggerLabels: Record<RunTrigger, string> = { Manual: 'Manual', Scheduled: 'Agendada', AfterSync: 'Após a sincronização' }

export const failureLabels: Record<string, string> = {
  Interrupted: 'A execução foi interrompida (o servidor parou ou reiniciou) e foi encerrada como falha. Os dados já salvos foram mantidos.',
  AiUnavailable: 'O agente de IA não está disponível.', NotCompleted: 'A categorização não pôde ser concluída.', Cancelled: 'A execução foi cancelada.',
  UnexpectedError: 'Erro inesperado durante a execução.', Abandoned: 'A execução foi interrompida e recuperada.',
}

export const syncStateText: Record<SyncAutomationState, string> = {
  Unavailable: 'Indisponível neste ambiente', NoConnections: 'Sem conexões com conta integrada', Running: 'Sincronizando agora', SchedulerOff: 'Agendamento desligado no servidor',
  Paused: 'Pausada', Scheduled: 'Ativa',
}
export const categorizationStateText: Record<CategorizationAutomationState, string> = {
  Running: 'Categorizando agora', AiUnavailable: 'Agente de IA indisponível', NothingToDo: 'Nada elegível no momento', Ready: 'Pronta',
}
export const aiReasonText: Record<string, string> = {
  AiNotConfigured: 'O agente de IA não está configurado neste ambiente.', AiDisabled: 'A classificação por IA está desligada neste ambiente.',
}
