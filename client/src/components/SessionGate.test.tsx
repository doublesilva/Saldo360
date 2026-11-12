import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Session, SessionState } from '../session'
import { SessionGate } from './SessionGate'

const login = vi.hoisted(() => vi.fn())
vi.mock('../api/auth', () => ({ login }))

const session = (state: SessionState, extra: Partial<Session> = {}): Session => ({ state, busy: false, slow: false, logout: vi.fn(), retry: vi.fn(), ...extra })
const open = (url: string) => window.history.replaceState(null, '', url)

afterEach(() => { cleanup(); login.mockClear() })

describe('SessionGate — entrada antes de haver sessão', () => {
  it('verificando: anuncia "Verificando acesso…" e reserva o espaço da ação', () => {
    const { container } = render(<SessionGate session={session({ kind: 'loading' })} />)
    expect(screen.getByRole('heading', { name: 'Verificando acesso…' })).toBeTruthy()
    expect(screen.getByRole('status').textContent).toContain('conferindo')
    expect(container.querySelector('.session-gate__actions')).toBeTruthy()
    expect(screen.queryByRole('button')).toBeNull()
  })

  it('consulta lenta: mensagem útil e "Tentar novamente" (sem declarar sessão expirada)', () => {
    const retry = vi.fn()
    render(<SessionGate session={session({ kind: 'loading' }, { slow: true, retry })} />)
    expect(screen.getByRole('status').textContent).toContain('demorando')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(retry).toHaveBeenCalledOnce()
    expect(screen.queryByRole('button', { name: 'Entrar' })).toBeNull()
  })

  it('sem sessão em link direto: diz o destino e oferece "Entrar"', () => {
    open('/agenda?competencia=2026-09')
    render(<SessionGate session={session({ kind: 'anonymous' })} />)
    expect(screen.getByText('Entre para abrir a Agenda.')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Entrar' }))
    expect(login).toHaveBeenCalledOnce()
  })

  it('sem sessão na raiz ou em endereço desconhecido: mensagem genérica', () => {
    open('/')
    const { unmount } = render(<SessionGate session={session({ kind: 'anonymous' })} />)
    expect(screen.getByText('Entre para acessar o seu orçamento.')).toBeTruthy()
    unmount()
    open('/nao-existe')
    render(<SessionGate session={session({ kind: 'anonymous' })} />)
    expect(screen.getByText('Entre para acessar o seu orçamento.')).toBeTruthy()
  })

  it('falha de consulta é diferente de sessão expirada e é recuperável', () => {
    const retry = vi.fn()
    render(<SessionGate session={session({ kind: 'error', message: 'HTTP 503' }, { retry })} />)
    expect(screen.getByRole('alert').textContent).toContain('não significa que ela tenha expirado')
    expect(screen.queryByRole('button', { name: 'Entrar' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(retry).toHaveBeenCalledOnce()
  })
})
