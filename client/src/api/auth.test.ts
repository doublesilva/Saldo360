import { afterEach, describe, expect, it } from 'vitest'
import { login } from './auth'

afterEach(() => window.history.replaceState(null, '', '/'))

describe('login', () => {
  it('devolve a pessoa ao destino interno reconhecido, com os filtros válidos e sem resíduos da URL', () => {
    window.history.replaceState(null, '', '/agenda?competencia=2026-09&tipo=Expense&login=failed&ref=abc')
    const navigate: string[] = []
    login(url => navigate.push(url))
    expect(navigate).toEqual([`/auth/login?returnUrl=${encodeURIComponent('/agenda?competencia=2026-09&tipo=Expense')}`])
  })

  it('endereço desconhecido volta para a Visão Geral', () => {
    window.history.replaceState(null, '', '/qualquer-coisa?x=1')
    const navigate: string[] = []
    login(url => navigate.push(url))
    expect(navigate).toEqual([`/auth/login?returnUrl=${encodeURIComponent('/')}`])
  })
})
