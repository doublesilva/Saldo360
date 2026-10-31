import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { SessionState } from './session'
import App from './App'

const session = vi.hoisted(() => ({ state: { kind: 'anonymous' } as SessionState }))
vi.mock('./session', () => ({ useSession: () => ({ state: session.state, busy: false, logout: vi.fn() }) }))
vi.mock('./api/health', () => ({ fetchHealth: vi.fn().mockResolvedValue({ status: 'Healthy', totalDurationMs: 1, checks: [] }) }))
vi.mock('./components/PersonalFinance', () => ({ PersonalFinance: () => <p>Área financeira</p> }))
vi.mock('./components/UserMenu', () => ({ UserMenu: () => <p>Menu</p> }))
vi.mock('./components/InvitePage', () => ({ InvitePage: () => <p>Convite</p> }))

beforeEach(() => window.history.replaceState(null, '', '/?login=failed&ref=0HNABC123'))
afterEach(() => { cleanup(); window.history.replaceState(null, '', '/') })

describe('App — aviso de falha de login', () => {
  it('mostra o código de referência enquanto não há sessão', async () => {
    session.state = { kind: 'anonymous' }
    render(<App />)

    expect((await screen.findByRole('alert')).textContent).toContain('0HNABC123')
  })

  it('com o usuário autenticado o aviso residual da URL não aparece no topo das telas e a URL é limpa', async () => {
    session.state = { kind: 'authenticated', user: { subject: 's', issuer: 'i' } as never }
    render(<App />)

    expect(await screen.findByText('Área financeira')).toBeTruthy()
    expect(screen.queryByText(/Código de referência/)).toBeNull()
    await waitFor(() => expect(window.location.search).toBe(''))
  })
})
