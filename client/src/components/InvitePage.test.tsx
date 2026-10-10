import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FamilyDetails } from '../api/families'
import { captureInviteToken, clearPendingInviteToken, getPendingInviteToken } from '../invite'
import type { Session, SessionState } from '../session'
import { InvitePage } from './InvitePage'

const loginMock = vi.hoisted(() => vi.fn())
vi.mock('../api/auth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/auth')>()),
  login: loginMock,
}))

const TOKEN = 'token-do-convite_123'

const family: FamilyDetails = {
  id: 'f1',
  name: 'Família Silva',
  createdAt: '2026-10-10T00:00:00Z',
  myRole: 'Member',
  members: [
    { displayName: 'Ana', role: 'Owner', joinedAt: '2026-10-10T00:00:00Z', isCurrentUser: false },
    { displayName: 'Bruno', role: 'Member', joinedAt: '2026-10-10T01:00:00Z', isCurrentUser: true },
  ],
}

function session(state: SessionState): Session {
  return { state, busy: false, logout: async () => {} }
}

const authenticated = session({
  kind: 'authenticated',
  user: { id: 's', issuer: 'https://auth', subject: 's', displayName: 'Bruno', username: 'bruno', email: null },
})

/** fetch simulado: /auth/csrf e o aceite com o status informado. */
function mockFetch(acceptStatus: number, acceptBody: unknown = {}) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
    const url = String(input)
    if (url === '/auth/csrf') return new Response(JSON.stringify({ token: 'csrf' }), { status: 200 })
    if (url === '/api/family-invitations/accept') return new Response(JSON.stringify(acceptBody), { status: acceptStatus })
    throw new Error(`requisição inesperada: ${url}`)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

beforeEach(() => {
  window.history.replaceState(null, '', `/convite#token=${TOKEN}`)
  captureInviteToken()
})

afterEach(() => {
  cleanup()
  clearPendingInviteToken()
  vi.unstubAllGlobals()
  loginMock.mockReset()
})

describe('InvitePage', () => {
  it('sem sessão: pede login e preserva o token para depois do retorno', () => {
    const fetchMock = mockFetch(200)
    render(<InvitePage session={session({ kind: 'anonymous' })} onAccepted={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: 'Entrar para aceitar' }))

    expect(loginMock).toHaveBeenCalledOnce()
    expect(getPendingInviteToken()).toBe(TOKEN)
    expect(window.location.href).not.toContain(TOKEN)
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('depois do login: só aceita com clique explícito e envia o token apenas no corpo', async () => {
    const fetchMock = mockFetch(200, { alreadyMember: false, family })
    const onAccepted = vi.fn()
    // Nova montagem, como ao voltar do provedor de login.
    render(<InvitePage session={authenticated} onAccepted={onAccepted} />)

    expect(fetchMock).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Aceitar convite' }))

    await waitFor(() => expect(onAccepted).toHaveBeenCalledWith(family))
    const acceptCall = fetchMock.mock.calls.find(([url]) => String(url) === '/api/family-invitations/accept')!
    const init = acceptCall[1]!
    expect(String(acceptCall[0])).not.toContain(TOKEN)
    expect(init.method).toBe('POST')
    expect(JSON.parse(String(init.body))).toEqual({ token: TOKEN })
    expect((init.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    expect(getPendingInviteToken()).toBeNull()
  })

  it('convite indisponível: mensagem genérica e token descartado', async () => {
    mockFetch(404, { title: 'Convite inválido, expirado ou já utilizado.' })
    render(<InvitePage session={authenticated} onAccepted={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: 'Aceitar convite' }))

    expect(await screen.findByText(/Convite inválido, expirado ou já utilizado/)).toBeTruthy()
    expect(getPendingInviteToken()).toBeNull()
  })

  it('falha transitória: mantém o token e permite tentar de novo', async () => {
    mockFetch(500)
    const onAccepted = vi.fn()
    render(<InvitePage session={authenticated} onAccepted={onAccepted} />)

    fireEvent.click(screen.getByRole('button', { name: 'Aceitar convite' }))

    expect(await screen.findByRole('alert')).toBeTruthy()
    expect(getPendingInviteToken()).toBe(TOKEN)
    expect(screen.getByRole('button', { name: 'Aceitar convite' })).toBeTruthy()
    expect(onAccepted).not.toHaveBeenCalled()
  })

  it('sessão expirada no aceite: mantém o token e volta ao login', async () => {
    mockFetch(401)
    render(<InvitePage session={authenticated} onAccepted={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: 'Aceitar convite' }))

    await waitFor(() => expect(loginMock).toHaveBeenCalledOnce())
    expect(getPendingInviteToken()).toBe(TOKEN)
  })

  it('descartar remove o token', () => {
    render(<InvitePage session={authenticated} onAccepted={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: 'Descartar convite' }))

    expect(getPendingInviteToken()).toBeNull()
    expect(screen.getByText(/Nenhum convite pendente/)).toBeTruthy()
  })

  it('sem token: orienta a abrir o link recebido', () => {
    clearPendingInviteToken()
    render(<InvitePage session={authenticated} onAccepted={vi.fn()} />)

    expect(screen.getByText(/Nenhum convite pendente/)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Aceitar convite' })).toBeNull()
  })
})
