import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SyncConnection, SyncMonitor } from '../api/pluggySyncStatus'
import { PluggySyncPanel } from './PluggySyncPanel'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const json = (value: unknown, status = 200) => Promise.resolve(new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } }))
const text = (element: Element | null) => (element?.textContent ?? '').replace(/ /g, ' ')

const connection = (overrides: Partial<SyncConnection> = {}): SyncConnection => ({
  connectionId: 'c1', label: 'Itaú PF', institution: 'MeuPluggy', integratedAccounts: 4, status: 'Completed',
  lastAttemptAt: '2026-10-29T10:30:00Z', lastSuccessAt: '2026-10-29T10:30:00Z', nextAttemptAt: '2026-10-30T10:30:00Z', lastTrigger: 'Scheduled', lastDurationSeconds: 75,
  lastCreated: 12, lastUpdated: 3, lastError: null, consecutiveFailures: 0, consentExpiresAt: null, consentExpiringSoon: false, sourceUpdatedAt: null,
  recentRuns: [
    { id: 'r2', startedAt: '2026-10-29T10:30:00Z', finishedAt: '2026-10-29T10:31:15Z', status: 'Completed', created: 12, updated: 3, errors: 0, errorSummary: null, trigger: 'Scheduled' },
    { id: 'r1', startedAt: '2026-10-28T10:30:00Z', finishedAt: '2026-10-28T10:31:00Z', status: 'Failed', created: 0, updated: 0, errors: 1, errorSummary: 'RateLimited:transactions', trigger: 'Manual' },
  ],
  ...overrides,
})
const monitor = (connections: SyncConnection[], enabled = true): SyncMonitor => ({
  now: '2026-10-29T12:00:00Z', auto: { enabled, intervalHours: 24, windowStartHour: 0, windowEndHour: 24, maxConnectionsPerRun: 5, pollIntervalMinutes: 15 }, connections,
})

describe('PluggySyncPanel', () => {
  it('mostra o estado, a última atualização, a próxima sincronização, a duração e o histórico resumido', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(monitor([connection()]))))
    render(<PluggySyncPanel workspaceId="w" />)

    const item = await screen.findByRole('listitem', { name: 'Sincronização de Itaú PF' })
    expect(within(item).getByText('Sincronização concluída', { selector: '.badge' })).toBeTruthy()
    expect(text(within(item).getByText('Próxima sincronização').nextElementSibling)).toBe('30/10/2026 07:30')
    expect(text(within(item).getByText('Última sincronização bem-sucedida').nextElementSibling)).toBe('29/10/2026 07:30')
    expect(text(within(item).getByText('Última execução').nextElementSibling)).toBe('1 min 15 s · 12 novo(s), 3 atualizado(s)')
    expect(screen.getByText(/Sincronização automática ligada: a cada 24 h/)).toBeTruthy()
    fireEvent.click(within(item).getByText('Últimas execuções (2)'))
    const rows = within(item).getAllByRole('row')
    expect(text(rows[1])).toContain('Automática')
    expect(text(rows[2])).toContain('Manual')
    expect(text(rows[2])).toContain('limite de requisições da Pluggy') // código curto vira frase; nada técnico ou sensível
  })

  it('distingue autorização expirada, vencimento próximo, falha e a instituição sem dados novos', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(monitor([
      connection({ connectionId: 'a', label: 'Expirada', status: 'ConsentExpired', lastError: 'ConsentExpired', nextAttemptAt: null }),
      connection({ connectionId: 'b', label: 'Quase', consentExpiringSoon: true, consentExpiresAt: '2026-11-02T03:00:00Z' }),
      connection({ connectionId: 'c', label: 'Falhou', status: 'Failed', lastError: 'Timeout:transactions', consecutiveFailures: 2 }),
      connection({ connectionId: 'd', label: 'Parada', status: 'SourceNotUpdated' }),
      connection({ connectionId: 'e', label: 'Parcial', status: 'CompletedWithErrors', lastError: 'Unavailable:bills' }),
    ]))))
    render(<PluggySyncPanel workspaceId="w" />)

    await screen.findByRole('listitem', { name: 'Sincronização de Expirada' })
    expect(within(screen.getByRole('listitem', { name: 'Sincronização de Expirada' })).getByRole('alert').textContent).toContain('autorização desta conexão expirou')
    expect(text(screen.getByRole('listitem', { name: 'Sincronização de Quase' }))).toContain('A autorização vence em 02/11/2026')
    const failed = screen.getByRole('listitem', { name: 'Sincronização de Falhou' })
    expect(within(failed).getByText('Falha na sincronização', { selector: '.badge' })).toBeTruthy()
    expect(text(failed)).toContain('a Pluggy demorou a responder (2 falhas seguidas)')
    expect(text(screen.getByRole('listitem', { name: 'Sincronização de Parada' }))).toContain('A instituição ainda não trouxe dados novos')
    expect(text(screen.getByRole('listitem', { name: 'Sincronização de Parada' }))).toContain('o Saldo360 verifica de novo sozinho') // automática ligada
    expect(within(screen.getByRole('listitem', { name: 'Sincronização de Parcial' })).getByText('Concluída com erros', { selector: '.badge' })).toBeTruthy()
  })

  it('"Sincronizar agora" usa a sincronização existente, mostra o progresso e recarrega o estado', async () => {
    let state = monitor([connection({ lastCreated: 0 })])
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (url === '/auth/csrf') return json({ token: 'csrf' })
      if (url.endsWith('/connections/c1/sync') && init?.method === 'POST') { state = monitor([connection({ lastCreated: 9, lastTrigger: 'Manual' })]); return json({ id: 'r3', status: 'Completed' }) }
      return json(state)
    })
    vi.stubGlobal('fetch', fetchMock)
    const changed = vi.fn()
    render(<PluggySyncPanel workspaceId="w" onChanged={changed} />)
    const item = await screen.findByRole('listitem', { name: 'Sincronização de Itaú PF' })

    fireEvent.click(within(item).getByRole('button', { name: 'Sincronizar agora' }))

    await waitFor(() => expect(text(screen.getByText('Última execução').nextElementSibling)).toContain('9 novo(s)'))
    const post = fetchMock.mock.calls.find(call => call[1]?.method === 'POST' && String(call[0]).endsWith('/sync'))!
    expect(String(post[0])).toBe('/api/financial-workspaces/w/pluggy-poc/connections/c1/sync')
    expect((post[1]!.headers as Record<string, string>)['X-CSRF-TOKEN']).toBe('csrf')
    expect(changed).toHaveBeenCalled()
  })

  it('mostra o conflito quando já há sincronização em andamento e não esconde o erro', async () => {
    vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => {
      if (url === '/auth/csrf') return json({ token: 'csrf' })
      if (init?.method === 'POST') return json({ title: 'Conflict', status: 409, detail: 'Já existe uma sincronização em andamento para esta conexão.' }, 409)
      return json(monitor([connection()]))
    }))
    render(<PluggySyncPanel workspaceId="w" />)
    const item = await screen.findByRole('listitem', { name: 'Sincronização de Itaú PF' })

    fireEvent.click(within(item).getByRole('button', { name: 'Sincronizar agora' }))

    // A lista recarrega depois do clique (o cartão é remontado): a mensagem continua visível.
    expect((await screen.findByRole('alert')).textContent).toContain('sincronização em andamento')
  })

  it('com a automática desligada não inventa próxima execução; sem conexões nada aparece', async () => {
    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(monitor([connection({ nextAttemptAt: null, lastTrigger: 'Manual' })], false))))
    const { unmount } = render(<PluggySyncPanel workspaceId="w" />)
    const item = await screen.findByRole('listitem', { name: 'Sincronização de Itaú PF' })
    expect(text(within(item).getByText('Próxima sincronização').nextElementSibling)).toBe('Automática desligada')
    expect(screen.getByText(/Sincronização automática desligada/)).toBeTruthy()
    unmount()

    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(monitor([connection({ status: 'SourceNotUpdated', nextAttemptAt: null })], false))))
    const waiting = render(<PluggySyncPanel workspaceId="w" />)
    expect(text(await screen.findByRole('listitem', { name: 'Sincronização de Itaú PF' }))).toContain('A sincronização automática está desligada: use "Sincronizar agora"')
    expect(text(screen.getByRole('listitem', { name: 'Sincronização de Itaú PF' }))).not.toContain('verifica de novo sozinho')
    waiting.unmount()

    vi.stubGlobal('fetch', vi.fn((_url: string, _init?: RequestInit) => json(monitor([]))))
    const { container } = render(<PluggySyncPanel workspaceId="w" />)
    await waitFor(() => expect(container.querySelector('.pluggy-sync')).toBeNull())
  })
})
