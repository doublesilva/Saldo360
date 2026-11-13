import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

// P1: o bloco "Configure seu orçamento" vive em CSS próprio e segue as regras de largura da Visão Geral.
const css = readFileSync(join(process.cwd(), 'src/components/overview/OverviewSetup.css'), 'utf8')

describe('layout do guia de primeira configuração', () => {
  it('uma coluna por padrão e só amplia por largura do contêiner, sem larguras fixas em px', () => {
    expect(css).toMatch(/\.ov-setup__steps\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\)/)
    expect(css).toMatch(/@container ov \(min-width: 40rem\)\s*\{[^}]*\.ov-setup__steps\s*\{\s*grid-template-columns:\s*repeat\(2, minmax\(0, 1fr\)\)/)
    expect(css).toMatch(/@container ov \(min-width: 62rem\)\s*\{[^}]*\.ov-setup__steps\s*\{\s*grid-template-columns:\s*repeat\(4, minmax\(0, 1fr\)\)/)
    expect(css).not.toMatch(/[^-](?:min-|max-)?width:\s*\d+px\s*;/)
    expect(css).not.toMatch(/overflow-x:\s*scroll/)
  })
  it('quebra textos longos e não usa texto abaixo de 12px', () => {
    expect(css).toContain('overflow-wrap: anywhere')
    expect(css).not.toMatch(/font-size:\s*0?\.[0-6]\d*rem/)
  })
  it('o estado da etapa não depende só de cor: o texto do estado está sempre presente', () => {
    const source = readFileSync(join(process.cwd(), 'src/components/overview/OverviewSetupGuide.tsx'), 'utf8')
    expect(source).toContain('{step.stateLabel}')
  })
})
