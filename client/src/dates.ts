/** Fuso do calendário de negócio, o mesmo usado pela API para recusar datas futuras. */
export const BUSINESS_TIME_ZONE = 'America/Sao_Paulo'

// en-CA formata como yyyy-MM-dd, o formato do <input type="date"> e da API.
const isoDate = new Intl.DateTimeFormat('en-CA', {
  timeZone: BUSINESS_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
})

/** Data civil de hoje em America/Sao_Paulo ("2026-10-10"), independente do fuso do navegador. */
export function todayInSaoPaulo(now: Date = new Date()): string {
  return isoDate.format(now)
}
