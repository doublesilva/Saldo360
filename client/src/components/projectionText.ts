import type { ProjectionMonth, ProjectionScenario, ProjectionScenarioName } from '../api/projection'
import { qualityText } from './monthlyPlanText'

/** Textos da projeção financeira (Entrega 3). Só apresentação: nenhum número é calculado aqui. */
export const scenarioLabels: Record<ProjectionScenarioName, string> = { Current: 'Atual', Ceilings: 'Tetos' }
export const scenarioHints: Record<ProjectionScenarioName, string> = {
  Current: 'O que o sistema já conhece: previsto, recorrências, parcelas, faturas, receita PJ e repasse.',
  Ceilings: 'Parte do Atual e completa cada competência até os tetos de cartões e de fora dos cartões; o que já excede o teto é preservado.',
}
export type ScenarioMode = 'both' | ProjectionScenarioName
export const modeLabels: Record<ScenarioMode, string> = { both: 'Comparar Atual e Tetos', Current: 'Somente Atual', Ceilings: 'Somente Tetos' }

/** Gravidade do aviso: `limit` não invalida a projeção (é um limite conhecido); `partial` a torna parcial e nunca garantida. */
export interface QualityMessage { text: string; severity: 'partial' | 'info' }
export const projectionQualityText: Record<string, QualityMessage> = {
  FoodCategoriesInferred: { severity: 'info', text: 'As categorias de alimentação foram inferidas pelo nome ("aliment…") e ainda não foram gravadas: selecione-as na configuração da provisão de alimentação.' },
  FoodProvisionCategoriesNotConfigured: { severity: 'partial', text: 'A provisão de alimentação não foi aplicada: nenhuma categoria alimentar está configurada.' },
  FoodProvisionDefaultAmount: { severity: 'info', text: 'A provisão de alimentação usa o valor de referência, ainda não gravado como configuração do orçamento.' },
  FoodKnownFromCategorizedPurchasesOnly: { severity: 'partial', text: 'A alimentação já reconhecida considera só compras de cartão com categoria alimentar: compras pendentes sem categoria não contam, e a estimativa pode ficar acima do necessário.' },
  FoodProvisionEstimated: { severity: 'info', text: 'Inclui provisão de alimentação estimada no cartão (apenas a diferença ainda não reconhecida na fatura).' },
  OverdraftChargesNotEstimated: { severity: 'partial', text: 'Há uso projetado do cheque especial: juros e IOF futuros não são estimados e não estão no saldo. Não são zero, estão desconhecidos.' },
  CeilingTargetsNotDefined: { severity: 'partial', text: 'Os tetos (metas de cartões e total do orçamento) não estão definidos: o cenário Tetos não complementa nada neste mês.' },
  CardsCeilingExceeded: { severity: 'info', text: 'O consumo de cartões já excede o teto: o excesso é preservado, não reduzido.' },
  OutsideCardsCeilingExceeded: { severity: 'info', text: 'As despesas fora dos cartões já excedem o teto: o excesso é preservado, não reduzido.' },
  IncomeReceiptAmbiguous: { severity: 'partial', text: 'Mais de uma competência disputa o mesmo recebimento: a associação é ambígua e a projeção é parcial.' },
  IncomeReceiptOverdue: { severity: 'partial', text: 'Há valor a receber com data vencida: segue identificado, mas não entra no caixa de hoje.' },
}

export function qualityMessage(code: string): QualityMessage {
  return projectionQualityText[code] ?? { severity: 'partial', text: qualityText[code] ?? `Aviso da projeção: ${code}.` }
}

export interface QualityGroup { code: string; message: QualityMessage; competences: string[]; scenarios: ProjectionScenarioName[] }

/** Agrupa avisos repetidos por código, preservando as competências e os cenários em que ocorrem (ordem cronológica). */
export function groupQuality(scenarios: readonly ProjectionScenario[]): QualityGroup[] {
  const groups = new Map<string, { competences: Set<string>; scenarios: Set<ProjectionScenarioName> }>()
  for (const scenario of scenarios) for (const month of scenario.months) for (const code of month.quality) {
    const group = groups.get(code) ?? { competences: new Set<string>(), scenarios: new Set<ProjectionScenarioName>() }
    group.competences.add(month.competence); group.scenarios.add(scenario.name); groups.set(code, group)
  }
  return [...groups].map(([code, group]) => ({ code, message: qualityMessage(code), competences: [...group.competences].sort(), scenarios: [...group.scenarios] }))
    .sort((a, b) => Number(a.message.severity !== 'partial') - Number(b.message.severity !== 'partial') || a.code.localeCompare(b.code))
}

/** Notas gerais da projeção (não ligadas a uma competência), sem repetir códigos que já aparecem agrupados por competência. */
export function generalNotes(notes: readonly string[], groups: readonly QualityGroup[]): QualityGroup[] {
  const covered = new Set(groups.map(group => group.code))
  return notes.filter(code => !covered.has(code)).map(code => ({ code, message: qualityMessage(code), competences: [], scenarios: [] }))
}

export const monthOf = (scenario: ProjectionScenario | undefined, competence: string): ProjectionMonth | undefined => scenario?.months.find(month => month.competence === competence)
