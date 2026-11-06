import type { BudgetAction, BudgetPressure } from '../../api/overview'

// T11.2: textos do consumo do orçamento. Nenhum valor é calculado aqui: o servidor entrega valores, estados e ações; este módulo só os descreve.

export const groupTitle = { Cards: 'Cartões de crédito', OutsideCards: 'Fora dos cartões' } as const

export function pressureText(pressure: BudgetPressure): string {
  switch (pressure.kind) {
    case 'CardsOverTarget': return 'Cartões acima da meta mensal'
    case 'OutsideCardsOverTarget': return 'Despesas fora dos cartões acima da meta'
    case 'FixedExpensesOverTarget': return 'Despesas fixas acima da meta'
    case 'CategoryOverTarget': return `Categoria ${pressure.subject ?? ''} acima do orçamento`.replace('  ', ' ')
    case 'OverdueExpenses': return 'Despesas vencidas ainda não liquidadas'
    case 'CardFinanceCharges': return 'Encargos financeiros dos cartões já efetivados'
    case 'IncomeNotReceived': return 'Receitas previstas e não recebidas'
    case 'OverdraftChargesNotEstimable': return 'Encargos do cheque especial ainda não podem ser estimados'
    default: return pressure.kind
  }
}

export const actionText: Record<BudgetAction, string> = {
  OpenCards: 'Consultar faturas', OpenBudget: 'Abrir orçamento', OpenAgenda: 'Abrir Agenda', OpenPlanning: 'Abrir Projeção financeira', OpenReview: 'Classificar lançamentos', OpenAccounts: 'Ver contas',
}

export const recommendationText: Record<string, string> = {
  CardsOverTarget: 'Os cartões ultrapassaram a meta mensal. Consulte as faturas.',
  OutsideCardsOverTarget: 'As despesas fora dos cartões ultrapassaram a meta. Revise o orçamento.',
  UncategorizedExpenses: 'Há despesas sem categoria que dificultam a análise do orçamento.',
  ProjectedOverTotal: 'O total previsto está acima do orçamento configurado.',
  IncomeEstimateUnconfirmed: 'A receita PJ depende de uma estimativa ainda não confirmada.',
  BudgetTotalNotDefined: 'O orçamento total não foi definido; configure-o para comparar os cenários.',
  CardTargetNotDefined: 'A meta dos cartões não foi definida; configure-a para dividir o orçamento.',
  OverdueExpenses: 'Há despesas vencidas ainda não liquidadas. Confira a Agenda.',
  CardCoverageIncomplete: 'Há faturas com cobertura incompleta: os valores dos cartões são preliminares.',
}

/** T12.2: origem da meta total vinda do resolvedor único da T12.1. `from` é a competência (AAAA-MM) em que o padrão começou a valer. */
export function originText(origin: string | null | undefined, from: string | null | undefined, monthName: (competence: string) => string): string | null {
  switch (origin) {
    case 'Inherited': return from ? `Meta herdada do padrão, em vigor desde ${monthName(from)}` : 'Meta herdada do padrão'
    case 'Customized': return 'Meta personalizada neste mês'
    case 'Historical': return 'Meta do registro mensal anterior'
    case 'Undefined': return 'Meta não configurada'
    default: return null
  }
}
