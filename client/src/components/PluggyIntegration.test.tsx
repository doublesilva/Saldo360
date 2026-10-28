import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AccountLink, AccountLinkDetail, AvailableAccount, CategorizationOverview, CategorizationResult, ReviewPage } from '../api/pluggyIntegration'
import type { PluggyConnection } from '../api/pluggyPoc'
import type { FinancialAccount } from '../api/accounts'
import { PluggyAccountLinkForm } from './PluggyAccountLinkForm'
import { PluggyAccountPanel, PluggyCategorizationPanel, PluggyReviewPanel } from './PluggyIntegration'

const api = vi.hoisted(() => ({
  listIntegrationConnections: vi.fn(), listAvailableAccounts: vi.fn(), linkAccount: vi.fn(), getLinkDetail: vi.fn(), applyOpeningBalanceSuggestion: vi.fn(),
  reprocessImported: vi.fn(), listReview: vi.fn(), resolveReview: vi.fn(), listLinks: vi.fn(), getCategorizationOverview: vi.fn(), runCategorization: vi.fn(),
}))
vi.mock('../api/pluggyIntegration', () => api)

const connection = { id: 'conn-1', label: 'Itaú PF', institution: 'Itaú', itemReference: 'aaaaaaaa…' } as PluggyConnection
const bank: AvailableAccount = {
  pluggyAccountId: 'acc-bank', kind: 'Bank', displayName: 'Conta corrente ••••6789', maskedNumber: '••••6789', pluggyType: 'BANK', pluggySubtype: 'CHECKING_ACCOUNT',
  suggestedAccountType: 'Checking', subtypeRecognized: true, balance: '1500.75', currency: 'BRL', card: null, linkedLinkId: null, linkable: true, notLinkableReason: null,
}
const card: AvailableAccount = {
  ...bank, pluggyAccountId: 'acc-card', kind: 'Card', displayName: 'Cartão ••••2222', maskedNumber: '••••2222', pluggyType: 'CREDIT', pluggySubtype: 'CREDIT_CARD',
  suggestedAccountType: null, balance: '200.00', card: { closingDay: 10, dueDay: 17, creditLimit: '5000.00', availableCreditLimit: '800.00', brand: 'MASTERCARD' },
}
const linked: AvailableAccount = { ...bank, pluggyAccountId: 'acc-linked', displayName: 'Poupança ••••1111', linkable: false, notLinkableReason: 'AlreadyLinked', linkedLinkId: 'l9' }
const manual = { id: 'm1', name: 'Conta antiga', isActive: true } as FinancialAccount

const link = (over: Partial<AccountLink> = {}): AccountLink => ({
  id: 'l1', connectionId: 'conn-1', origin: 'Pluggy', kind: 'Bank', financialAccountId: 'a1', creditCardId: null, displayName: 'Itaú · Conta corrente ••••6789', institutionName: 'Itaú',
  pluggyType: 'BANK', pluggySubtype: 'CHECKING_ACCOUNT', status: 'Active', importFrom: null, linkedAt: '2026-10-28T10:00:00Z', lastSyncedAt: '2026-10-28T11:30:00Z',
  observed: { balance: '1500.75', observedAt: '2026-10-28T10:00:00Z', overdraftContractedLimit: '500.00', overdraftUsedLimit: '0.00', creditLimit: null, availableCreditLimit: null },
  postings: { applied: 3, waiting: 1, needsReview: 1, covered: 0, ignored: 0 }, ...over,
})
const detail = (over: Partial<AccountLinkDetail['reconciliation']> = {}, linkOver: Partial<AccountLink> = {}): AccountLinkDetail => ({
  link: link(linkOver),
  reconciliation: {
    observedBalance: '1500.75', observedAt: '2026-10-28T10:00:00Z', calculatedBalance: '1480.75', calculatedAsOf: '2026-10-28', difference: '20.00', calculatedUnavailableReason: null,
    reviewCount: 1, waitingCount: 1, suggestedOpeningBalance: null, suggestedOpeningBalanceDate: null, suggestionUnavailableReason: 'ReviewPending', card: null,
    notes: ['1 movimento(s) em revisão ainda não entram no saldo calculado.'], ...over,
  },
})

beforeEach(() => {
  vi.clearAllMocks()
  api.listIntegrationConnections.mockResolvedValue([connection])
  api.listAvailableAccounts.mockResolvedValue([bank, card, linked])
})
afterEach(cleanup)

describe('PluggyAccountLinkForm', () => {
  it('lista os Items, as contas devolvidas pela Pluggy (sem digitar ID) e bloqueia as já integradas', async () => {
    render(<PluggyAccountLinkForm workspaceId="w1" manualAccounts={[manual]} onLinked={vi.fn()} onCancel={vi.fn()} />)
    await screen.findByRole('option', { name: /Itaú PF/ })
    expect(screen.queryByLabelText(/accountId|identificador/i)).toBeNull()

    fireEvent.change(screen.getByLabelText('Conexão (Item)'), { target: { value: 'conn-1' } })

    const bankRadio = await screen.findByRole('radio', { name: /Conta corrente ••••6789/ })
    expect((bankRadio as HTMLInputElement).disabled).toBe(false)
    expect((screen.getByRole('radio', { name: /Poupança ••••1111/ }) as HTMLInputElement).disabled).toBe(true)
    expect(screen.getByText(/já integrada/)).toBeTruthy()
    expect(api.listAvailableAccounts).toHaveBeenCalledWith('w1', 'conn-1')
  })

  it('cria a conta bancária a partir dos metadados, mediante confirmação, sem saldo inicial', async () => {
    api.linkAccount.mockResolvedValue(detail())
    const onLinked = vi.fn()
    render(<PluggyAccountLinkForm workspaceId="w1" manualAccounts={[manual]} onLinked={onLinked} onCancel={vi.fn()} />)
    await screen.findByRole('option', { name: /Itaú PF/ })
    fireEvent.change(screen.getByLabelText('Conexão (Item)'), { target: { value: 'conn-1' } })
    fireEvent.click(await screen.findByRole('radio', { name: /Conta corrente ••••6789/ }))

    expect((screen.getByLabelText('Nome da conta') as HTMLInputElement).value).toBe('Conta corrente')
    fireEvent.change(screen.getByLabelText('Pessoa física ou jurídica'), { target: { value: 'PJ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar e vincular' }))

    await waitFor(() => expect(onLinked).toHaveBeenCalled())
    expect(api.linkAccount).toHaveBeenCalledWith('w1', expect.objectContaining({
      connectionId: 'conn-1', pluggyAccountId: 'acc-bank', mode: 'CreateNew', name: 'Conta corrente', accountType: 'Checking', financialScope: 'PJ', importFrom: null,
    }))
    expect(JSON.stringify(api.linkAccount.mock.calls[0][1])).not.toMatch(/openingBalance/)
  })

  it('vincular a uma conta existente exige a data de início da importação', async () => {
    api.linkAccount.mockResolvedValue(detail())
    render(<PluggyAccountLinkForm workspaceId="w1" manualAccounts={[manual]} onLinked={vi.fn()} onCancel={vi.fn()} />)
    await screen.findByRole('option', { name: /Itaú PF/ })
    fireEvent.change(screen.getByLabelText('Conexão (Item)'), { target: { value: 'conn-1' } })
    fireEvent.click(await screen.findByRole('radio', { name: /Conta corrente ••••6789/ }))
    fireEvent.click(screen.getByRole('radio', { name: /Vincular a uma conta já cadastrada/ }))
    fireEvent.change(screen.getByLabelText('Conta do Saldo360'), { target: { value: 'm1' } })

    fireEvent.click(screen.getByRole('button', { name: 'Confirmar e vincular' }))
    expect((await screen.findByRole('alert')).textContent).toMatch(/data de início da importação/)
    expect(api.linkAccount).not.toHaveBeenCalled()

    fireEvent.change(screen.getByLabelText('Importar movimentos a partir de'), { target: { value: '2026-10-01' } })
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar e vincular' }))
    await waitFor(() => expect(api.linkAccount).toHaveBeenCalledWith('w1', expect.objectContaining({ mode: 'LinkExisting', existingAccountId: 'm1', importFrom: '2026-10-01' })))
  })

  it('cartão sugere fechamento, vencimento e limite da Pluggy e envia o limite como texto decimal', async () => {
    api.linkAccount.mockResolvedValue(detail({}, { kind: 'Card', financialAccountId: null, creditCardId: 'c1' }))
    render(<PluggyAccountLinkForm workspaceId="w1" manualAccounts={[]} onLinked={vi.fn()} onCancel={vi.fn()} />)
    await screen.findByRole('option', { name: /Itaú PF/ })
    fireEvent.change(screen.getByLabelText('Conexão (Item)'), { target: { value: 'conn-1' } })
    fireEvent.click(await screen.findByRole('radio', { name: /Cartão ••••2222/ }))

    expect((screen.getByLabelText('Dia de fechamento da fatura') as HTMLInputElement).value).toBe('10')
    expect((screen.getByLabelText('Dia de vencimento da fatura') as HTMLInputElement).value).toBe('17')
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar e vincular' }))

    await waitFor(() => expect(api.linkAccount).toHaveBeenCalled())
    expect(api.linkAccount.mock.calls[0][1]).toMatchObject({ pluggyAccountId: 'acc-card', mode: 'CreateNew', closingDay: 10, dueDay: 17, creditLimit: '5000.00' })
  })

  it('mostra o erro da API (ex.: vínculo duplicado) sem fechar o formulário', async () => {
    api.linkAccount.mockRejectedValue(new Error('Esta conta da Pluggy já está vinculada.'))
    render(<PluggyAccountLinkForm workspaceId="w1" manualAccounts={[]} onLinked={vi.fn()} onCancel={vi.fn()} />)
    await screen.findByRole('option', { name: /Itaú PF/ })
    fireEvent.change(screen.getByLabelText('Conexão (Item)'), { target: { value: 'conn-1' } })
    fireEvent.click(await screen.findByRole('radio', { name: /Conta corrente ••••6789/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Confirmar e vincular' }))

    expect((await screen.findByRole('alert')).textContent).toContain('já está vinculada')
  })

  it('avisa quando não há Item cadastrado', async () => {
    api.listIntegrationConnections.mockResolvedValue([])
    render(<PluggyAccountLinkForm workspaceId="w1" manualAccounts={[]} onLinked={vi.fn()} onCancel={vi.fn()} />)
    expect(await screen.findByText(/Nenhum Item cadastrado/)).toBeTruthy()
  })
})

describe('PluggyAccountPanel', () => {
  it('separa saldo informado pelo banco, saldo calculado e diferença, e mostra situação e última sincronização', async () => {
    api.getLinkDetail.mockResolvedValue(detail())
    render(<PluggyAccountPanel workspaceId="w1" linkId="l1" refreshKey="0" />)

    const panel = await screen.findByLabelText(/Integração Itaú · Conta corrente/)
    const text = (panel.textContent ?? '').replace(/ /g, ' ')
    expect(text).toContain('Saldo informado pelo banco')
    expect(text).toContain('R$ 1.500,75')
    expect(text).toContain('Saldo calculado pelo Saldo360 até 28/10/2026')
    expect(text).toContain('R$ 1.480,75')
    expect(text).toContain('Diferença (banco − Saldo360): R$ 20,00')
    expect(text).toContain('Ativa')
    expect(text).toContain('Limite de cheque especial informado pelo banco: R$ 500,00')
    expect(text).toMatch(/3 contabilizados · 1 pendentes ou futuros · 1 em revisão/)
  })

  it('saldo calculado indisponível nunca vira zero e a sugestão só aparece com base consistente', async () => {
    api.getLinkDetail.mockResolvedValue(detail({ calculatedBalance: null, difference: null, calculatedAsOf: null, calculatedUnavailableReason: 'OpeningBalanceNotConfigured' }))
    render(<PluggyAccountPanel workspaceId="w1" linkId="l1" refreshKey="0" />)

    const panel = await screen.findByLabelText(/Integração Itaú/)
    expect(panel.textContent).toContain('indisponível (sem saldo inicial configurado)')
    expect(panel.textContent).not.toContain('Diferença')
    expect(panel.textContent).toContain('Sugestão de saldo inicial indisponível: há movimentos em revisão')
    expect(screen.queryByRole('button', { name: 'Aplicar saldo inicial sugerido' })).toBeNull()
  })

  it('aplica o saldo inicial sugerido só após confirmação, sem enviar valor', async () => {
    api.getLinkDetail.mockResolvedValue(detail({ calculatedBalance: null, difference: null, calculatedUnavailableReason: 'OpeningBalanceNotConfigured', suggestedOpeningBalance: '1438.10', suggestedOpeningBalanceDate: '2026-10-19', suggestionUnavailableReason: null }))
    api.applyOpeningBalanceSuggestion.mockResolvedValue(detail({ calculatedBalance: '1500.75', difference: '0.00' }))
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    render(<PluggyAccountPanel workspaceId="w1" linkId="l1" refreshKey="0" />)
    const button = await screen.findByRole('button', { name: 'Aplicar saldo inicial sugerido' })

    fireEvent.click(button)
    expect(api.applyOpeningBalanceSuggestion).not.toHaveBeenCalled()
    fireEvent.click(button)

    await waitFor(() => expect(api.applyOpeningBalanceSuggestion).toHaveBeenCalledWith('w1', 'l1', false))
    expect(confirm).toHaveBeenCalledTimes(2)
    expect((await screen.findByLabelText(/Integração Itaú/)).textContent).toContain('— confere.')
  })

  it('cartão mostra limites separados do saldo de caixa e as faturas do banco × Saldo360', async () => {
    api.getLinkDetail.mockResolvedValue(detail({
      calculatedBalance: null, difference: null, calculatedUnavailableReason: 'CardHasNoCashBalance',
      card: {
        creditLimit: '5000.00', usedLimit: '230.00', availableLimit: '4770.00', observedBalanceDue: '200.00', observedAvailableLimit: '800.00',
        bills: [{ closingDate: '2026-10-10', dueDate: '2026-10-17', bankTotal: '235.00', saldo360Total: '230.00', difference: '5.00', status: 'Mismatch' }],
      },
    }, { kind: 'Card', financialAccountId: null, creditCardId: 'c1' }))
    render(<PluggyAccountPanel workspaceId="w1" linkId="l1" refreshKey="0" />)

    const panel = await screen.findByLabelText(/Integração Itaú/)
    const text = (panel.textContent ?? '').replace(/ /g, ' ')
    expect(text).toContain('Cartão não soma saldo de caixa')
    expect(text).toContain('Limite do cartão: R$ 5.000,00 · usado (Saldo360): R$ 230,00 · disponível (Saldo360): R$ 4.770,00')
    expect(text).toContain('Fatura com fechamento em 10/10/2026: banco R$ 235,00 · Saldo360 R$ 230,00 · diferença R$ 5,00 — Aberta e diverge do banco')
    expect(text).not.toContain('Saldo calculado pelo Saldo360')
  })

  it('reprocessa os importados e recarrega', async () => {
    api.getLinkDetail.mockResolvedValue(detail())
    api.reprocessImported.mockResolvedValue({})
    const onChanged = vi.fn()
    render(<PluggyAccountPanel workspaceId="w1" linkId="l1" refreshKey="0" onChanged={onChanged} />)

    fireEvent.click(await screen.findByRole('button', { name: 'Reprocessar importados' }))

    await waitFor(() => expect(api.reprocessImported).toHaveBeenCalledWith('w1'))
    await waitFor(() => expect(api.getLinkDetail).toHaveBeenCalledTimes(2))
    expect(onChanged).toHaveBeenCalled()
  })
})

describe('PluggyReviewPanel', () => {
  const page = (): ReviewPage => ({
    items: [
      { postingId: 'p1', importedTransactionId: 't1', linkId: 'l1', linkDisplayName: 'Itaú · Conta corrente', linkKind: 'Bank', state: 'NeedsReview', reason: 'TransferWithoutCounterpart', date: '2026-10-06',
        amount: '20.00', direction: 'Expense', description: 'PARA POUPANCA', pluggyCategory: 'Transfers', pluggyStatus: 'Posted', operationType: null, allowedResolutions: ['AsExpense', 'Ignore'], resolution: null },
      { postingId: 'p2', importedTransactionId: 't2', linkId: 'l2', linkDisplayName: 'Itaú · Cartão', linkKind: 'Card', state: 'NeedsReview', reason: 'Card:CardRefund', date: '2026-10-07',
        amount: '40.00', direction: 'Income', description: 'ESTORNO LOJA', pluggyCategory: null, pluggyStatus: 'Posted', operationType: 'ESTORNO', allowedResolutions: ['Ignore'], resolution: null },
    ],
    total: 2, page: 1, pageSize: 25, counts: { applied: 0, waiting: 0, needsReview: 2, covered: 0, ignored: 0 },
  })

  it('lista os movimentos sem classificação segura com o motivo e só as decisões permitidas', async () => {
    api.listReview.mockResolvedValue(page())
    render(<PluggyReviewPanel workspaceId="w1" refreshKey="0" />)

    expect(await screen.findByText('Movimentos para revisar (2)')).toBeTruthy()
    const transfer = screen.getByText('PARA POUPANCA').closest('li')!
    expect(within(transfer).getByText('Transferência sem a outra ponta em uma conta integrada')).toBeTruthy()
    expect(within(transfer).getByRole('button', { name: 'Contabilizar como despesa' })).toBeTruthy()
    expect(within(transfer).queryByRole('button', { name: 'Contabilizar como receita' })).toBeNull()
    const refund = screen.getByText('ESTORNO LOJA').closest('li')!
    expect(within(refund).getByText('Estorno no cartão')).toBeTruthy()
    expect(within(refund).getAllByRole('button').map(button => button.textContent)).toEqual(['Não contabilizar'])
  })

  it('registra a decisão e recarrega a fila', async () => {
    api.listReview.mockResolvedValueOnce(page()).mockResolvedValue({ ...page(), items: [page().items[1]], total: 1 })
    api.resolveReview.mockResolvedValue({})
    const onChanged = vi.fn()
    render(<PluggyReviewPanel workspaceId="w1" refreshKey="0" onChanged={onChanged} />)

    fireEvent.click(await screen.findByRole('button', { name: 'Contabilizar como despesa' }))

    await waitFor(() => expect(api.resolveReview).toHaveBeenCalledWith('w1', 'p1', 'AsExpense'))
    expect(await screen.findByText('Movimentos para revisar (1)')).toBeTruthy()
    expect(onChanged).toHaveBeenCalled()
  })

  it('"Não contabilizar" pede confirmação', async () => {
    api.listReview.mockResolvedValue(page())
    api.resolveReview.mockResolvedValue({})
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<PluggyReviewPanel workspaceId="w1" refreshKey="0" />)

    fireEvent.click((await screen.findAllByRole('button', { name: 'Não contabilizar' }))[0])

    expect(confirm).toHaveBeenCalled()
    expect(api.resolveReview).not.toHaveBeenCalled()
  })

  it('mostra erro de decisão sem perder a lista', async () => {
    api.listReview.mockResolvedValue(page())
    api.resolveReview.mockRejectedValue(new Error('Este movimento não está em revisão.'))
    render(<PluggyReviewPanel workspaceId="w1" refreshKey="0" />)

    fireEvent.click(await screen.findByRole('button', { name: 'Contabilizar como despesa' }))

    expect((await screen.findByRole('alert')).textContent).toContain('não está em revisão')
    expect(screen.getByText('PARA POUPANCA')).toBeTruthy()
  })

  it('orienta quando não há nada a revisar', async () => {
    api.listReview.mockResolvedValue({ ...page(), items: [], total: 0 })
    render(<PluggyReviewPanel workspaceId="w1" refreshKey="0" />)
    expect(await screen.findByText('Nada para revisar.')).toBeTruthy()
  })
})

const overview = (over: Partial<CategorizationOverview> = {}): CategorizationOverview => ({
  counts: { total: 100, manual: 4, pluggy: 70, rule: 6, ai: 5, needsReview: 9, notEvaluated: 6 },
  ai: { available: true, model: 'gpt-4o-mini', reason: null }, ...over,
})
const categorizationResult = (over: Partial<CategorizationResult> = {}): CategorizationResult => ({
  considered: 20, manual: 4, pluggy: 12, rule: 3, ai: 2, unresolved: 3, categoriesCreated: 2, skippedChanged: 0, aiItemsSent: 5, aiCalls: 1, inputTokens: 1200,
  outputTokens: 80, estimatedCostUsd: 0.000228, aiStoppedBecause: null, aiFailure: null, model: 'gpt-4o-mini', counts: overview().counts, ...over,
})

describe('PluggyCategorizationPanel', () => {
  it('mostra os contadores por origem da categoria e a ordem de classificação', async () => {
    api.getCategorizationOverview.mockResolvedValue(overview())
    render(<PluggyCategorizationPanel workspaceId="w1" refreshKey="k" />)

    const list = await screen.findByLabelText('Lançamentos por origem da categoria')
    expect(within(list).getByText('Pela Pluggy: 70')).toBeTruthy()
    expect(within(list).getByText('Por regras (não são consumo): 6')).toBeTruthy()
    expect(within(list).getByText('Pela IA: 5')).toBeTruthy()
    expect(within(list).getByText('Escolhidas por você: 4')).toBeTruthy()
    expect(within(list).getByText('Sem categoria segura (precisam de revisão): 9')).toBeTruthy()
  })

  it('a categorização local roda sem confirmação e sem enviar nada à IA', async () => {
    api.getCategorizationOverview.mockResolvedValue(overview())
    api.runCategorization.mockResolvedValue(categorizationResult({ aiCalls: 0, aiItemsSent: 0, ai: 0 }))
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const changed = vi.fn()
    render(<PluggyCategorizationPanel workspaceId="w1" refreshKey="k" onChanged={changed} />)

    fireEvent.click(await screen.findByRole('button', { name: 'Categorizar automaticamente' }))

    await waitFor(() => expect(api.runCategorization).toHaveBeenCalledWith('w1', { useAi: false, confirmExternal: false }))
    expect(confirm).not.toHaveBeenCalled()
    expect(await screen.findByText(/Categorizados agora: 12 pela Pluggy, 3 por regras, 0 pela IA/)).toBeTruthy()
    expect(screen.getByText(/Categorias criadas: 2/)).toBeTruthy()
    expect(changed).toHaveBeenCalled()
    confirm.mockRestore()
  })

  it('a IA exige confirmação explícita: recusar não chama a API; confirmar envia confirmExternal e mostra tokens e custo estimado', async () => {
    api.getCategorizationOverview.mockResolvedValue(overview())
    api.runCategorization.mockResolvedValue(categorizationResult())
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
    render(<PluggyCategorizationPanel workspaceId="w1" refreshKey="k" />)
    const button = await screen.findByRole('button', { name: /Categorizar também com IA/ })

    fireEvent.click(button)
    expect(api.runCategorization).not.toHaveBeenCalled()
    expect(confirm.mock.calls[0][0]).toMatch(/OpenAI/)
    expect(confirm.mock.calls[0][0]).toMatch(/CPF\/CNPJ/)

    fireEvent.click(button)
    await waitFor(() => expect(api.runCategorization).toHaveBeenCalledWith('w1', { useAi: true, confirmExternal: true }))
    expect(await screen.findByText(/5 lançamento\(s\) enviados, 1200 tokens de entrada e 80 de saída, custo estimado US\$ 0\.0002/)).toBeTruthy()
    confirm.mockRestore()
  })

  it('sem IA disponível o botão fica desabilitado e a categorização local continua', async () => {
    api.getCategorizationOverview.mockResolvedValue(overview({ ai: { available: false, model: null, reason: 'AiNotConfigured' } }))
    render(<PluggyCategorizationPanel workspaceId="w1" refreshKey="k" />)

    const ai = await screen.findByRole('button', { name: /Categorizar também com IA/ }) as HTMLButtonElement
    expect(ai.disabled).toBe(true)
    expect(screen.getByText(/classificação por IA não está disponível neste ambiente/)).toBeTruthy()
    expect((screen.getByRole('button', { name: 'Categorizar automaticamente' }) as HTMLButtonElement).disabled).toBe(false)
  })

  it('falha do provedor de IA mantém o resultado local e explica sem expor detalhes', async () => {
    api.getCategorizationOverview.mockResolvedValue(overview())
    api.runCategorization.mockResolvedValue(categorizationResult({ aiStoppedBecause: 'ProviderFailure', aiFailure: 'ProviderAuthenticationFailed', ai: 0, aiItemsSent: 0, aiCalls: 1 }))
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<PluggyCategorizationPanel workspaceId="w1" refreshKey="k" />)

    fireEvent.click(await screen.findByRole('button', { name: /Categorizar também com IA/ }))

    expect(await screen.findByText(/A IA não respondeu; o que foi classificado localmente foi mantido/)).toBeTruthy()
    expect(screen.queryByText(/ProviderAuthenticationFailed/)).toBeNull()
  })

  it('erro da API é mostrado como alerta', async () => {
    api.getCategorizationOverview.mockResolvedValue(overview())
    api.runCategorization.mockRejectedValue(new Error('Falha ao categorizar os lançamentos.'))
    render(<PluggyCategorizationPanel workspaceId="w1" refreshKey="k" />)

    fireEvent.click(await screen.findByRole('button', { name: 'Categorizar automaticamente' }))

    expect((await screen.findByRole('alert')).textContent).toMatch(/Falha ao categorizar/)
  })
})
