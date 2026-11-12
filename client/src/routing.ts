// Rotas do shell autenticado, sem biblioteca: a seção vai no caminho e os filtros essenciais em parâmetros de consulta.
// Nada financeiro (nome, saldo, valor) vai na URL: só seção, competência (AAAA-MM), identificador de conta já aceito pela aplicação e tipo de evento.
// A API continua responsável por autorizar e resolver o escopo; aqui só se valida o formato e se descarta o que não for reconhecido.

export const sectionPaths = {
  overview: '/', transactions: '/lancamentos', accounts: '/contas', cards: '/cartoes', budgets: '/orcamento', calendar: '/agenda', goals: '/metas', planning: '/planejamento',
  categories: '/categorias', recurrences: '/recorrencias', planned: '/previsoes', transfers: '/transferencias', debts: '/dividas', settings: '/configuracoes', automation: '/automacao',
} as const

export type RouteSection = keyof typeof sectionPaths
const sectionByPath = new Map<string, RouteSection>((Object.entries(sectionPaths) as [RouteSection, string][]).map(([section, path]) => [path, section]))

export const planningTabParams = { monthly: 'plano-mensal', projection: 'projecao', six: 'seis-meses', calendar: 'dias-uteis', scenarios: 'cenarios' } as const
export type PlanningTabKey = keyof typeof planningTabParams

const COMPETENCE = /^\d{4}-(0[1-9]|1[0-2])$/
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
export const isCompetence = (value: string | null | undefined): value is string => value != null && COMPETENCE.test(value)
export const isAccountId = (value: string | null | undefined): value is string => value != null && GUID.test(value)

/** Escolha de conta na URL: ausente = padrão do módulo (conta principal); "todas" = consolidado; GUID = conta específica. */
export type AccountParam = { kind: 'default' } | { kind: 'all' } | { kind: 'account'; accountId: string }

export function accountToParam(choice: AccountParam): string | null {
  return choice.kind === 'all' ? 'todas' : choice.kind === 'account' ? choice.accountId.toLowerCase() : null
}

export interface AppRoute {
  section: RouteSection
  /** Somente parâmetros válidos (os inválidos são descartados e listados em `problems`). */
  params: URLSearchParams
  /** Nomes dos parâmetros ou do caminho que não eram válidos e foram ajustados para o padrão. */
  problems: string[]
}

export function parseRoute(location: { pathname: string; search: string }): AppRoute {
  const path = location.pathname.length > 1 ? location.pathname.replace(/\/+$/, '') : location.pathname
  const section = sectionByPath.get(path)
  const problems: string[] = section ? [] : ['endereço']
  const raw = new URLSearchParams(location.search)
  const params = new URLSearchParams()
  const take = (name: string, valid: (value: string) => boolean) => {
    const value = raw.get(name)
    if (value === null) return
    if (valid(value)) params.set(name, name === 'conta' ? value.toLowerCase() : value)
    else problems.push(name)
  }
  take('competencia', isCompetence)
  take('conta', value => value === 'todas' || isAccountId(value))
  take('tipo', value => value === 'Income' || value === 'Expense')
  take('aba', value => Object.values(planningTabParams).includes(value as never))
  take('recuperacao', value => value === '1')
  return { section: section ?? 'overview', params, problems }
}

export function routeUrl(section: RouteSection, params?: URLSearchParams): string {
  const query = params?.toString()
  return `${sectionPaths[section]}${query ? `?${query}` : ''}`
}

export function accountFromParams(params: URLSearchParams): AccountParam {
  const value = params.get('conta')
  return value === 'todas' ? { kind: 'all' } : isAccountId(value) ? { kind: 'account', accountId: value } : { kind: 'default' }
}

export function planningTabFromParams(params: URLSearchParams): PlanningTabKey | undefined {
  const value = params.get('aba')
  return (Object.entries(planningTabParams).find(([, param]) => param === value)?.[0]) as PlanningTabKey | undefined
}

export const problemNotice = 'Parte do link não era válida e foi ajustada para o padrão. Os dados abaixo correspondem aos filtros exibidos.'
export const unavailableAccountNotice = 'A conta do link não está disponível. Os dados abaixo correspondem ao padrão exibido.'
