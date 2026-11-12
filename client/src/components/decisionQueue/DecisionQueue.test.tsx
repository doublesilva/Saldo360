import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DecisionQueue } from './DecisionQueue'
import { classificationItem, classificationPage, importItem, reviewPage } from './queueFixtures'

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
const text = (element: Element | null) => (element?.textContent ?? '').replace(/[  ]/g, ' ')

interface Api { review?: unknown; classification?: unknown }
interface Paged { items: unknown[]; total: number; pageSize: number }
/** Pagina como as APIs: fatia `items` por page/pageSize da URL. `failPage` faz a página (de qualquer fonte) falhar até ser limpa. */
const slice = (source: Paged, url: string) => {
  const query = new URL(url, 'http://x').searchParams, page = Number(query.get('page') ?? 1), size = Number(query.get('pageSize') ?? 25)
  return { ...source, page, pageSize: size, items: source.items.slice((page - 1) * size, page * size) }
}
/** Estado mutável: cada teste decide o que as duas fontes devolvem em cada momento; todo método diferente de GET fica registrado. */
function mockApi(api: Api = {}) {
  const state = { review: api.review ?? reviewPage([]), classification: api.classification ?? classificationPage([]), writes: [] as string[], failPage: null as number | null, requests: [] as string[] }
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input), method = init?.method ?? 'GET'
    if (method !== 'GET') {
      if (url.includes('/csrf')) return json({ token: 't' })
      state.writes.push(`${method} ${url}`); return json({})
    }
    const failing = state.failPage !== null && new URL(url, 'http://x').searchParams.get('page') === String(state.failPage)
    if (url.includes('/classification-review')) { state.requests.push(url); return state.classification === 'fail' || failing ? json({ title: 'erro' }, 500) : json(slice(state.classification as Paged, url)) }
    if (url.includes('/pluggy-integration/review')) { state.requests.push(url); return state.review === 'fail' || failing ? json({ title: 'erro' }, 500) : json(slice(state.review as Paged, url)) }
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

  const manyImports = (count: number, over: (index: number) => Partial<ReturnType<typeof importItem>> = () => ({})) =>
    Array.from({ length: count }, (_, index) => importItem({ postingId: `r${index}`, description: `Mov ${index}`, date: `2026-10-${String((index % 28) + 1).padStart(2, '0')}`, accounted: true, ...over(index) }))
  const manyClass = (count: number) => Array.from({ length: count }, (_, index) => classificationItem({ recordId: `c${index}`, description: `Class ${index}`, date: `2026-10-${String((index % 28) + 1).padStart(2, '0')}` }))
  const rows = () => screen.queryAllByRole('listitem')

  it('com mais de 25 itens por fonte, Carregar mais leva até o último item sem resolver nada, sem duplicar', async () => {
    const state = mockApi({ review: reviewPage(manyImports(130), 130), classification: classificationPage(manyClass(60), 60) })
    render(<DecisionQueue workspaceId="w" />)
    expect(await screen.findByText('Mostrando 25 de 190')).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Requer sua decisão (190)' })).toBeTruthy()
    expect(rows()).toHaveLength(25)
    for (let shown = 25; shown < 190; shown += 25) fireEvent.click(screen.getByRole('button', { name: 'Carregar mais pendências' }))
    expect(await screen.findByText('Mostrando 190 de 190')).toBeTruthy()
    expect(rows()).toHaveLength(190)
    expect(screen.queryByRole('button', { name: 'Carregar mais pendências' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Revisar Mov 129' })).toBeTruthy()
    expect(state.writes).toEqual([])
    expect(state.requests.every(url => url.includes('pageSize=100'))).toBe(true)
  })

  it('item fora dos totais de uma página posterior da origem aparece no topo da fila', async () => {
    const items = [...manyImports(120, () => ({ date: '2026-10-20' })), importItem({ postingId: 'late', description: 'Fora dos totais tardio', accounted: false, date: '2026-01-02' })]
    mockApi({ review: reviewPage(items, items.length) })
    render(<DecisionQueue workspaceId="w" />)
    await screen.findByText('Mostrando 25 de 121')
    expect(text(rows()[0])).toContain('Fora dos totais tardio')
    expect(text(rows()[0])).toContain('Fora dos totais até a revisão')
  })

  it('falha numa página posterior preserva a lista, oferece Tentar novamente no mesmo ponto e a nova tentativa completa sem duplicar', async () => {
    const state = mockApi({ review: reviewPage(manyImports(150), 150) })
    state.failPage = 2
    render(<DecisionQueue workspaceId="w" />)
    expect(await screen.findByText('Mostrando 25 de 150')).toBeTruthy()
    const alert = await screen.findByRole('alert')
    expect(text(alert)).toContain('Cobertura parcial')
    expect(screen.getByRole('heading', { name: 'Requer sua decisão (150)' })).toBeTruthy() // o total do servidor já era conhecido
    for (let i = 0; i < 3; i++) fireEvent.click(screen.getByRole('button', { name: 'Carregar mais pendências' }))
    expect(rows()).toHaveLength(100) // só o que foi lido (primeira página)
    state.failPage = null
    fireEvent.click(within(alert).getByRole('button', { name: 'Tentar novamente' }))
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
    expect(rows()).toHaveLength(100) // a lista mostrada continua a mesma enquanto carrega
    for (let i = 0; i < 2; i++) fireEvent.click(await screen.findByRole('button', { name: 'Carregar mais pendências' }))
    expect(await screen.findByText('Mostrando 150 de 150')).toBeTruthy()
    expect(new Set(rows().map(row => text(row.querySelector('strong')))).size).toBe(150)
  })

  it('Revisar de item de página tardia abre o detalhe certo; Voltar à fila restaura os lotes e devolve o foco ao item', async () => {
    const state = mockApi({ review: reviewPage(manyImports(130, index => ({ date: index === 5 ? '2026-01-01' : '2026-10-20' })), 130) })
    render(<DecisionQueue workspaceId="w" />)
    await screen.findByText('Mostrando 25 de 130')
    for (let i = 0; i < 5; i++) fireEvent.click(screen.getByRole('button', { name: 'Carregar mais pendências' }))
    expect(rows()).toHaveLength(130)
    const target = screen.getByRole('button', { name: 'Revisar Mov 5' }) // data antiga: fica no fim da ordem, na página 1 da origem
    fireEvent.click(target)
    expect(await screen.findByRole('heading', { name: 'Revisão do movimento' })).toBeTruthy()
    expect(screen.getByText('Mov 5', { selector: '.transaction__description' })).toBeTruthy()
    expect(screen.queryByText(/não está mais em revisão/)).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: /Voltar à fila/ }))
    await waitFor(() => expect(rows()).toHaveLength(130))
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Revisar Mov 5' })))
    expect(state.writes).toEqual([])
  })

  it('item de página posterior da origem abre o detalhe certo mesmo longe da primeira página', async () => {
    const items = manyImports(250, index => ({ date: index === 220 ? '2026-12-30' : '2026-10-01' }))
    mockApi({ review: reviewPage(items, 250) })
    render(<DecisionQueue workspaceId="w" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Revisar Mov 220' })) // data mais recente: topo da fila; origem: página 3
    expect(await screen.findByText('Mov 220', { selector: '.transaction__description' })).toBeTruthy()
    expect(screen.queryByText(/não está mais em revisão/)).toBeNull()
  })

  it('Voltar à fila com o item resolvido move o foco para o próximo item disponível', async () => {
    const items = [importItem({ postingId: 'a', description: 'Primeiro', date: '2026-11-09' }), importItem({ postingId: 'b', description: 'Segundo', date: '2026-11-08' }), importItem({ postingId: 'c', description: 'Terceiro', date: '2026-11-07' })]
    const state = mockApi({ review: reviewPage(items) })
    render(<DecisionQueue workspaceId="w" />)
    const open = await screen.findByRole('button', { name: 'Revisar Segundo' })
    state.review = reviewPage([items[0], items[2]]) // resolvido em outra aba antes de abrir
    fireEvent.click(open)
    expect(await screen.findByText(/não está mais em revisão/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: /Voltar à fila/ }))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Revisar Segundo' })).toBeNull())
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Revisar Terceiro' })))
  })
})
