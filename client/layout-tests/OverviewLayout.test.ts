// @vitest-environment node
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/** Contrato visual e financeiro da Visão Geral (dashboard Folio): lê os arquivos-fonte, pois o jsdom não calcula layout nem mede overflow. */
const src = (relative: string) => readFileSync(join(process.cwd(), 'src', relative), 'utf8')
const css = src('Overview.css')
const overviewDir = join(process.cwd(), 'src', 'components', 'overview')
const overviewFiles = readdirSync(overviewDir).filter(file => /\.(tsx?|ts)$/.test(file) && !/\.test\.|Fixtures/.test(file))

describe('Responsividade da Visão Geral', () => {
  it('usa grades fluidas e permite encolher: nada de larguras fixas nem rolagem horizontal da página', () => {
    expect(css).toMatch(/\.ov\s*\{[^}]*min-width:\s*0/)
    expect(css).toContain('minmax(0, 1fr)')
    expect(css).not.toMatch(/overflow-x:\s*scroll/)
    expect(css).not.toMatch(/[^-](?:min-|max-)?width:\s*\d+px\s*;/)
    // Rolagem horizontal só dentro da tabela de valores do gráfico.
    expect(css).toMatch(/\.ov-table-wrap\s*\{[^}]*overflow-x:\s*auto/)
  })

  it('o layout acompanha a largura real do conteúdo ao lado do menu (container), como no Folio: corpo e coluna de apoio no desktop, uma coluna no tablet e no celular', () => {
    expect(css).toMatch(/\.ov\s*\{[^}]*container:\s*ov \/ inline-size/)
    // Topo (T14.3): no desktop o resultado e a composição ocupam a coluna principal e o saldo/cheque especial fica ao lado; no celular é uma coluna na ordem A, B, C.
    expect(css).toMatch(/\.ov-top\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/)
    expect(css).toMatch(/@container ov \(min-width: 62rem\)\s*\{[^@]*\.ov-top\s*\{\s*grid-template-columns:\s*minmax\(0, 1\.75fr\) minmax\(0, 1fr\);\s*grid-template-areas:\s*'result balance' 'composition balance'/)
    expect(css).toMatch(/@container ov \(min-width: 62rem\)\s*\{[^@]*\.ov-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 2\.6fr\) minmax\(0, 1fr\)/)
    expect(css).toMatch(/@container ov \(min-width: 40rem\)\s*\{[^@]*\.ov-result__kpis\s*\{\s*grid-template-columns:\s*repeat\(4, minmax\(0, 1fr\)\)/)
    expect(css).toMatch(/\.ov-result__kpis\s*\{[^}]*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/)
    // Orçamento mensal (com as categorias dentro da Meta total): largura total e altura natural (sem altura fixa nem esticar).
    expect(css).toMatch(/\.ov-budget-area\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\);[^}]*gap:\s*1\.5rem;[^}]*align-items:\s*start/)
    expect(css).toMatch(/\.ov-budget-area > \.ov-card\s*\{[^}]*width:\s*100%;[^}]*height:\s*auto;[^}]*align-self:\s*start/)
    expect(css).not.toMatch(/\.ov-budget-area[^{]*\{[^}]*(?:stretch|[^-]height:\s*\d)/)
    expect(css).not.toMatch(/@container[^{]*\{[^@]*\.ov-budget-area/)
    // Em larguras estreitas a tabela de lançamentos esconde Categoria e Conta e as mostra sob a descrição.
    expect(css).toMatch(/@container ovmain \(max-width: 34rem\)\s*\{\s*\.ov-recent__table \.ov-wide\s*\{\s*display:\s*none/)
    expect(css).not.toMatch(/position:\s*sticky/)
  })

  it('tokens do Folio (variante escura) só dentro do dashboard; números em fonte monoespaçada', () => {
    for (const token of ['--ov-card: #1b211f', '--ov-fg: #e5eae7', '--ov-border: #303934', '--ov-primary: #82c8ad', '--ov-danger: #e28d88', '--ov-radius: 6px']) expect(css).toContain(token)
    expect(css).toMatch(/\.ov-mono\s*\{[^}]*font-family:\s*var\(--ov-mono\)/)
    expect(css).not.toMatch(/^:root/m)
    // As etiquetas continuam globais: Orçamento e Projeção as usam fora do dashboard.
    expect(css).toMatch(/^\.ov-tag\s*\{/m); expect(css).toMatch(/^\.ov-stale\s*\{/m)
  })

  it('nenhum texto abaixo de 12px e alvos de toque de 44px no celular', () => {
    expect(css).not.toMatch(/font-size:\s*0?\.[0-6]\d*rem/)
    expect(css).toMatch(/@media \(max-width: 42rem\)\s*\{[^}]*\.ov button[^{]*\{\s*min-height:\s*2\.75rem/)
  })

  it('nenhum valor é truncado com reticências; o texto quebra de linha', () => {
    expect(css).not.toContain('text-overflow: ellipsis')
    expect(css).toContain('overflow-wrap: anywhere')
  })
})

describe('Composição Folio', () => {
  it('a página segue a ordem: ações prioritárias logo abaixo dos filtros, depois resumo, resultado, composição, saldo, orçamento e evolução e não monta os painéis antigos', () => {
    const page = src('components/overview/Overview.tsx')
    const order = ['<OverviewAlerts', '<OverviewSummary', '<OverviewResult', '<OverviewComposition', '<OverviewBalanceCard', '<OverviewBudgetProgress', '<OverviewEvolutionSection', '<OverviewRecent', '<OverviewAccounts', '<OverviewUpcoming', '<OverviewGoals'].map(tag => page.indexOf(tag))
    expect(order.every(index => index > 0)).toBe(true)
    expect([...order].sort((a, b) => a - b)).toEqual(order)
    for (const legacy of ['Disclosure', 'OverviewDecision', 'OverviewBudgetControl', 'OverviewCashFlow', 'OverviewRecovery', 'OverviewProjectedRecovery', 'OverviewActions', 'OverviewIndicators', 'OverviewAside']) {
      expect(page).not.toContain(legacy)
      expect(existsSync(join(overviewDir, `${legacy}.tsx`))).toBe(false)
    }
  })

  it('a recuperação do cheque especial é compacta, dentro do bloco do saldo, com medidor acessível', () => {
    const hero = src('components/overview/OverviewBalanceCard.tsx')
    expect(existsSync(join(overviewDir, 'OverviewHero.tsx'))).toBe(false)
    expect(hero).toContain('role="meter"'); expect(hero).toContain('aria-valuetext')
    expect(hero).toMatch(/if \(!overdraft\.eligible\) return null/)
    expect(css).toMatch(/\.ov-meter > span\s*\{[^}]*calc\(100% \* var\(--ov-part\) \/ var\(--ov-whole\)\)/)
  })
})

describe('Estados de carregamento (T14.3)', () => {
  it('esqueletos são neutros e respeitam prefers-reduced-motion; "Carregando" nunca usa a cor de erro', () => {
    expect(css).toMatch(/\.ov-skeleton\s*\{[^}]*var\(--ov-muted-bg\)/)
    expect(css).not.toMatch(/\.ov-skeleton\s*\{[^}]*danger/)
    expect(css).toMatch(/@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.ov-skeleton\s*\{\s*animation:\s*none/)
    for (const file of ['OverviewResult.tsx', 'OverviewBalanceCard.tsx']) expect(src(`components/overview/${file}`)).not.toMatch(/Carregando…'/)
  })
})

describe('Contrato financeiro da Visão Geral', () => {
  it('não calcula dinheiro no navegador: sem soma, arredondamento ou ponto flutuante nos componentes (exceto a geometria do gráfico)', () => {
    expect(overviewFiles.length).toBeGreaterThan(5)
    for (const file of overviewFiles) {
      const source = readFileSync(join(overviewDir, file), 'utf8')
      expect(source, file).not.toMatch(/toFixed|Math\.(round|floor|ceil)|\.reduce\(\s*\([^)]*\)\s*=>\s*[a-z]+\s*\+/)
      // parseFloat só posiciona os pontos do gráfico da evolução (nunca é exibido nem somado).
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
