import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DecisionQueue } from './DecisionQueue'
import { classificationItem, classificationPage, importItem, reviewPage } from './queueFixtures'

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')

interface Api { review?: unknown; classification?: unknown }
/** Estado mutável: cada teste decide o que as duas fontes devolvem em cada momento; todo método diferente de GET fica registrado. */
function mockApi(api: Api = {}) {
  const state = { review: api.review ?? reviewPage([]), classification: api.classification ?? classificationPage([]), writes: [] as string[] }
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input), method = init?.method ?? 'GET'
    if (method !== 'GET') {
      if (url.includes('/csrf')) return json({ token: 't' })
      state.writes.push(`${method} ${url}`); return json({})
    }
    if (url.includes('/classification-review')) return state.classification === 'fail' ? json({ title: 'erro' }, 500) : json(state.classification)
    if (url.includes('/pluggy-integration/review')) return state.review === 'fail' ? json({ title: 'erro' }, 500) : json(state.review)
    if (url.includes('/categories')) return json([])
    return json({})
  }))
  return state
}

describe('Requer sua decisão — fila', () => {
  it('fila vazia: diz que nada requer decisão', async () => {
    mockApi()
    render(<DecisionQueue workspaceId="w" />)
    expect(await screen.findByText('Nenhuma pendência requer sua decisão agora.')).toBeTruthy()
  })

  it('carregamento: mostra o estado de carregamento antes do resultado', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})))
    render(<DecisionQueue workspaceId="w" />)
    expect(screen.getByText('Carregando as pendências…')).toBeTruthy()
  })

  it('cada item mostra tipo, descrição, conta, data, valor, motivo e impacto — fora dos totais primeiro', async () => {
    mockApi({
      review: reviewPage([importItem({ postingId: 'in', description: 'Já contado', accounted: true, date: '2026-11-10' }), importItem({ postingId: 'out', description: 'PIX Mercado', accounted: false, amount: '120.00', date: '2026-11-05' })]),
      classification: classificationPage([classificationItem({ description: 'Loja X' })]),
    })
    render(<DecisionQueue workspaceId="w" />)
    const items = await screen.findAllByRole('listitem')
    expect(items).toHaveLength(3)
    const first = text(items[0])
    expect(first).toContain('PIX Mercado')
    expect(first).toContain('Conta Corrente')
    expect(first).toContain('05/11/2026')
    expect(first).toContain('120,00')
    expect(first).toContain('Motivo:')
    expect(first).toContain('Fora dos totais até a revisão')
    expect(text(items[1])).toContain('Já contabilizado · continua nos totais até a decisão')
    expect(text(items[2])).toContain('Já contabilizado · categoria pendente')
    expect(within(items[2]).getByRole('button', { name: 'Revisar Loja X' })).toBeTruthy()
  })

  it('falha parcial: mantém as pendências da fonte disponível e sinaliza cobertura parcial com nova tentativa', async () => {
    const state = mockApi({ review: reviewPage([importItem({ description: 'PIX Mercado' })]), classification: 'fail' })
    render(<DecisionQueue workspaceId="w" />)
    expect(await screen.findByText('PIX Mercado')).toBeTruthy()
    const alert = screen.getByRole('alert')
    expect(text(alert)).toContain('Cobertura parcial')
    expect(text(alert)).toContain('classificações pendentes')
    state.classification = classificationPage([classificationItem({ description: 'Loja X' })])
    fireEvent.click(within(alert).getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByText('Loja X')).toBeTruthy()
    await waitFor(() => expect(screen.queryByText(/Cobertura parcial/)).toBeNull())
  })

  it('falha total: mostra o erro com Tentar novamente e nunca a fila vazia', async () => {
    mockApi({ review: 'fail', classification: 'fail' })
    render(<DecisionQueue workspaceId="w" />)
    expect(await screen.findByRole('button', { name: 'Tentar novamente' })).toBeTruthy()
    expect(screen.queryByText('Nenhuma pendência requer sua decisão agora.')).toBeNull()
  })

  it('dois movimentos parecidos sem vínculo comprovado continuam como itens separados', async () => {
    mockApi({ review: reviewPage([importItem({ postingId: 'a', description: 'Loja X', amount: '80.00' }), importItem({ postingId: 'b', description: 'Loja X', amount: '80.00' })]) })
    render(<DecisionQueue workspaceId="w" />)
    expect(await screen.findAllByRole('button', { name: 'Revisar Loja X' })).toHaveLength(2)
  })

  it('Revisar abre o fluxo existente só com o item escolhido; consultar e abrir não grava nada', async () => {
    const state = mockApi({ review: reviewPage([importItem({ postingId: 'a', description: 'Primeiro' }), importItem({ postingId: 'b', description: 'Segundo' })]) })
    render(<DecisionQueue workspaceId="w" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Revisar Segundo' }))
    expect(await screen.findByRole('heading', { name: 'Revisão do movimento' })).toBeTruthy()
    expect(screen.queryByText('Primeiro', { selector: '.transaction__description' })).toBeNull()
    expect(screen.getByText('Segundo', { selector: '.transaction__description' })).toBeTruthy()
    expect(screen.getByTestId('review-impact')).toBeTruthy()
    expect(state.writes).toEqual([])
    fireEvent.click(screen.getByRole('button', { name: /Voltar à fila/ }))
    expect(await screen.findAllByRole('listitem')).toHaveLength(2)
  })

  it('item resolvido em outra aba: informa o estado atual e atualiza a fila', async () => {
    const state = mockApi({ review: reviewPage([importItem({ postingId: 'a', description: 'PIX Mercado' }), importItem({ postingId: 'b', description: 'Outro' })]) })
    const onChanged = vi.fn()
    render(<DecisionQueue workspaceId="w" onChanged={onChanged} />)
    const open = await screen.findByRole('button', { name: 'Revisar PIX Mercado' })
    state.review = reviewPage([importItem({ postingId: 'b', description: 'Outro' })]) // resolvido em outra aba antes de abrir o detalhe
    fireEvent.click(open)
    expect(await screen.findByText(/não está mais em revisão/)).toBeTruthy()
    await waitFor(() => expect(onChanged).toHaveBeenCalledTimes(1))
    fireEvent.click(screen.getByRole('button', { name: /Voltar à fila/ }))
    expect(await screen.findByText('Outro')).toBeTruthy()
    expect(screen.queryByText('PIX Mercado')).toBeNull()
    expect(state.writes).toEqual([])
  })

  it('resolver confirmado grava uma vez, atualiza a fila e avisa a tela (indicador da Visão Geral)', async () => {
    const state = mockApi({ review: reviewPage([importItem({ postingId: 'a', description: 'PIX Mercado', allowedResolutions: ['AsExpense'] })]) })
    vi.stubGlobal('confirm', vi.fn(() => true))
    const onChanged = vi.fn()
    render(<DecisionQueue workspaceId="w" onChanged={onChanged} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Revisar PIX Mercado' }))
    await screen.findByRole('heading', { name: 'Revisão do movimento' })
    state.review = reviewPage([])
    fireEvent.click(await screen.findByRole('button', { name: /despesa/i }))
    await waitFor(() => expect(state.writes.filter(w => w.includes('/resolve'))).toHaveLength(1))
    await waitFor(() => expect(onChanged).toHaveBeenCalled())
    fireEvent.click(await screen.findByRole('button', { name: /Voltar à fila/ }))
    expect(await screen.findByText('Nenhuma pendência requer sua decisão agora.')).toBeTruthy()
  })

  it('Revisar de uma classificação abre o fluxo existente de classificação só com aquele registro', async () => {
    mockApi({ classification: classificationPage([classificationItem({ recordId: 'k1', description: 'Loja X' }), classificationItem({ recordId: 'k2', description: 'Loja Y' })]) })
    render(<DecisionQueue workspaceId="w" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Revisar Loja Y' }))
    expect(await screen.findByRole('heading', { name: 'Revisão da classificação' })).toBeTruthy()
    expect(screen.getAllByText('Loja Y').length).toBeGreaterThan(0)
    expect(screen.queryByText('Loja X')).toBeNull()
    expect(await screen.findByRole('button', { name: 'Salvar classificação' })).toBeTruthy()
  })
})
