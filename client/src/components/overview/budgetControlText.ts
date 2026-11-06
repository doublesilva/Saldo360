import type { BudgetAction, BudgetGroupState, BudgetPressure } from '../../api/overview'

// T11.2: textos do consumo do orçamento. Nenhum valor é calculado aqui: o servidor entrega valores, estados e ações; este módulo só os descreve.

export const groupTitle = { Cards: 'Cartões de crédito', OutsideCards: 'Fora dos cartões' } as const

export const groupStateText: Partial<Record<BudgetGroupState, string>> = {
  TargetNotDefined: 'Meta não configurada', TargetNotAttributable: 'A meta é global e não se aplica a uma conta isolada', NoCards: 'Nenhum cartão cadastrado', Partial: 'Dados incompletos',
}

export const statusText = { WithinBudget: 'Dentro da meta', Exceeded: 'Acima da meta', NoTarget: 'Meta não configurada' } as const

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

export const referenceText: Record<string, string> = {
  CardsTarget: 'meta dos cartões', OutsideCardsTarget: 'meta fora dos cartões', FixedTarget: 'meta de despesas fixas', CategoryTarget: 'meta da categoria',
}
export const impactText: Record<BudgetPressure['impact'], string> = { Budget: 'Impacto no orçamento', Cash: 'Impacto no caixa', BudgetAndCash: 'Orçamento e caixa', NotEstimable: 'Impacto não estimável' }
export const qualityText: Record<BudgetPressure['quality'], string> = { Confirmed: 'Confirmado', Projected: 'Projetado', Estimated: 'Inclui estimativa', Preliminary: 'Preliminar', NotEstimable: 'Não estimável' }

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

export const noteText: Record<string, string> = {
  TargetsGlobalOnly: 'As metas do orçamento são globais. Selecione "Todas as contas" para compará-las.',
  TotalBelowCardTarget: 'O orçamento total está abaixo da meta dos cartões: a meta fora dos cartões foi considerada zero.',
}

export const reasonText: Record<string, string> = {
  CardCoverageIncomplete: 'cobertura de cartões incompleta', MissingPayerAccount: 'cartão sem conta pagadora', DebtCoverageIncomplete: 'dívidas sem conta responsável',
  PendingBankAmbiguous: 'débito do banco sem vínculo seguro', MovementsInReview: 'movimentos em revisão', PartialPeriod: 'período parcial',
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
