import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PluggyLab } from './PluggyLab'
import { money } from '../pluggyFormat'

const api = vi.hoisted(() => ({
  listConnections: vi.fn(), addConnection: vi.fn(), listPluggyAccounts: vi.fn(), listPluggyTransactions: vi.fn(), listPluggyBills: vi.fn(), getDiagnostics: vi.fn(), getLabStatus: vi.fn(),
}))
vi.mock('../api/pluggyPoc', () => api)

const connection = {
  id: '11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa', label: 'Itaú PF', itemReference: 'aaaaaaaa…', institution: 'Itaú', institutionType: 'PERSONAL_BANK', isOpenFinance: true,
  state: 'ConsentExpired', status: 'LOGIN_ERROR', executionStatus: 'ERROR', lastUpdatedAt: '2026-10-20T10:00:00+00:00', consentExpiresAt: null,
  errorCode: 'INVALID_CREDENTIALS', errorMessage: 'Reautorize', integrationError: null, createdAt: '2026-10-01T00:00:00+00:00',
}
const bank = {
  id: 'bank-1', connectionId: connection.id, type: 'BANK', subtype: 'CHECKING_ACCOUNT', name: 'Conta corrente', marketingName: null, maskedNumber: '••••6789',
  maskedBranchAccount: '••••3456', currency: 'BRL', balance: '1500.7', updatedAt: null, overdraftContractedLimit: '500', overdraftUsedLimit: null, creditLimit: null,
  availableCreditLimit: null, minimumPayment: null, cardBrand: null, cardLevel: null, balanceCloseDate: null, balanceDueDate: null,
}
const card = { ...bank, id: 'card-1', type: 'CREDIT', overdraftContractedLimit: null, balance: '300.1', subtype: 'CREDIT_CARD', name: 'Cartão', maskedNumber: '••••2222', creditLimit: '5000', availableCreditLimit: '4699.90', cardBrand: 'MASTERCARD' }

// O formatador do projeto usa espaço inseparável após "R$".
const brl = (value: string) => value.replace(/R\$ /g, 'R$ ')

describe('PluggyLab', () => {
  afterEach(cleanup)
  beforeEach(() => {
    vi.clearAllMocks()
    api.listConnections.mockResolvedValue([connection])
    api.listPluggyAccounts.mockResolvedValue([bank, card])
    api.getLabStatus.mockResolvedValue({ enabled: true, importEnabled: false })
  })

  it('formata valores sem converter para number e mostra "Não informado" para ausentes', () => {
    expect(money('1500.7', 'BRL')).toBe(brl('R$ 1.500,70'))
    expect(money('-10.25', 'BRL')).toBe(brl('-R$ 10,25'))
    expect(money('5000', null)).toBe(brl('R$ 5.000,00'))
    expect(money('12.5', 'USD')).toBe('USD 12.50')
    expect(money('0.12345', 'BRL')).toBe('0.12345')
    expect(money(null)).toBe('Não informado')
  })

  it('lista conexões com estado, expiração e erro, sem expor o itemId completo', async () => {
    render(<PluggyLab workspaceId="w1" />)
    const row = (await screen.findByText('Itaú')).closest('tr')!
    expect(within(row).getByText(/Autorização expirada/)).toBeTruthy()
    expect(within(row).getByText(/INVALID_CREDENTIALS — Reautorize/)).toBeTruthy()
    expect(within(row).getAllByText('Não informado').length).toBeGreaterThan(0)
    expect(within(row).getByText(/item aaaaaaaa…/)).toBeTruthy()
    expect(screen.getByText(/Nada aqui altera contas, lançamentos/)).toBeTruthy()
  })

  it('cadastra Item apenas com itemId e rótulo e recarrega a lista', async () => {
    api.addConnection.mockResolvedValue(connection)
    render(<PluggyLab workspaceId="w1" />)
    await screen.findByText('Itaú')
    fireEvent.change(screen.getByLabelText(/itemId/), { target: { value: ' 11111111-1111-4111-8111-111111111111 ' } })
    fireEvent.change(screen.getByLabelText('Rótulo'), { target: { value: 'Nubank' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cadastrar Item' }))
    await waitFor(() => expect(api.addConnection).toHaveBeenCalledWith('w1', { itemId: '11111111-1111-4111-8111-111111111111', label: 'Nubank' }))
    await waitFor(() => expect(api.listConnections).toHaveBeenCalledTimes(2))
    expect(screen.queryByLabelText(/senha|password|cpf/i)).toBeNull()
  })

  it('mostra erro de cadastro sem perder o formulário', async () => {
    api.addConnection.mockRejectedValue(new Error('Este Item já está vinculado.'))
    render(<PluggyLab workspaceId="w1" />)
    await screen.findByText('Itaú')
    fireEvent.change(screen.getByLabelText(/itemId/), { target: { value: '11111111-1111-4111-8111-111111111111' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cadastrar Item' }))
    expect((await screen.findByRole('alert')).textContent).toContain('já está vinculado')
  })

  it('inspeciona contas com números mascarados e limites', async () => {
    render(<PluggyLab workspaceId="w1" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Inspecionar' }))
    expect(await screen.findByText('••••6789')).toBeTruthy()
    expect(screen.getAllByText('R$ 1.500,70').length).toBeGreaterThan(0)
    expect(screen.getByText(/Cheque especial R\$ 500,00/)).toBeTruthy()
    expect(screen.getAllByText(/Limite R\$ 5.000,00 · Disponível R\$ 4.699,90/).length).toBeGreaterThan(0)
    expect(api.listPluggyAccounts).toHaveBeenCalledWith('w1', connection.id, expect.anything())
  })

  it('filtra transações por período e carrega a página seguinte por cursor', async () => {
    const tx = (id: string, extra = {}) => ({
      id, date: '2026-10-03T12:00:00+00:00', description: null, descriptionRaw: 'MERCADO', amount: '-10.25', amountInAccountCurrency: null, currency: 'BRL', type: 'DEBIT',
      status: 'PENDING', category: null, balance: null, operationType: null, paymentMethod: null, hasPayer: false, hasReceiver: false, hasReferenceNumber: false, merchant: null,
      merchantCategory: null, installmentNumber: 2, totalInstallments: 10, installmentsTotalAmount: null, billId: null, cardLast4: null, ...extra,
    })
    api.listPluggyTransactions.mockResolvedValueOnce({ items: [tx('t1')], nextCursor: 'abc==' }).mockResolvedValueOnce({ items: [tx('t2', { descriptionRaw: 'PADARIA' })], nextCursor: null })
    render(<PluggyLab workspaceId="w1" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Inspecionar' }))
    await screen.findByText('••••6789')
    fireEvent.change(screen.getByLabelText('De'), { target: { value: '2026-10-01' } })
    fireEvent.change(screen.getByLabelText('Até'), { target: { value: '2026-10-31' } })
    fireEvent.click(screen.getByRole('button', { name: 'Consultar' }))

    expect(await screen.findByText('MERCADO')).toBeTruthy()
    expect(api.listPluggyTransactions).toHaveBeenCalledWith('w1', 'bank-1', { from: '2026-10-01', to: '2026-10-31', after: undefined })
    expect(screen.getByText('2/10')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Carregar mais' }))
    expect(await screen.findByText('PADARIA')).toBeTruthy()
    expect(api.listPluggyTransactions).toHaveBeenLastCalledWith('w1', 'bank-1', { from: '2026-10-01', to: '2026-10-31', after: 'abc==' })
    expect(screen.queryByRole('button', { name: 'Carregar mais' })).toBeNull()
  })

  it('consulta faturas de cartão e exibe erros controlados', async () => {
    api.listPluggyBills.mockResolvedValueOnce({ items: [{ id: 'b1', dueDate: '2026-10-17T00:00:00+00:00', closingDate: '2026-10-10T00:00:00+00:00', payments: [{ amount: '1500.25', currency: 'BRL', paymentDate: '2026-10-16T00:00:00+00:00', paymentMode: 'PIX', valueType: 'TOTAL' }], totalAmount: '1500.25', currency: 'BRL', minimumPayment: null, allowsInstallments: true, financeCharges: [{ type: 'IOF', amount: '3.45', currency: 'BRL', additionalInfo: null }] }], page: 1, totalPages: 1, total: 1 })
    render(<PluggyLab workspaceId="w1" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Inspecionar' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Consultar faturas' }))
    expect(await screen.findByText('R$ 1.500,25')).toBeTruthy()
    expect(screen.getByText(/IOF R\$ 3,45/)).toBeTruthy()

    api.listPluggyBills.mockRejectedValueOnce(new Error('A Pluggy limitou as requisições. Tente novamente em instantes.'))
    fireEvent.click(screen.getByRole('button', { name: 'Consultar faturas' }))
    expect((await screen.findByRole('alert')).textContent).toContain('limitou as requisições')
  })

  it('gera o diagnóstico diferenciando situações dos recursos', async () => {
    const resource = (resource: string, state: string, extra = {}) => ({ resource, state, count: 0, earliestDate: null, latestDate: null, fields: [], limitations: [], errorKind: null, fieldsReturned: [], fieldsMissing: [], ...extra })
    api.getDiagnostics.mockResolvedValue({
      connectionId: connection.id, institution: 'Itaú', state: 'Healthy', lastSync: '2026-10-20T10:00:00+00:00', generatedAt: '2026-10-28T12:00:00+00:00', connectorProducts: ['ACCOUNTS'], notes: ['Retrato único.'],
      itemResources: [resource('Empréstimos', 'NotSupported', { limitations: ['O conector não declara o produto LOANS.'] })],
      accounts: [{ accountId: 'bank-1', name: 'Conta corrente', type: 'BANK', subtype: 'CHECKING_ACCOUNT', updatedAt: null, resources: [
        resource('Transações', 'Available', { count: 5, earliestDate: '2026-01-10T12:00:00+00:00', latestDate: '2026-05-10T12:00:00+00:00', fieldsReturned: ['id', 'amount'], fieldsMissing: ['operationType'] }),
        resource('Faturas', 'Unavailable', { errorKind: 'NotFound' }),
        resource('Parcelas', 'Error', { errorKind: 'Timeout' }),
        resource('Limites', 'Empty'),
      ] }],
    })
    render(<PluggyLab workspaceId="w1" />)
    fireEvent.click(await screen.findByRole('button', { name: 'Inspecionar' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Gerar diagnóstico' }))

    expect(await screen.findByText('Não suportado')).toBeTruthy()
    expect(screen.getByText('Disponível')).toBeTruthy()
    expect(screen.getByText('Indisponível')).toBeTruthy()
    expect(screen.getByText('Erro de integração')).toBeTruthy()
    expect(screen.getByText('Sem dados')).toBeTruthy()
    expect(screen.getByText('10/01/2026 – 10/05/2026')).toBeTruthy()
    expect(screen.getByText('operationType')).toBeTruthy()
    expect(screen.getByText('Retrato único.')).toBeTruthy()
  })
})
