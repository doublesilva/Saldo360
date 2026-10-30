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

  it('reorganiza em degraus (4 indicadores em linha → 2 por linha) e em uma coluna de blocos e prioriza compromissos e contas no celular', () => {
    expect(css).toMatch(/@media \(max-width: 75rem\)[^}]*grid-template-columns: repeat\(2/)
    expect(css).toMatch(/@media \(max-width: 64rem\)/)
    expect(css).toMatch(/@media \(max-width: 40rem\)[\s\S]*\.ov-indicators\s*\{[^}]*repeat\(2, minmax\(0, 1fr\)\)/)
    expect(css).toMatch(/\.ov-slot--commitments\s*\{\s*order:\s*1/)
    expect(css).toMatch(/\.ov-slot--accounts\s*\{\s*order:\s*2/)
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
