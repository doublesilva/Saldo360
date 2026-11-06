import type { EffectiveTarget } from '../../api/effectiveBudget'

// T12.1: textos do orçamento padrão. Nenhum valor é calculado aqui: o servidor resolve a herança e entrega a origem de cada meta.

/** Mês "2026-11" → "11/2026". */
export const monthShort = (competence: string) => `${competence.slice(5, 7)}/${competence.slice(0, 4)}`

export function originText(target: EffectiveTarget): string {
  if (target.origin === 'Undefined') return 'Sem meta configurada'
  if (target.origin === 'Customized') return target.removed ? 'Suspensa neste mês' : 'Personalizado neste mês'
  if (target.origin === 'Historical') return target.removed ? 'Retirada neste mês' : 'Registro mensal anterior'
  return target.removed ? `Retirada do padrão desde ${monthShort(target.standardFrom ?? '')}` : `Herdado do padrão desde ${monthShort(target.standardFrom ?? '')}`
}

export const targetLabel: Record<string, string> = {
  TotalExpenses: 'Total de despesas PF', AllCards: 'Todos os cartões', FixedExpenses: 'Despesas fixas', OutsideCards: 'Fora dos cartões',
}

export const targetHint: Record<string, string> = {
  TotalExpenses: 'Orçamento total do mês. Os cartões e o que fica fora deles cabem dentro dele.',
  AllCards: 'Meta de todos os cartões somados, incluindo a provisão alimentar.',
  FixedExpenses: 'Visão complementar das despesas fixas: não é o mesmo que "fora dos cartões" e não soma ao total.',
  OutsideCards: 'Calculado: total menos a meta dos cartões.',
}

export const warningText: Record<string, string> = {
  BudgetNotConfigured: 'Nenhum orçamento padrão configurado. Defina as metas uma vez e os próximos meses as herdam.',
  TotalExpensesRequiredForCeilings: 'Defina o total de despesas para o cenário Tetos operar plenamente.',
  TotalBelowCards: 'O total de despesas está menor que a meta dos cartões.',
  OverlappingCategories: 'Há meta de uma categoria e de uma subcategoria dela no mesmo mês: o gasto seria contado duas vezes.',
}

export const referenceWarning: Record<string, string> = {
  FixedExpensesNotOutsideCards: 'A meta de despesas fixas não equivale à meta fora dos cartões.',
  CategoriesMayBeInsideCards: 'Metas de categorias podem estar contidas na meta dos cartões e não se somam ao total.',
  TotalExpensesRequiredForCeilings: 'O total de despesas precisa estar definido para o cenário Tetos operar plenamente.',
}

export const actionText: Record<string, string> = { Set: 'Definida', Remove: 'Retirada', Restore: 'Restaurada ao padrão' }
export const scopeText: Record<string, string> = { FromCompetence: 'Este mês e os próximos', OnlyCompetence: 'Somente este mês' }
