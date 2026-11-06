// @vitest-environment node
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/** Contrato visual do orçamento padrão (T12.1): lê os arquivos-fonte, pois o jsdom não calcula layout nem mede overflow. */
const src = (relative: string) => readFileSync(join(process.cwd(), 'src', relative), 'utf8')
const css = src('Budget.css')

describe('Responsividade do orçamento padrão', () => {
  it('usa linhas fluidas que quebram em telas estreitas (390 e 360 px): sem larguras fixas nem rolagem horizontal', () => {
    expect(css).toMatch(/\.rbp\s*\{[^}]*min-width:\s*0/)
    expect(css).toMatch(/\.rbp__row\s*\{[^}]*overflow-wrap:\s*anywhere/)
    expect(css).toMatch(/\.rbp__main\s*\{[^}]*flex-wrap:\s*wrap/)
    expect(css).toMatch(/\.rbp__buttons\s*\{[^}]*flex-wrap:\s*wrap/)
    const block = css.slice(css.indexOf('/* T12.1'))
    expect(block).not.toMatch(/[^-](?:min-|max-)?width:\s*\d+px/)
    expect(block).not.toMatch(/overflow-x:\s*scroll/)
  })

  it('o painel e os textos não calculam dinheiro no navegador', () => {
    for (const file of ['components/budget/RecurringBudgetPanel.tsx', 'components/budget/recurringBudgetText.ts', 'api/effectiveBudget.ts']) {
      expect(src(file), file).not.toMatch(/toFixed|Math\.(round|floor|ceil)|\.reduce\(\s*\([^)]*\)\s*=>\s*[a-z]+\s*\+/)
    }
  })
})
