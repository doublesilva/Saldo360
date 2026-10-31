import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { PlanActualDashboard } from '../../api/planActual'
import type { OverdraftRecovery } from '../../api/overdraftRecovery'
import { OverviewRecovery } from './OverviewRecovery'

const account = (id: string, name: string, contractedLimit: string | null) => ({ accountId: id, name, contractedLimit })
const dashboard = (accounts: ReturnType<typeof account>[], accountId: string | null = null) =>
  ({ competence: '2026-10', scope: { kind: accountId ? 'Account' : 'All', accountId }, overdraft: { eligible: true, accounts } }) as unknown as PlanActualDashboard

const recovery = (over: Partial<OverdraftRecovery> = {}): OverdraftRecovery => ({
  accountId: 'a1', accountName: 'Itaú', competence: '2026-10', periodKind: 'Current', state: 'PartialRecovery', contractedLimit: '20000.00',
  openingUsed: '15000.00', currentUsed: '13000.00', available: '7000.00', aboveLimit: '0.00', target: '2500.00', recovered: '2000.00',
  projectedRecovery: '1800.00', deviation: '-700.00', positiveBalance: null, referenceDate: '2026-09-30', capturedAt: '2026-10-01T12:00:00Z',
  updatedAt: '2026-10-20T12:00:00Z', reasons: [], isFinalResult: false, lateReference: false, attainmentPercent: '80.0', balanceAsOf: '2026-10-20', ...over,
})

function mock(responder: (url: string, init?: RequestInit) => Response) {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    return url === '/auth/csrf' ? Response.json({ token: 't' }) : responder(url, init)
  }))
}
const validation = (field: string, message: string) => Response.json({ status: 400, errors: { [field]: [message] } }, { status: 400 })
const noop = () => undefined
const single = (limit: string | null = '20000.00') => dashboard([account('a1', 'Itaú', limit)], 'a1')

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('OverviewRecovery', () => {
  it('mostra o exemplo de aceite com textos em português, sem enum cru', async () => {
    mock(() => Response.json(recovery()))
    render(<OverviewRecovery workspaceId="w" data={single()} onAccounts={noop} onProjection={noop} />)
    expect(await screen.findByText(/Recuperação parcial/)).toBeTruthy()
    expect(screen.queryByText(/PartialRecovery/)).toBeNull()
    expect(screen.getByText('Meta original (fixa)').nextSibling?.textContent).toContain('2.500,00')
    expect(screen.getByText(/Atingimento da meta: 80,0%/)).toBeTruthy()
  })

  it('mês encerrado mostra resultado final e não projeção', async () => {
    mock(() => Response.json(recovery({ isFinalResult: true, periodKind: 'Past', projectedRecovery: null, deviation: null })))
    render(<OverviewRecovery workspaceId="w" data={single()} onAccounts={noop} onProjection={noop} />)
    expect(await screen.findByText(/Mês encerrado \(resultado final\)/)).toBeTruthy()
    expect(screen.queryByText('Recuperação projetada')).toBeNull()
  })

  it('meta negativa mostra piora prevista e, sem valor a recuperar, compara em reais sem percentual', async () => {
    mock(() => Response.json(recovery({ target: '-300.00', attainmentPercent: null, state: 'Worsening' })))
    render(<OverviewRecovery workspaceId="w" data={single()} onAccounts={noop} onProjection={noop} />)
    expect(await screen.findByText(/Piora prevista de/)).toBeTruthy()
    expect(screen.getByText(/comparação em reais/)).toBeTruthy()
    expect(screen.queryByText(/Atingimento da meta/)).toBeNull()
  })

  it('atingimento acima de 100% mostra o valor real', async () => {
    mock(() => Response.json(recovery({ attainmentPercent: '140.0' })))
    render(<OverviewRecovery workspaceId="w" data={single()} onAccounts={noop} onProjection={noop} />)
    expect(await screen.findByText(/Atingimento da meta: 140,0%/)).toBeTruthy()
  })

  it('referência tardia é explicitada', async () => {
    mock(() => Response.json(recovery({ lateReference: true, capturedAt: '2026-10-15T12:00:00Z' })))
    render(<OverviewRecovery workspaceId="w" data={single()} onAccounts={noop} onProjection={noop} />)
    expect(await screen.findByText(/Referência tardia/)).toBeTruthy()
  })

  it('saldo positivo aparece como saldo positivo e limite totalmente recuperado', async () => {
    mock(() => Response.json(recovery({ state: 'FullyRecovered', currentUsed: '0.00', recovered: '15000.00', positiveBalance: '500.00' })))
    render(<OverviewRecovery workspaceId="w" data={single()} onAccounts={noop} onProjection={noop} />)
    expect(await screen.findByText(/Limite totalmente recuperado/)).toBeTruthy()
    expect(screen.getByText(/Saldo positivo de/).textContent).toContain('500,00')
  })

  it('sem meta registrada oferece registrar; sem limite oferece informar o limite', async () => {
    const onAccounts = vi.fn()
    mock((_url, init) => init?.method === 'POST' ? Response.json(recovery()) : validation('snapshot', 'A meta do mês ainda não foi registrada.'))
    render(<OverviewRecovery workspaceId="w" data={single(null)} onAccounts={onAccounts} onProjection={noop} />)
    expect(await screen.findByText(/Sem meta registrada/)).toBeTruthy()
    expect(screen.getByText(/Sem limite informado/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Informar limite' }))
    expect(onAccounts).toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Registrar meta do mês' }))
    await waitFor(() => expect(screen.getByText(/Recuperação parcial/)).toBeTruthy())
  })

  it('saldo indisponível e falha de carga aparecem como mensagem no card, sem esconder as ações', async () => {
    mock(() => validation('balance', 'Saldo indisponível para acompanhar a recuperação.'))
    const first = render(<OverviewRecovery workspaceId="w" data={single('1.00')} onAccounts={noop} onProjection={noop} />)
    expect((await screen.findByRole('alert')).textContent).toContain('Saldo indisponível')
    expect(screen.getByRole('button', { name: 'Ver projeção' })).toBeTruthy()
    first.unmount()
    mock(() => { throw new TypeError('offline') })
    render(<OverviewRecovery workspaceId="w" data={single('1.00')} onAccounts={noop} onProjection={noop} />)
    expect((await screen.findByRole('alert')).textContent).toContain('Não foi possível carregar a recuperação')
  })

  it('todas as contas: lista cada conta separadamente e avisa que não há compensação', async () => {
    mock(url => Response.json(recovery({ accountId: url.includes('a2') ? 'a2' : 'a1' })))
    render(<OverviewRecovery workspaceId="w" data={dashboard([account('a1', 'Itaú PF', '1000.00'), account('a2', 'Banco PJ com um nome bem longo', '2000.00')])} onAccounts={noop} onProjection={noop} />)
    expect(await screen.findByText('Itaú PF')).toBeTruthy()
    expect(screen.getByText('Banco PJ com um nome bem longo')).toBeTruthy()
    expect(screen.getByText(/não se compensam entre contas/)).toBeTruthy()
  })
})
