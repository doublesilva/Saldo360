import type { BudgetFactState, BudgetGroupingType, FinancialBudgetAlertStatus } from '../../api/budgets'

// Textos do orçamento (R03). Valores monetários continuam sendo texto decimal exato: aqui só se formata, nunca se calcula.

export const groupTitles: Record<BudgetGroupingType, string> = {
  FixedExpenses: 'Despesas fixas',
  AllCards: 'Todos os cartões',
  Category: 'Categorias',
}

export const groupHints: Record<BudgetGroupingType, string> = {
  FixedExpenses: 'Toda despesa classificada como fixa, de qualquer categoria e meio de pagamento.',
  AllCards: 'Compras e parcelas de todos os cartões somados, pela competência (mês de vencimento) das faturas. Não há teto por cartão.',
  Category: 'Cada meta cobre a categoria escolhida; numa categoria principal, inclui as subcategorias.',
}

/** O texto sempre diz o estado: ícone e cor só reforçam. */
export const statusLabels: Record<FinancialBudgetAlertStatus, string> = {
  WithinBudget: 'Dentro do limite',
  ApproachingLimit: 'Perto do limite',
  LimitReached: 'Limite atingido',
  Exceeded: 'Limite excedido',
}

export const statusIcons: Record<FinancialBudgetAlertStatus, string> = { WithinBudget: '✓', ApproachingLimit: '!', LimitReached: '●', Exceeded: '×' }

export const factStateLabels: Record<BudgetFactState, string> = {
  Realized: 'Realizada',
  CardLaunched: 'Lançada no cartão',
  Pending: 'Prevista',
}

/** Grupo da Visão Geral → tipo de meta. */
export type BudgetGroup = 'fixed' | 'cards' | 'categories'
export const groupingOfGroup: Record<BudgetGroup, BudgetGroupingType> = { fixed: 'FixedExpenses', cards: 'AllCards', categories: 'Category' }
