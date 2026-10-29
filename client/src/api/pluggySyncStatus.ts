import { requestJson } from './apiErrors'

/**
 * T09.6: monitoramento da sincronização das conexões Pluggy. Vem do banco (não consulta a Pluggy). Nada aqui carrega token, credencial,
 * dado bancário ou mensagem da instituição: só estados, datas, contagens e códigos curtos de erro.
 */
export type SyncStatus = 'NeverSynced' | 'Running' | 'Completed' | 'CompletedWithErrors' | 'Failed' | 'ConsentExpired' | 'SourceNotUpdated'

export interface SyncRunSummary {
  id: string; startedAt: string; finishedAt: string | null; status: 'Running' | 'Completed' | 'CompletedWithErrors' | 'Failed'
  created: number; updated: number; errors: number; errorSummary: string | null; trigger: 'Manual' | 'Scheduled'
}

export interface SyncConnection {
  connectionId: string
  label: string | null
  institution: string | null
  integratedAccounts: number
  status: SyncStatus
  lastAttemptAt: string | null
  lastSuccessAt: string | null
  nextAttemptAt: string | null
  lastTrigger: 'Manual' | 'Scheduled' | null
  lastDurationSeconds: number | null
  lastCreated: number
  lastUpdated: number
  lastError: string | null
  consecutiveFailures: number
  consentExpiresAt: string | null
  consentExpiringSoon: boolean
  sourceUpdatedAt: string | null
  recentRuns: SyncRunSummary[]
}

export interface SyncMonitor {
  now: string
  auto: { enabled: boolean; intervalHours: number; windowStartHour: number; windowEndHour: number; maxConnectionsPerRun: number; pollIntervalMinutes: number }
  connections: SyncConnection[]
}

export const getSyncMonitor = (workspaceId: string, signal?: AbortSignal) =>
  requestJson<SyncMonitor>(`/api/financial-workspaces/${encodeURIComponent(workspaceId)}/pluggy-integration/sync-status`, { signal, headers: { Accept: 'application/json' } })

export const syncStatusLabels: Record<SyncStatus, string> = {
  NeverSynced: 'Ainda não sincronizada',
  Running: 'Sincronizando…',
  Completed: 'Sincronização concluída',
  CompletedWithErrors: 'Concluída com erros',
  Failed: 'Falha na sincronização',
  ConsentExpired: 'Autorização expirada',
  SourceNotUpdated: 'Atualização da instituição ainda não disponível',
}

export const triggerLabels: Record<string, string> = { Manual: 'Manual', Scheduled: 'Automática' }

/** Códigos curtos de erro ("RateLimited:transactions", "ConsentExpired"...) em frases para o usuário. */
export function describeSyncError(summary: string | null): string | null {
  if (!summary) return null
  const text: Record<string, string> = {
    ConsentExpired: 'a autorização expirou: renove-a no Meu Pluggy', RateLimited: 'limite de requisições da Pluggy', Timeout: 'a Pluggy demorou a responder',
    Unavailable: 'a Pluggy está indisponível', Authentication: 'a Pluggy recusou as credenciais', Forbidden: 'acesso negado pela Pluggy', FinancialApplyFailed: 'falha ao processar os lançamentos',
    Truncated: 'coleta incompleta', Abandoned: 'execução interrompida e recuperada', UnexpectedError: 'erro inesperado',
  }
  const parts = summary.split(';').map(part => part.trim().split(':')[0]).filter(Boolean)
  return [...new Set(parts.map(code => text[code] ?? code))].join('; ')
}
