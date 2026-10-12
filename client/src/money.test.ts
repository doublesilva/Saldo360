import { describe, expect, it } from 'vitest'
import {
  decimalToInput,
  formatAmountInput,
  formatBrl,
  formatBrlDecimal,
  formatDate,
  isNegativeDecimal,
  parseAmount,
} from './money'

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

describe('formatBrlDecimal (texto exato da API, sem number)', () => {
  it.each([
    ['1300.00', 'R$ 1.300,00'],
    ['0.30', 'R$ 0,30'],
    ['0.00', 'R$ 0,00'],
    ['-0.00', 'R$ 0,00'],
    ['-400.00', '-R$ 400,00'],
    ['-0.01', '-R$ 0,01'],
    ['29999999999999.97', 'R$ 29.999.999.999.999,97'],
    ['1234567890123456789.99', 'R$ 1.234.567.890.123.456.789,99'], // além da precisão de um number
  ])('"%s" → "%s"', (text, expected) => {
    expect(formatBrlDecimal(text)).toBe(expected)
  })

  it('segue o formato do Intl pt-BR', () => {
    expect(formatBrlDecimal('-1234.56')).toBe(formatBrl(-1234.56))
  })

  it.each(['1300', '1300.5', '1,300.00', 'abc'])('recusa "%s" (não é texto decimal com 2 casas)', (text) => {
    expect(() => formatBrlDecimal(text)).toThrow()
  })

  it('identifica negativos sem converter para number', () => {
    expect([isNegativeDecimal('-0.01'), isNegativeDecimal('-0.00'), isNegativeDecimal('5.00')]).toEqual([true, false, false])
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

describe('decimalToInput', () => {
  it.each([
    ['1234.56', '1234,56'],
    ['0.10', '0,10'],
    ['9999999999999.99', '9999999999999,99'],
  ])('"%s" → "%s", e parseAmount devolve o mesmo texto', (text, input) => {
    expect(decimalToInput(text)).toBe(input)
    expect(parseAmount(decimalToInput(text))).toMatchObject({ kind: 'ok', text })
  })

  it.each(['1234.5', '1234', 'abc'])('recusa "%s"', (text) => {
    expect(() => decimalToInput(text)).toThrow()
  })
})
