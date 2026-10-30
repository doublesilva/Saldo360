import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PersonalFinance } from './PersonalFinance'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('Personal finance shell', () => {
  it('re-enters the same server-authorized Personal after logout/remount without persisting a client-selected context', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input); calls.push(url)
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url === '/api/me/financial-workspace') return Response.json({ id: 'personal-a', ownerUserId: 'a', name: 'Existente', type: 'Personal', isActive: true })
      return new Response('{}', { status: 503 })
    }))
    const first = render(<PersonalFinance />)
    await screen.findByRole('heading', { name: 'Existente' })
    first.unmount()
    render(<PersonalFinance />)
    await screen.findByRole('heading', { name: 'Existente' })
    await waitFor(() => expect(calls.filter(url => url.includes('personal-a/financial-dashboard/overview?'))).toHaveLength(2))
    expect(calls.filter(url => url === '/api/me/financial-workspace')).toHaveLength(2)
    expect(calls.some(url => url === '/api/families')).toBe(false)
    expect(screen.getByText('Orçamento pessoal')).toBeTruthy()
  })

  it('does not show or query the old workspace after switching authenticated identity', async () => {
    const calls: string[] = []
    let current = 'a'
    let old!: (value: Response) => void
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input); calls.push(url)
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url === '/api/me/financial-workspace') {
        if (current === 'a') return new Promise<Response>(resolve => { old = resolve })
        return Response.json({ id: 'personal-b', ownerUserId: 'b', name: 'Orçamento B', type: 'Personal', isActive: true })
      }
      return new Response('{}', { status: 503 })
    }))
    const view = render(<PersonalFinance key="issuer|a" />)
    await waitFor(() => expect(old).toBeDefined())
    current = 'b'; view.rerender(<PersonalFinance key="issuer|b" />)
    await screen.findByRole('heading', { name: 'Orçamento B' })
    old(Response.json({ id: 'personal-a', ownerUserId: 'a', name: 'Orçamento A', type: 'Personal', isActive: true }))
    await waitFor(() => expect(calls.some(url => url.includes('personal-b/financial-dashboard/overview'))).toBe(true))
    expect(screen.queryByRole('heading', { name: 'Orçamento A' })).toBeNull()
    expect(calls.some(url => url.includes('/personal-a/'))).toBe(false)
  })

  it('keeps the user area available when bootstrap fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => String(input) === '/auth/csrf' ? Response.json({ token: 'csrf' }) : new Response('{}', { status: 503 })))
    render(<PersonalFinance userArea={<button>Sair</button>} />)
    await screen.findByRole('alert')
    expect(screen.getByRole('button', { name: 'Sair' })).toBeTruthy()
  })

  it('keeps the Visão Geral filters (competência e conta) across module navigation', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input); calls.push(url)
      if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
      if (url === '/api/me/financial-workspace') return Response.json({ id: 'personal-a', ownerUserId: 'a', name: 'Personal', type: 'Personal', isActive: true })
      return new Response('{}', { status: 503 })
    }))
    render(<PersonalFinance />)
    const competence = await screen.findByLabelText('Competência')
    fireEvent.change(competence, { target: { value: '2026-11' } })
    fireEvent.change(screen.getByLabelText('Conta ou consolidado'), { target: { value: 'all' } })
    await waitFor(() => expect(calls.some(url => url.includes('/financial-dashboard/overview') && url.includes('competence=2026-11') && url.includes('scope=all'))).toBe(true))
    fireEvent.click(screen.getByRole('button', { name: 'Configurações' }))
    fireEvent.click(screen.getByRole('button', { name: 'Visão Geral' }))
    expect((await screen.findByLabelText('Competência') as HTMLInputElement).value).toBe('2026-11')
    expect((screen.getByLabelText('Conta ou consolidado') as HTMLSelectElement).value).toBe('all')
    expect(screen.getAllByLabelText('Competência')).toHaveLength(1)
  })

  it('opens the personal workspace without consulting a family and keeps social UI separate', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input); calls.push(url)
      if (url === '/auth/csrf') return new Response(JSON.stringify({ token: 'csrf' }))
      if (url === '/api/me/financial-workspace') {
        expect(init?.method).toBe('POST')
        return new Response(JSON.stringify({ id: 'personal-a', ownerUserId: 'a', name: 'Meu orçamento', type: 'Personal', isActive: true }))
      }
      if (url === '/api/families') return new Response('[]')
      return new Response('{}', { status: 503 })
    }))
    render(<PersonalFinance />)
    await screen.findByRole('heading', { name: 'Meu orçamento' })
    await waitFor(() => expect(calls.some(url => url.startsWith('/api/financial-workspaces/personal-a/'))).toBe(true))
    expect(calls.some(url => url.startsWith('/api/families'))).toBe(false)
    expect(screen.queryByText('Shared')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Família (social)' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Configurações' }))
    expect(calls.some(url => url.startsWith('/api/families'))).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Gerenciar famílias e integrantes' }))
    await screen.findByText('Vínculos familiares não compartilham seu orçamento pessoal.')
    await waitFor(() => expect(calls).toContain('/api/families'))
    expect(calls.some(url => /\/api\/families\/[^/]+\/(accounts|financial-dashboard)/.test(url))).toBe(false)
  })

  it('does not mount financial data when ensure fails', async () => {
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input); calls.push(url)
      return url === '/auth/csrf' ? new Response(JSON.stringify({ token: 'csrf' })) : new Response('{}', { status: 409 })
    }))
    render(<PersonalFinance />)
    await screen.findByRole('alert')
    expect(calls.some(url => url.startsWith('/api/financial-workspaces/'))).toBe(false)
    expect(screen.queryByRole('button', { name: 'Contas' })).toBeNull()
  })
})
