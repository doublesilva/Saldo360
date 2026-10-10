import { describe, expect, it } from 'vitest'
import { todayInSaoPaulo } from './dates'

describe('todayInSaoPaulo', () => {
  it.each([
    ['2026-10-11T01:00:00Z', '2026-10-10'], // 22:00 de 10/10 em Brasília
    ['2026-10-11T03:00:00Z', '2026-10-11'], // meia-noite em Brasília
    ['2026-01-01T02:59:59Z', '2025-12-31'],
  ])('%s → %s', (instant, expected) => {
    expect(todayInSaoPaulo(new Date(instant))).toBe(expected)
  })
})
