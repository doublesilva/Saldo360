import { describe, expect, it } from 'vitest'
import { addCalendarMonths, endOfMonth, formatMonthYear, startOfMonth, todayInSaoPaulo } from './dates'

describe('todayInSaoPaulo', () => {
  it.each([
    ['2026-10-11T01:00:00Z', '2026-10-10'], // 22:00 de 10/10 em Brasília
    ['2026-10-11T03:00:00Z', '2026-10-11'], // meia-noite em Brasília
    ['2026-01-01T02:59:59Z', '2025-12-31'],
  ])('%s → %s', (instant, expected) => {
    expect(todayInSaoPaulo(new Date(instant))).toBe(expected)
  })
})

describe('endOfMonth', () => {
  it.each([
    ['2026-10-12', '2026-10-31'],
    ['2026-02-10', '2026-02-28'],
    ['2028-02-01', '2028-02-29'],
    ['2026-12-31', '2026-12-31'],
  ])('%s → %s', (date, expected) => {
    expect(endOfMonth(date)).toBe(expected)
  })
})

describe('utilitários mensais civis', () => {
  it('obtém início, navega entre anos e formata sem depender do fuso do navegador', () => {
    expect(startOfMonth('2026-12-31')).toBe('2026-12-01')
    expect(addCalendarMonths('2026-12-15', 1)).toBe('2027-01-01')
    expect(addCalendarMonths('2026-01-15', -1)).toBe('2025-12-01')
    expect(formatMonthYear('2027-01-01')).toBe('Janeiro de 2027')
  })
})
