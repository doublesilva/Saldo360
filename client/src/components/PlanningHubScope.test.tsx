import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PlanningHub } from './PlanningHub'
import { sixMonths } from './monthlyPlanFixtures'
import { planningTabFromParams, planningTabParams } from '../routing'

// P1: tela de destino de "Ver projeção". Ela precisa aplicar a competência e o escopo que o guia anuncia, não só receber os parâmetros.
afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('destino de "Ver projeção" (seis meses)', () => {
  it('consulta o planejamento com a competência e a conta anunciadas e as mostra nos filtros', async () => {
    const urls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => { urls.push(String(input)); return new Response(JSON.stringify(sixMonths), { status: 200, headers: { 'Content-Type': 'application/json' } }) }))
    render(<PlanningHub workspaceId="w" initialTab="six" initialCompetence="2026-11" initialScope={{ kind: 'account', accountId: 'acc-1' }} />)
    await waitFor(() => expect(urls.some(url => url.includes('/six-months?'))).toBe(true))
    const request = new URL(urls.find(url => url.includes('/six-months?')) as string, 'http://x')
    expect(request.searchParams.get('from')).toBe('2026-11')
    expect(request.searchParams.get('scope')).toBe('account')
    expect(request.searchParams.get('accountId')).toBe('acc-1')
    expect((screen.getByLabelText('Competência') as HTMLInputElement).value).toBe('2026-11')
    expect((screen.getByLabelText('Conta do planejamento') as HTMLSelectElement).value).toBe('account:acc-1')
  })

  it('a aba "six" sobrevive à ida e volta pela URL', () => {
    expect(planningTabFromParams(new URLSearchParams({ aba: planningTabParams.six }))).toBe('six')
  })
})
