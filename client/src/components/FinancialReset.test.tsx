import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { FinancialReset } from './FinancialReset'
import { PersonalFinance } from './PersonalFinance'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })
function api(status = 200) {
  const calls: { url: string; init?: RequestInit }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input); calls.push({ url, init })
    if (url === '/auth/csrf') return new Response(JSON.stringify({ token: 'csrf' }))
    if (url === '/api/me/financial-workspace') return new Response(JSON.stringify({ id: 'w', ownerUserId: 'a', name: 'Personal', type: 'Personal', isActive: true }))
    if (url.endsWith('/financial-reset/preview')) return new Response(JSON.stringify({ workspaceId: 'w', status: 'Ready', counts: { accounts: 2, transactions: 4, documentInboxItems: 1 } }))
    if (url.endsWith('/financial-reset')) return new Response(JSON.stringify({ workspaceId: 'w', status: 'ResetCompleted', deleted: { accounts: 2 } }), { status })
    if (url.endsWith('/accounts')) return new Response('[]')
    return new Response('{}', { status: 503 })
  }))
  return calls
}
async function open() { fireEvent.click(screen.getByRole('button', { name: 'Resetar orçamento' })); return screen.findByRole('textbox', { name: 'Digite APAGAR ORÇAMENTO' }) }
it('shows counts without values, focuses confirmation and requires exact text; cancellation writes nothing', async () => {
  const calls = api(); render(<FinancialReset workspaceId="w" onReset={vi.fn()} />)
  const input = await open()
  await waitFor(() => expect(document.activeElement).toBe(input))
  expect(screen.getByText('Contas')).toBeTruthy(); expect(screen.getByText('4')).toBeTruthy()
  const submit = screen.getByRole('button', { name: 'Apagar dados financeiros' }) as HTMLButtonElement
  expect(submit.disabled).toBe(true)
  fireEvent.change(input, { target: { value: 'APAGAR ORÇAMENTO ' } }); expect(submit.disabled).toBe(true)
  fireEvent.change(input, { target: { value: 'APAGAR ORÇAMENTO' } }); expect(submit.disabled).toBe(false)
  fireEvent.click(screen.getByRole('button', { name: 'Cancelar' }))
  expect(calls.some(c => c.init?.method === 'POST' && c.url.endsWith('/financial-reset'))).toBe(false)
  expect(screen.getByRole('button', { name: 'Resetar orçamento' })).toBeTruthy()
})
it.each([409, 500])('does not present success on HTTP %s and invalidates confirmation', async status => {
  api(status); const completed = vi.fn(); render(<FinancialReset workspaceId="w" onReset={completed} />)
  fireEvent.change(await open(), { target: { value: 'APAGAR ORÇAMENTO' } })
  fireEvent.click(screen.getByRole('button', { name: 'Apagar dados financeiros' }))
  await screen.findByRole('alert'); expect(completed).not.toHaveBeenCalled()
  expect((screen.getByRole('button', { name: 'Apagar dados financeiros' }) as HTMLButtonElement).disabled).toBe(true)
})
it('remounts financial state after success, keeps workspace and reconsults the empty home', async () => {
  const calls = api(); render(<PersonalFinance />)
  await screen.findByRole('heading', { name: 'Personal' })
  fireEvent.click(screen.getByRole('button', { name: 'Configurações' }))
  fireEvent.change(await open(), { target: { value: 'APAGAR ORÇAMENTO' } })
  const before = calls.filter(c => c.url.includes('/financial-dashboard?')).length
  fireEvent.click(screen.getByRole('button', { name: 'Apagar dados financeiros' }))
  await screen.findByText(/Orçamento reiniciado/)
  await waitFor(() => expect(calls.filter(c => c.url.includes('/financial-dashboard?')).length).toBeGreaterThan(before))
  expect(calls.filter(c => c.url === '/api/me/financial-workspace')).toHaveLength(1)
  expect(calls.some(c => c.url.includes('/auth/logout'))).toBe(false)
  expect(calls.find(c => c.url.endsWith('/financial-reset'))?.init?.body).toBe(JSON.stringify({ confirmation: 'APAGAR ORÇAMENTO' }))
})
it('waits for the server, blocks repeat submission and supports semantic keyboard form submission', async () => {
  let complete!: (response: Response) => void
  const pending = new Promise<Response>(resolve => { complete = resolve })
  let writes = 0
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input)
    if (url === '/auth/csrf') return new Response(JSON.stringify({ token: 'csrf' }))
    if (url.endsWith('/preview')) return new Response(JSON.stringify({ workspaceId: 'w', status: 'Ready', counts: { accounts: 1 } }))
    writes++; return pending
  }))
  const done = vi.fn(); render(<FinancialReset workspaceId="w" onReset={done} />)
  const input = await open(); fireEvent.change(input, { target: { value: 'APAGAR ORÇAMENTO' } })
  fireEvent.submit(input.closest('form')!)
  await waitFor(() => expect(writes).toBe(1))
  expect(done).not.toHaveBeenCalled()
  expect((screen.getByRole('button', { name: 'Reiniciando…' }) as HTMLButtonElement).disabled).toBe(true)
  expect((screen.getByRole('button', { name: 'Cancelar' }) as HTMLButtonElement).disabled).toBe(true)
  fireEvent.submit(input.closest('form')!); expect(writes).toBe(1)
  complete(new Response(JSON.stringify({ workspaceId: 'w', status: 'ResetCompleted', deleted: { accounts: 1 } })))
  await waitFor(() => expect(done).toHaveBeenCalledWith(expect.objectContaining({ status: 'ResetCompleted' })))
})

it('keeps reset disabled when preview fails and permits an explicit read-only retry', async () => {
  let previews = 0
  vi.stubGlobal('fetch', vi.fn(async () => ++previews === 1 ? new Response('{}', { status: 500 })
    : new Response(JSON.stringify({ workspaceId: 'w', status: 'Ready', counts: { accounts: 0 } }))))
  render(<FinancialReset workspaceId="w" onReset={vi.fn()} />)
  fireEvent.click(screen.getByRole('button', { name: 'Resetar orçamento' }))
  await screen.findByRole('alert')
  expect((screen.getByRole('button', { name: 'Apagar dados financeiros' }) as HTMLButtonElement).disabled).toBe(true)
  fireEvent.click(screen.getByRole('button', { name: 'Recarregar prévia' }))
  await screen.findByRole('textbox', { name: 'Digite APAGAR ORÇAMENTO' })
  expect(previews).toBe(2)
})
