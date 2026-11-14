/**
 * Saúde da automação bancária: contrato único do servidor (Central de Automação e Visão Geral usam a mesma regra). Só estados, motivos curtos e datas:
 * nada aqui consulta a Pluggy. Servidores antigos não enviam `health`; nesse caso as telas não mostram a linha nova.
 */
export type SyncHealthState = 'UpToDate' | 'Delayed' | 'Blocked' | 'NeedsAttention' | 'AwaitingFirstImport' | 'AutomationDisabled'

export interface SyncHealth { state: SyncHealthState; reason: string | null; lastSuccessAt: string | null; overdueSince: string | null }
export interface SyncHealthSummary { state: SyncHealthState; connections: number; inState: number; upToDate: number; affectedConnectionIds: string[] }

export const syncHealthLabel: Record<SyncHealthState, string> = {
  UpToDate: 'Em dia', Delayed: 'Atrasada', Blocked: 'Bloqueada', NeedsAttention: 'Requer atenção',
  AwaitingFirstImport: 'Aguardando primeira importação', AutomationDisabled: 'Automação desativada',
}

/** Bloqueada e "requer atenção" pedem uma ação do usuário; na Visão Geral ficam em "Próximas ações" (sem repetir o aviso na linha de status). */
export const needsIntervention = (state: SyncHealthState) => state === 'Blocked' || state === 'NeedsAttention'

/** Motivo em texto. Só afirma o que o servidor sustenta: data do prazo vencido só quando enviada. */
export function syncHealthReason(health: SyncHealth, formatDate: (iso: string) => string): string | null {
  switch (health.reason) {
    case 'ConsentExpired': return 'A autorização do banco expirou. Renove-a no Meu Pluggy; enquanto isso nada é atualizado.'
    case 'RunFailed': return 'A última importação falhou.'
    case 'RunCompletedWithErrors': return 'A última importação terminou com erros.'
    case 'ImportOverdue': return health.overdueSince ? `A importação esperada até ${formatDate(health.overdueSince)} não aconteceu.` : 'A importação esperada não aconteceu no prazo.'
    case 'NoImportYet': return 'Nenhuma importação foi concluída ainda.'
    case 'AutomationPaused': return 'A sincronização automática está pausada para este orçamento.'
    case 'AutomationOff': return 'A sincronização automática está desligada neste ambiente.'
    default: return null
  }
}

/** Próximo passo disponível na Central, conforme o estado. Só aponta ações que existem: renovar no Meu Pluggy, "Sincronizar agora" e a pausa da automação. */
export function syncHealthNextStep(health: SyncHealth, autoEnabled: boolean): string | null {
  switch (health.state) {
    case 'Blocked': return 'Renove a autorização no Meu Pluggy.'
    case 'NeedsAttention': return 'Use "Sincronizar agora" para tentar de novo.'
    case 'Delayed': return 'O Saldo360 tenta de novo sozinho; você também pode usar "Sincronizar agora".'
    case 'AwaitingFirstImport': return autoEnabled ? 'A primeira importação acontece na próxima janela automática; ou use "Sincronizar agora".' : 'Use "Sincronizar agora" para fazer a primeira importação.'
    case 'AutomationDisabled': return health.reason === 'AutomationPaused' ? 'Reative a sincronização automática na aba Automações.' : 'Use "Sincronizar agora" quando quiser atualizar.'
    default: return null
  }
}

/** Impacto só com atraso confirmado (cadência configurada e prazo vencido). */
export const delayedImpact = 'Os movimentos das contas desta conexão podem estar desatualizados.'

export const syncHealthQueryFailed = 'Não foi possível consultar a saúde da automação.'

/** Título do resumo: estado do pior caso e, quando só parte das conexões está nele, "n de m". */
export function syncHealthSummaryLabel(summary: SyncHealthSummary): string {
  const partial = summary.inState < summary.connections && summary.state !== 'UpToDate'
  return `${syncHealthLabel[summary.state]}${partial ? ` · ${summary.inState} de ${summary.connections} conexões` : ''}`
}
