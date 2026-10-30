import { BUSINESS_TIME_ZONE } from './dates'
import { formatDate } from './money'
import type { AutomationRun } from './api/pluggyAutomation'

/** Data e hora no fuso do calendário de negócio ("30/10/2026 14:05"); o horário do navegador não interfere. */
export const whenSp = (iso: string | null): string => iso
  ? `${formatDate(new Intl.DateTimeFormat('en-CA', { timeZone: BUSINESS_TIME_ZONE, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date(iso)))} ${new Date(iso).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit', timeZone: BUSINESS_TIME_ZONE })}`
  : '—'

export const durationText = (seconds: number | null): string => seconds === null ? '—' : seconds < 60 ? `${seconds} s` : `${Math.floor(seconds / 60)} min ${seconds % 60} s`

/**
 * As contagens são por operação e nunca se somam entre etapas: a sincronização conta o que leu e gravou; a categorização conta o que classificou. Este texto
 * descreve só a etapa da própria execução.
 */
export function countsText(run: AutomationRun): string {
  if (run.sync) {
    const c = run.sync
    return `Lidas ${c.seen} · novas ${c.created} · atualizadas ${c.updated} · sem mudança ${c.unchanged} · ausentes na origem ${c.missing} · contas com erro ${c.errors} de ${c.accountsTotal}`
  }
  if (run.categorization) {
    const c = run.categorization
    return `Elegíveis ${c.considered} · classificadas sem IA ${c.classifiedLocal} · pela IA ${c.classifiedAi} · pendentes ${c.pending} · preservadas por escolha sua ${c.skippedChanged}`
      + (c.usedAi ? ` · enviadas à IA ${c.aiItemsSent} em ${c.aiCalls} chamada(s)` : ' · sem IA')
  }
  return 'Sem contagens.'
}
