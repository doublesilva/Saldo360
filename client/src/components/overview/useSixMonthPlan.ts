import { useCallback } from 'react'
import { getSixMonthPlan, type SixMonthPlan } from '../../api/monthlyPlan'
import type { PlanActualScopeChoice } from '../../api/planActual'
import { addCalendarMonths } from '../../dates'
import { useLoad, type Load } from './useLoad'

/** Meses mostrados antes da competência: a janela da Evolução (três antes, ela e os dois seguintes) também contém a competência, que o guia de configuração consulta. */
export const EVOLUTION_MONTHS_BEFORE = 3

export interface SixMonthLoad { load: Load<SixMonthPlan>; retry: () => void }

/**
 * Uma única leitura de `monthly-plan/six-months` por recorte (workspace, mês inicial, escopo), compartilhada pela Evolução financeira e pelo guia "Configure seu orçamento".
 * A chave inclui o recorte, então uma resposta de outra conta ou competência nunca é exibida; a nova tentativa refaz a mesma consulta para os dois blocos.
 */
export function useSixMonthPlan(workspaceId: string, competence: string, scope: PlanActualScopeChoice, refreshKey: number): SixMonthLoad {
  const from = addCalendarMonths(`${competence}-01`, -EVOLUTION_MONTHS_BEFORE).slice(0, 7)
  const { load, retry } = useLoad(`${workspaceId}|${from}|${scope.kind}:${scope.accountId ?? ''}|${refreshKey}`, useCallback((signal: AbortSignal) => getSixMonthPlan(workspaceId, from, scope, signal), [workspaceId, from, scope]))
  return { load, retry }
}
