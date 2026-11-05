import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { FinancialProjection } from '../../api/projection'
import { projection } from '../projectionFixtures'
import { OverviewProjectedRecovery } from './OverviewProjectedRecovery'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')
const stub = (build: (url: URL) => FinancialProjection) => {
  const fetchMock = vi.fn((input: string, _init?: RequestInit) => {
    const url = new URL(String(input), 'http://x')
    return Promise.resolve(new Response(JSON.stringify(build(url)), { status: 200, headers: { 'Content-Type': 'application/json' } }))
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('Visão Geral — recuperação do limite projetada', () => {
  it('resume a utilização de abertura e, em cada cenário, a utilização e a recuperação acumulada no fechamento do horizonte', async () => {
    stub(url => projection(url.searchParams.get('from')!, Number(url.searchParams.get('months'))))
    render(<OverviewProjectedRecovery workspaceId="w" competence="2026-10" refreshKey={0} onOpen={() => undefined} />)
    const card = await screen.findByRole('region', { name: 'Recuperação do limite projetada' })
    await screen.findByText(/Atual · fechamento de Março de 2027/)
    expect(text(card)).toMatch(/Utilização na abertura de Outubro de 2026 \(posição de referência\)\s*R\$ 1\.000,00/)
    expect(text(card)).toMatch(/Atual · fechamento de Março de 2027\s*Utilização R\$ 0,00 · Recuperação acumulada R\$ 1\.000,00/)
    expect(text(card)).toMatch(/Tetos · fechamento de Março de 2027\s*Utilização R\$ 1\.500,00 · Recuperação acumulada -R\$ 500,00/)
  })

  it('usa o horizonte escolhido e avisa que juros e IOF não são estimados', async () => {
    const fetchMock = stub(url => ({ ...projection(url.searchParams.get('from')!, Number(url.searchParams.get('months'))), notes: ['OverdraftChargesNotEstimated'] }))
    render(<OverviewProjectedRecovery workspaceId="w" competence="2026-10" refreshKey={0} onOpen={() => undefined} />)
    await screen.findByText(/Atual · fechamento de Março de 2027/)
    fireEvent.change(screen.getByLabelText('Horizonte da recuperação do limite'), { target: { value: '13' } })
    await screen.findByText(/Atual · fechamento de Outubro de 2027/)
    expect(new URL(String(fetchMock.mock.calls.at(-1)![0]), 'http://x').searchParams.get('months')).toBe('13')
    expect(screen.getByRole('note').textContent).toContain('Juros e IOF futuros')
  })

  it('mostra indisponível, não zero, quando não há limite conhecido, e marca projeção parcial', async () => {
    stub(url => projection(url.searchParams.get('from')!, 6, (_c, _s, index) => index === 5 ? { overdraft: null, complete: false } : {}))
    render(<OverviewProjectedRecovery workspaceId="w" competence="2026-10" refreshKey={0} onOpen={() => undefined} />)
    const card = await screen.findByRole('region', { name: 'Recuperação do limite projetada' })
    await screen.findByText(/Atual · fechamento/)
    expect(text(card)).toMatch(/Utilização Indisponível · Recuperação acumulada Indisponível · projeção parcial/)
  })

  it('leva ao Planejamento, na Projeção financeira, e só lê dados', async () => {
    const fetchMock = stub(url => projection(url.searchParams.get('from')!, 6))
    const onOpen = vi.fn()
    render(<OverviewProjectedRecovery workspaceId="w" competence="2026-10" refreshKey={0} onOpen={onOpen} />)
    await screen.findByText(/Atual · fechamento/)
    fireEvent.click(screen.getByRole('button', { name: 'Abrir a Projeção financeira' }))
    await waitFor(() => expect(onOpen).toHaveBeenCalledTimes(1))
    expect(fetchMock.mock.calls.every(([, init]) => ((init as RequestInit | undefined)?.method ?? 'GET') === 'GET')).toBe(true)
  })
})
