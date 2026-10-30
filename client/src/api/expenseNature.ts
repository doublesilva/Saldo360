/**
 * Tipo de despesa: fixa (compromisso habitual, mesmo que o valor mude), variável ou "Não definida". Só existe em despesa. "Não definida" é um
 * valor explícito: nada é inferido de cartão, parcelas ou categoria, e o sistema nunca assume "variável" em silêncio.
 */
export type ExpenseNature = 'Fixed' | 'Variable' | 'Undefined'

export const expenseNatureLabels: Record<ExpenseNature, string> = {
  Fixed: 'Fixa',
  Variable: 'Variável',
  Undefined: 'Não definida',
}

export const expenseNatures: ExpenseNature[] = ['Undefined', 'Fixed', 'Variable']

export const EXPENSE_NATURE_HINT = 'Fixa é um compromisso habitual, mesmo que o valor mude.'
