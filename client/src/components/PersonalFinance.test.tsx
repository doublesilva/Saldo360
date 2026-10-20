import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PersonalFinance } from './PersonalFinance'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('Personal finance shell', () => {
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
    fireEvent.click(screen.getByRole('button', { name: 'Família (social)' }))
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
