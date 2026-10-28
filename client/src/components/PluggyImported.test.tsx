import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PluggyImported } from './PluggyImported'
import type { ImportedPage, ImportedTransaction, PluggyAccount, PluggyConnection } from '../api/pluggyPoc'

const api = vi.hoisted(() => ({ listImported: vi.fn(), syncConnection: vi.fn(), listSyncRuns: vi.fn(), getStability: vi.fn() }))
vi.mock('../api/pluggyPoc', () => api)

const connection = { id: 'conn-1', label: 'Itaú PF', institution: 'MeuPluggy' } as PluggyConnection
const accounts = [{ id: 'bank-1', name: 'Conta corrente', marketingName: null, maskedNumber: '••••6789' }, { id: 'card-1', name: 'Cartão', marketingName: null, maskedNumber: '••••2222' }] as PluggyAccount[]

const tx = (over: Partial<ImportedTransaction> = {}): ImportedTransaction => ({
  id: 't1', connectionId: 'conn-1', accountId: 'bank-1', accountType: 'BANK', accountLabel: 'Conta corrente ••••6789', pluggyTransactionId: 'ext-1', date: '2026-10-03T12:00:00+00:00',
  description: 'MERCADO', descriptionRaw: 'MERCADO LTDA', amount: '-10.25', currency: 'BRL', type: 'DEBIT', status: 'Pending', operationType: 'PIX', category: null, installmentNumber: null,
  totalInstallments: null, billId: null, cardLast4: null, classification: 'PotentialExpense', classificationReason: 'BankDebit:PIX', firstImportedAt: '2026-10-28T10:00:00+00:00',
  lastObservedAt: '2026-10-28T11:30:00+00:00', lastChangedAt: null, missingSyncCount: 0, possibleDuplicateKey: null, possibleDuplicateCount: 1, isNewInLastSync: false, changedInLastSync: false, ...over,
})
const page = (items: ImportedTransaction[], over: Partial<ImportedPage> = {}): ImportedPage => ({
  items, total: items.length, page: 1, pageSize: 25, counters: { total: items.length, new: 1, updated: 2, pending: 3, missing: 0, lastSyncAt: '2026-10-28T11:30:00+00:00' }, ...over,
})
const run = { id: 'r1', connectionId: 'conn-1', startedAt: '2026-10-28T11:30:00+00:00', finishedAt: '2026-10-28T11:31:00+00:00', status: 'CompletedWithErrors', accountsTotal: 2, accountsSynced: 1,
  seen: 8, created: 5, updated: 2, unchanged: 1, missing: 0, errors: 1, errorSummary: 'RateLimited:transactions' } as const

describe('PluggyImported', () => {
  afterEach(cleanup)
  beforeEach(() => {
    vi.clearAllMocks()
    api.listImported.mockResolvedValue(page([tx()]))
    api.listSyncRuns.mockResolvedValue([])
  })

  it('mostra banco, conta, valor, status, classificação sugerida e contadores', async () => {
    api.listImported.mockResolvedValue(page([tx({ isNewInLastSync: true, possibleDuplicateKey: 'abc123def456', possibleDuplicateCount: 2, missingSyncCount: 1 })]))
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)

    const row = (await screen.findByText('MERCADO')).closest('tr')!
    expect(within(row).getByText(/MeuPluggy · Conta corrente ••••6789/)).toBeTruthy()
    expect(within(row).getByText('-R$ 10,25')).toBeTruthy()
    expect(within(row).getByText('Pendente')).toBeTruthy()
    expect(within(row).getByText('Despesa potencial')).toBeTruthy()
    expect(within(row).getByText(/Possível duplicidade/)).toBeTruthy()
    expect(within(row).getByText(/abc123def456/)).toBeTruthy()
    expect(within(row).getByText(/Nova · Ausente há 1 coleta/)).toBeTruthy()
    const counters = screen.getByRole('list', { name: 'Contadores' })
    expect(within(counters).getByText('Total: 1')).toBeTruthy()
    expect(within(counters).getByText('Novas na última sincronização: 1')).toBeTruthy()
    expect(within(counters).getByText('Atualizadas na última sincronização: 2')).toBeTruthy()
    expect(within(counters).getByText('Pendentes: 3')).toBeTruthy()
    expect(screen.getByText(/não criam lançamentos, não atualizam saldos nem geram previsões/)).toBeTruthy()
  })

  it('exibe parcela e rótulos em português para cada classificação', async () => {
    api.listImported.mockResolvedValue(page([
      tx({ id: 'a', description: 'TV', classification: 'CardInstallment', installmentNumber: 2, totalInstallments: 10, status: 'Posted' }),
      tx({ id: 'b', description: 'FATURA', classification: 'PotentialTransfer', classificationReason: 'CardBillPayment' }),
      tx({ id: 'c', description: 'ESTORNO', classification: 'InsufficientInformation', amount: null }),
    ]))
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)
    expect(await screen.findByText(/parcela 2\/10/)).toBeTruthy()
    // Os mesmos rótulos existem como opções do filtro; a verificação é feita só na tabela.
    const table = within(screen.getByRole('table'))
    expect(table.getByText('Parcela de cartão')).toBeTruthy()
    expect(table.getByText('Transferência potencial')).toBeTruthy()
    expect(table.getByText('Informação insuficiente')).toBeTruthy()
    expect(table.getAllByText('Não informado').length).toBeGreaterThan(0)
    expect(table.getByText('Efetivada')).toBeTruthy()
  })

  it('envia os filtros de conta, período, status e classificação para a API', async () => {
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)
    await screen.findByText('MERCADO')
    fireEvent.change(screen.getByLabelText('Conta'), { target: { value: 'card-1' } })
    fireEvent.change(screen.getByLabelText('De'), { target: { value: '2026-10-01' } })
    fireEvent.change(screen.getByLabelText('Até'), { target: { value: '2026-10-31' } })
    fireEvent.change(screen.getByLabelText('Status'), { target: { value: 'Pending' } })
    fireEvent.change(screen.getByLabelText('Classificação'), { target: { value: 'CardPurchase' } })
    fireEvent.click(screen.getByRole('button', { name: 'Filtrar' }))
    await waitFor(() => expect(api.listImported).toHaveBeenLastCalledWith('w1',
      { connectionId: 'conn-1', accountId: 'card-1', from: '2026-10-01', to: '2026-10-31', status: 'Pending', classification: 'CardPurchase', page: 1, pageSize: 25 }, expect.anything()))
  })

  it('pagina os resultados', async () => {
    api.listImported.mockResolvedValue(page([tx()], { total: 60, pageSize: 25 }))
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)
    await screen.findByText(/página 1 de 3/)
    expect((screen.getByRole('button', { name: 'Anterior' }) as HTMLButtonElement).disabled).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Próxima' }))
    await waitFor(() => expect(api.listImported).toHaveBeenLastCalledWith('w1', expect.objectContaining({ page: 2 }), expect.anything()))
  })

  it('sincroniza, mostra o resultado agregado e recarrega a lista', async () => {
    api.syncConnection.mockResolvedValue(run)
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)
    await screen.findByText('MERCADO')
    fireEvent.click(screen.getByRole('button', { name: 'Sincronizar agora' }))

    const status = await screen.findByRole('status')
    expect(status.textContent).toContain('Concluída com erros')
    expect(status.textContent).toContain('5 novas, 2 atualizadas, 1 inalteradas, 0 ausentes, 1 erros (1/2 contas)')
    expect(status.textContent).toContain('RateLimited:transactions')
    expect(api.syncConnection).toHaveBeenCalledWith('w1', 'conn-1')
    await waitFor(() => expect(api.listImported).toHaveBeenCalledTimes(2))
  })

  it('mostra erro de sincronização (ex.: sincronização simultânea) sem perder a lista', async () => {
    api.syncConnection.mockRejectedValue(new Error('Já existe uma sincronização em andamento para esta conexão.'))
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)
    await screen.findByText('MERCADO')
    fireEvent.click(screen.getByRole('button', { name: 'Sincronizar agora' }))
    expect((await screen.findByRole('alert')).textContent).toContain('Já existe uma sincronização em andamento')
    expect(screen.getByText('MERCADO')).toBeTruthy()
  })

  it('orienta quando ainda não há nada importado', async () => {
    api.listImported.mockResolvedValue(page([], { counters: { total: 0, new: 0, updated: 0, pending: 0, missing: 0, lastSyncAt: null } }))
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)
    expect(await screen.findByText(/Nenhuma transação importada/)).toBeTruthy()
  })

  it('compara sincronizações e mostra as evidências de estabilidade', async () => {
    api.getStability.mockResolvedValue({
      connectionId: 'conn-1', completedRuns: 2, previousRunAt: null, latestRunAt: null, previouslyImported: 100, preservedInLatest: 98, notSeenInLatest: 2, newInLatest: 4,
      newWithDateBeforePreviousRun: 1, changedFieldsInLatest: { status: 3, amount: 1 }, pendingToPostedInLatest: 3, otherStatusChangesInLatest: 0, pendingToPostedTotal: 3,
      amountChangesTotal: 1, descriptionChangesTotal: 0, futureDated: 5, futureDatedPending: 4, missingNow: 2, pendingNow: 6, possibleDuplicateGroups: 1, notes: ['Nenhuma transação é excluída.'],
    })
    render(<PluggyImported workspaceId="w1" connection={connection} accounts={accounts} />)
    await screen.findByText('MERCADO')
    fireEvent.click(screen.getByRole('button', { name: 'Comparar sincronizações' }))
    const list = await screen.findByRole('list', { name: 'Estabilidade' })
    expect(within(list).getByText(/IDs preservados na última: 98 de 100/)).toBeTruthy()
    expect(within(list).getByText(/PENDING → POSTED: 3 na última, 3 no total/)).toBeTruthy()
    expect(within(list).getByText(/status \(3\), amount \(1\)/)).toBeTruthy()
    expect(within(list).getByText(/Com data futura: 5 \(pendentes: 4\)/)).toBeTruthy()
  })
})
