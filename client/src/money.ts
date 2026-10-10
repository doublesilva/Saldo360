/** Mesmo limite da API: até 13 dígitos inteiros e 2 decimais (15 dígitos, exatos em um number). */
export const AMOUNT_MAX_INTEGER_DIGITS = 13
export const AMOUNT_MAX_DECIMALS = 2

export type ParsedAmount =
  | { kind: 'empty' }
  | {
      kind: 'ok'
      value: number
      /** Decimal normalizado e exato ("-1500.75"), para enviar à API sem passar por ponto flutuante. */
      text: string
    }
  | { kind: 'invalid'; message: string }

const THOUSANDS_WITH_COMMA = /^\d{1,3}(\.\d{3})+(,\d+)?$/ // 1.234 ou 1.234,56
const COMMA_DECIMAL = /^\d+(,\d+)?$/ // 1234 ou 1234,56
const DOT_DECIMAL = /^\d+\.\d+$/ // 1234.56 (fora do padrão de milhar)

/**
 * Converte o valor digitado (pt-BR: "1.234,56", "-250,00"; ou "1234.56") sem arredondar: mais de duas casas
 * decimais ou mais de 13 dígitos inteiros é erro, nunca um valor ajustado. Aceita sinal negativo.
 */
export function parseAmount(input: string, subject = 'O valor'): ParsedAmount {
  const compact = input.replace(/\s|R\$/g, '').replace(/−/g, '-')
  if (compact === '') return { kind: 'empty' }

  const negative = compact.startsWith('-')
  const unsigned = negative ? compact.slice(1) : compact

  let normalized: string
  if (THOUSANDS_WITH_COMMA.test(unsigned)) normalized = unsigned.replace(/\./g, '').replace(',', '.')
  else if (COMMA_DECIMAL.test(unsigned)) normalized = unsigned.replace(',', '.')
  else if (DOT_DECIMAL.test(unsigned)) normalized = unsigned
  else return { kind: 'invalid', message: 'Valor inválido. Use o formato 1.234,56 (negativo: -1.234,56).' }

  const [integer, decimals = ''] = normalized.split('.')
  if (decimals.length > AMOUNT_MAX_DECIMALS) {
    return { kind: 'invalid', message: `${subject} deve ter no máximo duas casas decimais.` }
  }
  if (integer.replace(/^0+/, '').length > AMOUNT_MAX_INTEGER_DIGITS) {
    return { kind: 'invalid', message: `${subject} deve ter no máximo 13 dígitos antes da vírgula.` }
  }

  const value = Number(normalized)
  const isZero = /^0*(\.0*)?$/.test(normalized)
  const text = negative && !isZero ? `-${normalized}` : normalized
  return { kind: 'ok', value: negative && value !== 0 ? -value : value, text }
}

/** Valor para o campo de edição: "-1500,75", sem separador de milhar. */
export function formatAmountInput(value: number): string {
  return value.toFixed(AMOUNT_MAX_DECIMALS).replace('.', ',')
}

const brl = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' })

export function formatBrl(value: number): string {
  return brl.format(value)
}

const DECIMAL_TEXT = /^(-?)(\d+)\.(\d{2})$/

/**
 * Formata em BRL um valor que a API envia como texto decimal exato ("-1300.00"), só com manipulação de texto:
 * nunca converte para number, então somas grandes como "29999999999999.97" não perdem centavos.
 * Mesmo formato do Intl pt-BR ("-R$ 1.300,00").
 */
export function formatBrlDecimal(text: string): string {
  const match = DECIMAL_TEXT.exec(text)
  if (!match) throw new Error(`Valor monetário inválido: ${text}`)
  const [, sign, integer, cents] = match
  const grouped = integer.replace(/^0+(?=\d)/, '').replace(/\B(?=(\d{3})+(?!\d))/g, '.')
  const negative = sign === '-' && /[1-9]/.test(integer + cents)
  return `${negative ? '-' : ''}R$ ${grouped},${cents}`
}

/** Se o texto decimal da API é negativo (diferente de zero). */
export function isNegativeDecimal(text: string): boolean {
  return text.startsWith('-') && /[1-9]/.test(text)
}

/** "2026-10-01" → "01/10/2026", sem passar por Date (evita deslocamento de fuso). */
export function formatDate(isoDate: string): string {
  const [year, month, day] = isoDate.split('-')
  return `${day}/${month}/${year}`
}
