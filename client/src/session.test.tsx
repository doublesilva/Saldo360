import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SLOW_SESSION_MS, useSession } from './session'

const user = { id: 's', issuer: 'i', subject: 's', displayName: 'Pessoa Teste', username: null, email: null }

afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals() })

describe('useSession', () => {
  beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }))

  it('sem sessão (401) é "anonymous", e falha de consulta é "error" — nunca um pelo outro', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 401 })))
    const anonymous = renderHook(() => useSession())
    await waitFor(() => expect(anonymous.result.current.state.kind).toBe('anonymous'))
    cleanup()
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 503 })))
    const failed = renderHook(() => useSession())
    await waitFor(() => expect(failed.result.current.state.kind).toBe('error'))
    cleanup()
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch') }))
    const offline = renderHook(() => useSession())
    await waitFor(() => expect(offline.result.current.state.kind).toBe('error'))
  })

  it('consulta lenta: depois de 2 segundos sinaliza "slow" e some quando a resposta chega', async () => {
    let answer!: (response: Response) => void
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(resolve => { answer = resolve })))
    const { result } = renderHook(() => useSession())
    expect(result.current.slow).toBe(false)
    await act(async () => { await vi.advanceTimersByTimeAsync(SLOW_SESSION_MS + 50) })
    expect(result.current.state.kind).toBe('loading')
    expect(result.current.slow).toBe(true)
    await act(async () => { answer(Response.json(user)) })
    await waitFor(() => expect(result.current.state.kind).toBe('authenticated'))
    expect(result.current.slow).toBe(false)
  })

  it('"Tentar novamente" descarta a resposta antiga: ela não sobrescreve o estado mais recente', async () => {
    const answers: ((response: Response) => void)[] = []
    const signals: AbortSignal[] = []
    vi.stubGlobal('fetch', vi.fn((_url: string, init?: RequestInit) => new Promise<Response>(resolve => { answers.push(resolve); signals.push(init!.signal!) })))
    const { result } = renderHook(() => useSession())
    await waitFor(() => expect(answers).toHaveLength(1))
    act(() => result.current.retry?.())
    await waitFor(() => expect(answers).toHaveLength(2))
    expect(signals[0].aborted).toBe(true)
    await act(async () => { answers[1](new Response(null, { status: 401 })) })
    await waitFor(() => expect(result.current.state.kind).toBe('anonymous'))
    await act(async () => { answers[0](Response.json(user)) }) // resposta tardia da primeira tentativa
    expect(result.current.state.kind).toBe('anonymous')
  })

  it('após uma falha, tentar novamente volta a "loading" e recupera a sessão', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 500 }))
      .mockResolvedValueOnce(Response.json(user))
    vi.stubGlobal('fetch', fetchMock)
    const { result } = renderHook(() => useSession())
    await waitFor(() => expect(result.current.state.kind).toBe('error'))
    act(() => result.current.retry?.())
    expect(result.current.state.kind).toBe('loading')
    await waitFor(() => expect(result.current.state.kind).toBe('authenticated'))
  })
})
