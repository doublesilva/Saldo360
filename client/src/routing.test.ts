import { describe, expect, it } from 'vitest'
import { accountFromParams, accountToParam, loginReturnUrl, parseRoute, planningTabFromParams, routeUrl } from './routing'

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
    expect(route.problems).toEqual(['aba', 'competencia', 'conta', 'tipo', 'recuperacao'])
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

  it('o retorno do login preserva só rota interna reconhecida e parâmetros válidos', () => {
    expect(loginReturnUrl({ pathname: '/agenda', search: '?competencia=2026-09&tipo=Expense&login=failed&ref=x' })).toBe('/agenda?competencia=2026-09&tipo=Expense')
    expect(loginReturnUrl({ pathname: '/planejamento', search: `?aba=seis-meses&competencia=2026-11&conta=${ACCOUNT}&recuperacao=1` })).toBe(`/planejamento?aba=seis-meses&competencia=2026-11&conta=${ACCOUNT}&recuperacao=1`)
    expect(loginReturnUrl({ pathname: '/agenda', search: '?competencia=2026-13' })).toBe('/agenda')
  })

  it('destino inválido ou externo volta para a Visão Geral', () => {
    expect(loginReturnUrl({ pathname: '/nao-existe', search: '?competencia=2026-09' })).toBe('/')
    expect(loginReturnUrl({ pathname: '//evil.invalid/agenda', search: '' })).toBe('/')
    expect(loginReturnUrl({ pathname: '/https://evil.invalid', search: '' })).toBe('/')
  })

  it('caminhos liberados (convite) mantêm o endereço original', () => {
    expect(loginReturnUrl({ pathname: '/convite', search: '?x=1' }, ['/convite'])).toBe('/convite?x=1')
  })
})
