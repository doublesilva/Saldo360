import { describe, expect, it } from 'vitest'
import { accountFromParams, accountToParam, parseRoute, planningTabFromParams, routeUrl } from './routing'

const ACCOUNT = '3f2b8c1e-5a47-4d0e-9b61-0c7d2a9e4f10'
const at = (pathname: string, search = '') => parseRoute({ pathname, search })

describe('rotas do shell autenticado', () => {
  it('a seção vem do caminho e a raiz é a Visão Geral', () => {
    expect(at('/').section).toBe('overview')
    expect(at('/agenda').section).toBe('calendar')
    expect(at('/planejamento/').section).toBe('planning')
    expect(at('/agenda').problems).toEqual([])
  })

  it('caminho desconhecido cai na Visão Geral e é reportado', () => {
    const route = at('/nao-existe')
    expect(route.section).toBe('overview')
    expect(route.problems).toEqual(['endereço'])
  })

  it('mantém só parâmetros válidos e reporta os demais', () => {
    const route = at('/agenda', '?competencia=2026-13&conta=nome-da-conta&tipo=Transfer&aba=x&recuperacao=2&extra=1')
    expect([...route.params.keys()]).toEqual([])
    expect(route.problems).toEqual(['competencia', 'conta', 'tipo', 'aba', 'recuperacao'])
    const valid = at('/agenda', `?competencia=2026-11&conta=${ACCOUNT.toUpperCase()}&tipo=Expense`)
    expect(valid.problems).toEqual([])
    expect(valid.params.get('conta')).toBe(ACCOUNT)
    expect(routeUrl('calendar', valid.params)).toBe(`/agenda?competencia=2026-11&conta=${ACCOUNT}&tipo=Expense`)
  })

  it('conta: ausente é o padrão, "todas" é o consolidado e só GUID vira conta específica', () => {
    expect(accountFromParams(at('/agenda').params)).toEqual({ kind: 'default' })
    expect(accountFromParams(at('/agenda', '?conta=todas').params)).toEqual({ kind: 'all' })
    expect(accountFromParams(at('/agenda', `?conta=${ACCOUNT}`).params)).toEqual({ kind: 'account', accountId: ACCOUNT })
    expect(accountToParam({ kind: 'default' })).toBeNull()
    expect(accountToParam({ kind: 'all' })).toBe('todas')
  })

  it('aba do planejamento usa nomes legíveis', () => {
    expect(planningTabFromParams(at('/planejamento', '?aba=seis-meses').params)).toBe('six')
    expect(planningTabFromParams(at('/planejamento').params)).toBeUndefined()
  })

  it('a URL nunca carrega nome, saldo ou valor: só os parâmetros conhecidos', () => {
    const route = at('/agenda', '?competencia=2026-11&saldo=1234&nome=Itau')
    expect([...route.params.keys()]).toEqual(['competencia'])
  })
})
