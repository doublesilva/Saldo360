import { afterEach, expect, it, vi } from 'vitest'
import { previewFinancialReset } from './financialReset'

afterEach(() => vi.unstubAllGlobals())

// Exercise the shared response reader through a simulated GET only: no real reset or database.
it.each([
  ['DocumentCutoverRequired', 'cutover dos arquivos'],
  ['PersonalOnly', 'somente para orçamento pessoal'],
  ['WorkspaceInactive', 'orçamento está inativo'],
])('preserves the explicit rejection reason %s', async (code, expected) => {
  const fetchMock = vi.fn(async () => new Response(JSON.stringify({ code }), { status: 409 }))
  vi.stubGlobal('fetch', fetchMock)
  await expect(previewFinancialReset('synthetic')).rejects.toThrow(expected)
  expect(fetchMock).toHaveBeenCalledTimes(1)
})

it.each(['', 'not-json', '{}', '{"code":"Unknown"}', 'null'])(
  'retains a safe fallback for unavailable or unknown conflict bodies: %s', async body => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(body, { status: 409 })))
    await expect(previewFinancialReset('synthetic')).rejects.toThrow('Recarregue a prévia')
  },
)

it('does not display untrusted server messages or internal details', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ code: 'Unknown', message: 'SQL internal detail' }), { status: 409 })))
  await expect(previewFinancialReset('synthetic')).rejects.not.toThrow('SQL internal detail')
})
