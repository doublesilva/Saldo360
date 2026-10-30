// @vitest-environment node
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/** Contrato visual do planejamento mensal (T10.2): lê os arquivos-fonte, pois o jsdom não calcula layout. */
const read = (relative: string) => readFileSync(join(process.cwd(), 'src', relative), 'utf8')
const css = read('MonthlyPlan.css')

describe('Responsividade e contrato visual do planejamento mensal', () => {
  it('usa layout fluido, sem larguras fixas, com rolagem apenas dentro das tabelas', () => {
    expect(css).toContain('minmax(min(100%')
    expect(css).toMatch(/\.monthly-plan__table-wrap\s*\{[^}]*overflow-x:\s*auto/)
    expect(css).toMatch(/\.monthly-plan\s*\{[^}]*min-width:\s*0/)
    // Larguras fixas são proibidas; a única exceção é o 1 px do utilitário de texto só para leitores de tela.
    expect(css).not.toMatch(/[^-]width:\s*(?!1px)\d+(px|rem)\s*;/)
    expect(css).not.toMatch(/overflow-x:\s*scroll/)
  })

  it('transforma as linhas em cartões em telas estreitas, rotulando cada célula', () => {
    expect(css).toMatch(/@media \(max-width: 40rem\)/)
    expect(css).toContain('content: attr(data-label)')
    const view = read('components/MonthlyPlanView.tsx')
    for (const label of ['Categoria', 'Limite', 'Realizado', 'Descrição', 'Vigente', 'Ações']) expect(view).toContain(`data-label="${label}"`)
  })

  it('mantém a tabela de seis meses legível: primeira coluna fixa e células com largura mínima', () => {
    expect(css).toMatch(/\.monthly-plan__table--months th\[scope="row"\]\s*\{[^}]*position:\s*sticky/)
    expect(css).toMatch(/\.monthly-plan__table--months td\s*\{[^}]*min-width/)
  })

  it('não implementa cálculo financeiro no navegador: nenhuma soma, arredondamento ou ponto flutuante nos componentes do módulo', () => {
    for (const file of ['MonthlyPlanView.tsx', 'MonthlyPlanForms.tsx', 'SixMonthPlanView.tsx', 'WorkingCalendarView.tsx', 'PlanningHub.tsx', 'monthlyPlanText.ts', '../api/monthlyPlan.ts']) {
      expect(read(file.startsWith('..') ? file.slice(3) : `components/${file}`), file).not.toMatch(/parseFloat|toFixed|Math\.(round|floor|ceil)|\.reduce\(/)
    }
  })

  it('o módulo Planejamento continua na navegação e abre o novo hub', () => {
    expect(read('components/AppShell.tsx')).toContain("['planning', 'Planejamento']")
    expect(read('components/PersonalFinance.tsx')).toContain("<PlanningHub workspaceId={workspaceId}")
  })
})
