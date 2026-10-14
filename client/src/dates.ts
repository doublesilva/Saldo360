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

/** Último dia do mês de uma data "yyyy-MM-dd" (ex.: "2026-02-10" → "2026-02-28"), só com a data civil. */
export function endOfMonth(date: string): string {
  const [year, month] = date.split('-').map(Number)
  const lastDay = new Date(Date.UTC(year, month, 0)).getUTCDate()
  return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}-${String(lastDay).padStart(2, '0')}`
}

/** Primeiro dia do mês de uma data civil, sem conversão pelo fuso do navegador. */
export function startOfMonth(date: string): string {
  return `${date.slice(0, 7)}-01`
}

/** Soma meses a uma data civil e devolve o primeiro dia do mês resultante. */
export function addCalendarMonths(date: string, amount: number): string {
  const [year, month] = date.split('-').map(Number)
  const shifted = new Date(Date.UTC(year, month - 1 + amount, 1))
  return `${shifted.getUTCFullYear()}-${String(shifted.getUTCMonth() + 1).padStart(2, '0')}-01`
}

/** Nome completo do mês e ano em português, calculado em UTC para preservar o mês civil. */
export function formatMonthYear(date: string): string {
  const [year, month] = date.split('-').map(Number)
  const text = new Intl.DateTimeFormat('pt-BR', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(
    new Date(Date.UTC(year, month - 1, 1)),
  )
  return text.charAt(0).toUpperCase() + text.slice(1)
}
