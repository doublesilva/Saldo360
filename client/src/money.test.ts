import { describe, expect, it } from 'vitest'
import { formatAmountInput, formatBrl, formatDate, parseAmount } from './money'

describe('parseAmount', () => {
  it.each([
    ['1.234,56', 1234.56],
    ['1234,56', 1234.56],
    ['1234.56', 1234.56],
    ['R$ 1.234,56', 1234.56],
    ['-1.500,75', -1500.75],
    ['−2,5', -2.5],
    ['0', 0],
    ['-0,00', 0],
    ['0,1', 0.1],
    ['1.500', 1500], // ponto como separador de milhar em pt-BR
    ['9.999.999.999.999,99', 9999999999999.99],
  ])('"%s" → %s', (text, value) => {
    expect(parseAmount(text)).toMatchObject({ kind: 'ok', value })
  })

  it.each([
    ['1.234,56', '1234.56'],
    ['-1.500,75', '-1500.75'],
    ['0,10', '0.10'],
    ['-0,00', '0.00'],
    ['-05', '-05'],
    ['9.999.999.999.999,99', '9999999999999.99'],
  ])('"%s" → texto exato "%s" (sem ponto flutuante)', (input, text) => {
    expect(parseAmount(input)).toMatchObject({ kind: 'ok', text })
  })

  it('usa o sujeito informado nas mensagens', () => {
    expect(parseAmount('1,234', 'O saldo inicial')).toEqual({
      kind: 'invalid',
      message: 'O saldo inicial deve ter no máximo duas casas decimais.',
    })
    expect(parseAmount('1,234')).toMatchObject({ message: 'O valor deve ter no máximo duas casas decimais.' })
  })

  it('vazio não é zero', () => {
    expect(parseAmount('   ')).toEqual({ kind: 'empty' })
  })

  it.each(['10,005', '0,001', '1234.567', '1,5000'])('recusa "%s" (mais de duas casas), sem arredondar', (text) => {
    expect(parseAmount(text)).toMatchObject({ kind: 'invalid', message: expect.stringContaining('duas casas') })
  })

  it.each(['10.000.000.000.000', '12345678901234'])('recusa "%s" (mais de 13 dígitos inteiros)', (text) => {
    expect(parseAmount(text)).toMatchObject({ kind: 'invalid', message: expect.stringContaining('13 dígitos') })
  })

  it.each(['abc', '1,2,3', '--1', '1.23.4', '1e5', ','])('recusa "%s"', (text) => {
    expect(parseAmount(text).kind).toBe('invalid')
  })
})

describe('formatação', () => {
  it('formata para o campo de edição sem milhar', () => {
    expect(formatAmountInput(-1500.75)).toBe('-1500,75')
    expect(formatAmountInput(0)).toBe('0,00')
  })

  it('formata em BRL e a data sem fuso', () => {
    expect(formatBrl(1234.56)).toMatch(/^R\$\s1\.234,56$/)
    expect(formatDate('2026-10-01')).toBe('01/10/2026')
  })
})
