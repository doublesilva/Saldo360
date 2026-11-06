// @vitest-environment node
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/** Contrato visual e financeiro da Visão Geral (R01): lê os arquivos-fonte, pois o jsdom não calcula layout nem mede overflow. */
const src = (relative: string) => readFileSync(join(process.cwd(), 'src', relative), 'utf8')
const css = src('Overview.css')
const overviewFiles = readdirSync(join(process.cwd(), 'src', 'components', 'overview')).filter(file => /\.(tsx?|ts)$/.test(file) && !/\.test\.|Fixtures/.test(file))

describe('Responsividade da Visão Geral', () => {
  it('usa grades fluidas e permite encolher: nada de larguras fixas nem rolagem horizontal da página', () => {
    expect(css).toMatch(/\.ov\s*\{[^}]*min-width:\s*0/)
    expect(css).toContain('minmax(0, 1fr)')
    expect(css).not.toMatch(/overflow-x:\s*scroll/)
    // Larguras fixas em px são proibidas (o 1px do utilitário de texto não existe aqui); as barras usam proporções.
    expect(css).not.toMatch(/[^-](?:min-|max-)?width:\s*\d+px\s*;/)
    // Rolagem horizontal só dentro da tabela alternativa.
    expect(css).toMatch(/\.ov-table-wrap\s*\{[^}]*overflow-x:\s*auto/)
  })

  it('T12.2: quatro indicadores em linha no desktop, dois em 768, dois compactos com a cobertura em largura total em 390/360', () => {
    expect(css).toMatch(/\.ov-situation__cards\s*\{[^}]*repeat\(4, minmax\(0, 1fr\)\)/)
    expect(css).toMatch(/@media \(max-width: 75rem\)\s*\{[^}]*\.ov-situation__cards[^}]*repeat\(2, minmax\(0, 1fr\)\)/) // 1200px: 768px cai aqui
    expect(css).toMatch(/@media \(max-width: 42rem\)[\s\S]*\.ov-situation__cards \.ov-decision__coverage\s*\{\s*grid-column:\s*1 \/ -1/) // 672px: 390 e 360
    expect(css).toMatch(/@media \(max-width: 64rem\)\s*\{\s*\.ov-situation__details\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/)
    expect(css).toMatch(/@media \(max-width: 42rem\)[\s\S]*\.ov-budget-groups\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/)
    expect(css).not.toMatch(/position:\s*sticky/) // sem faixas fixas ocupando a tela
    expect(css).toMatch(/\.ov-situation__context\s*\{[^}]*overflow-wrap:\s*anywhere/)
    expect(css).toMatch(/\.ov-action__causes li\s*\{[^}]*overflow-wrap:\s*anywhere/)
  })

  it('T12.2: tipografia e espaçamento da especificação (título 24px, seção 1.2rem, indicador 26–32px, auxiliar 13px, cards 16px, seções 32px, padding 20px)', () => {
    expect(css).toMatch(/\.ov-eyebrow\s*\{[^}]*font-size:\s*1\.5rem/)
    expect(css).toMatch(/\.ov-area__head h2[^{]*\{[^}]*font-size:\s*1\.2rem/)
    expect(css).toMatch(/\.ov-indicator__value\s*\{[^}]*clamp\(1\.65rem, 2\.4vw, 2rem\)/)
    expect(css).toMatch(/\.ov-meta\s*\{[^}]*font-size:\s*\.8125rem/)
    expect(css).toMatch(/\.ov\s*\{[^}]*gap:\s*2rem/)
    expect(css).toMatch(/\.ov-situation__cards\s*\{[^}]*gap:\s*1rem/)
    expect(css).toMatch(/\.ov-card\s*\{[^}]*padding:\s*1\.25rem/)
    expect(css).not.toMatch(/font-size:\s*0?\.[0-6]\d*rem/) // nenhum texto abaixo de 12px (0,75rem); o auxiliar fica em 13px
  })

  it('T12.2: botões e títulos expansíveis são alvos de toque (44px) no celular', () => {
    expect(css).toMatch(/@media \(max-width: 42rem\)[\s\S]*\.ov button\s*\{[^}]*min-height:\s*2\.75rem/)
    expect(css).toMatch(/\.ov-disclosure > summary\s*\{[^}]*min-height:\s*2\.75rem/)
  })

  it('T12.2: as áreas vêm na ordem situação, orçamento, recuperação, onde agir e análises detalhadas, e as análises só montam aberto', () => {
    const page = src('components/overview/Overview.tsx')
    const order = ['<OverviewSituationSection', '<OverviewBudgetSummarySection', '<OverviewRecoverySummary', '<OverviewActionsSection', 'id="ov-details-title"'].map(tag => page.indexOf(tag))
    expect(order.every(index => index > 0)).toBe(true)
    expect([...order].sort((a, b) => a - b)).toEqual(order)
    const disclosure = src('components/overview/Disclosure.tsx')
    expect(disclosure).toContain('{open && <div')
    // O resumo não faz as consultas de evolução e de projeção: elas ficam dentro das seções expansíveis.
    expect(page).not.toMatch(/getSixMonthPlan|getFinancialProjection/)
  })

  it('as barras da evolução financeira não são sobrescritas por outra regra .ov-bar (altura total, coluna flex)', () => {
    const rules = [...css.matchAll(/(?:^|\n)\.ov-bar\s*\{([^}]*)\}/g)].map(match => match[1])
    expect(rules).toHaveLength(1)
    expect(rules[0]).toMatch(/height:\s*100%/)
    expect(rules[0]).toMatch(/flex-direction:\s*column/)
  })

  it('a recuperação do cheque especial usa só proporções e cores por faixa (sem larguras fixas nem duplicar .sr-only)', () => {
    for (const band of ['Exceeded', 'Met', 'Attention', 'Critical']) expect(css).toContain(`.ov-recovery--${band}`)
    expect(css).toMatch(/\.ov-recovery\s*\{[^}]*display:\s*grid/)
    expect(css).toMatch(/\.ov-recovery__percent\s*\{[^}]*overflow-wrap:\s*anywhere/)
    expect(css).not.toMatch(/\.sr-only/)
    const component = src('components/overview/OverviewCashFlow.tsx')
    expect(component).toContain('role="progressbar"')
    expect(component).toContain('aria-valuetext')
  })

  it('nenhum valor é truncado com reticências; o texto quebra de linha', () => {
    expect(css).not.toContain('text-overflow: ellipsis')
    expect(css).toContain('overflow-wrap: anywhere')
  })
})

describe('Contrato financeiro da Visão Geral', () => {
  it('não calcula dinheiro no navegador: sem soma, arredondamento ou ponto flutuante nos componentes (exceto a geometria das barras)', () => {
    expect(overviewFiles.length).toBeGreaterThan(5)
    for (const file of overviewFiles) {
      const source = readFileSync(join(process.cwd(), 'src', 'components', 'overview', file), 'utf8')
      expect(source, file).not.toMatch(/toFixed|Math\.(round|floor|ceil)|\.reduce\(\s*\([^)]*\)\s*=>\s*[a-z]+\s*\+/)
      // parseFloat só mede a altura relativa das barras da evolução (nunca é exibido nem somado).
      if (!['OverviewEvolution.tsx'].includes(file)) expect(source, file).not.toMatch(/parseFloat/)
    }
    expect(src('api/overview.ts')).not.toMatch(/parseFloat|toFixed|Number\(/)
  })

  it('a navegação não oferece destinos retirados na R00 nem páginas vazias', () => {
    const shell = src('components/AppShell.tsx')
    for (const retired of ['Documentos', 'Inbox', 'Importa', 'Concilia', 'Confer', 'Relatórios']) expect(shell).not.toContain(retired)
    for (const kept of ['Visão Geral', 'Lançamentos', 'Contas', 'Cartões', 'Orçamento', 'Agenda', 'Metas']) expect(shell).toContain(`'${kept}'`)
  })
})
