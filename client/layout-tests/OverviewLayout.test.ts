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
    expect(css).toMatch(/@container ov \(min-width: 62rem\)\s*\{[^@]*\.ov-hero\s*\{\s*grid-template-columns:\s*minmax\(0, 1fr\) minmax\(0, 1\.75fr\)/)
    expect(css).toMatch(/@container ov \(min-width: 62rem\)\s*\{[^@]*\.ov-grid\s*\{\s*grid-template-columns:\s*minmax\(0, 2\.6fr\) minmax\(0, 1fr\)/)
    expect(css).toMatch(/@container ov \(min-width: 40rem\)\s*\{[^@]*\.ov-hero__kpis\s*\{\s*grid-template-columns:\s*repeat\(3, minmax\(0, 1fr\)\)/)
    expect(css).toMatch(/@container ovmain \(min-width: 40rem\)\s*\{\s*\.ov-pair\s*\{\s*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/)
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
  it('a página segue a ordem do Folio e não monta os painéis antigos', () => {
    const page = src('components/overview/Overview.tsx')
    const order = ['<OverviewHero', '<OverviewAlerts', '<OverviewEvolutionSection', '<OverviewExpenseDistribution', '<OverviewBudgetProgress', '<OverviewRecent', '<OverviewAccounts', '<OverviewUpcoming', '<OverviewGoals'].map(tag => page.indexOf(tag))
    expect(order.every(index => index > 0)).toBe(true)
    expect([...order].sort((a, b) => a - b)).toEqual(order)
    for (const legacy of ['Disclosure', 'OverviewDecision', 'OverviewBudgetControl', 'OverviewCashFlow', 'OverviewRecovery', 'OverviewProjectedRecovery', 'OverviewActions', 'OverviewIndicators', 'OverviewAside']) {
      expect(page).not.toContain(legacy)
      expect(existsSync(join(overviewDir, `${legacy}.tsx`))).toBe(false)
    }
  })

  it('a recuperação do cheque especial é compacta, dentro do bloco do saldo, com medidor acessível', () => {
    const hero = src('components/overview/OverviewHero.tsx')
    expect(hero).toContain('role="meter"'); expect(hero).toContain('aria-valuetext')
    expect(hero).toMatch(/if \(!overdraft\.eligible\) return null/)
    expect(css).toMatch(/\.ov-meter > span\s*\{[^}]*calc\(100% \* var\(--ov-part\) \/ var\(--ov-whole\)\)/)
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
