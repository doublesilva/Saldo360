import { afterEach, expect, it, vi } from 'vitest'
import { createInvitation, revokeInvitation } from './invitations'

afterEach(() => vi.unstubAllGlobals())

it('keeps family invitations on social routes, never on financial workspaces', async () => {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input); calls.push(path)
    if (path === '/auth/csrf') return new Response(JSON.stringify({ token: 'csrf' }))
    if (init?.method === 'DELETE') return new Response(null, { status: 204 })
    return new Response(JSON.stringify({ id: 'invite', token: 'synthetic', expiresAt: '2026-10-30T00:00:00Z' }), { status: 201 })
  }))
  await createInvitation('social-family')
  await revokeInvitation('social-family', 'invite')
  expect(calls).toContain('/api/families/social-family/invitations')
  expect(calls).toContain('/api/families/social-family/invitations/invite')
  expect(calls.some(path => path.includes('financial-workspaces'))).toBe(false)
})
