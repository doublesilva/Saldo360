import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { searchLatestCardEmail, type FinancialCreditCard } from '../api/creditCards'
import { CardLatestEmailSearch } from './CardLatestEmailSearch'
import { FinancialCreditCardImports } from './FinancialCreditCardImports'

vi.mock('../api/creditCards', async importOriginal => ({ ...await importOriginal<typeof import('../api/creditCards')>(), searchLatestCardEmail: vi.fn() }))
vi.mock('./FinancialDocumentInbox', () => ({ FinancialDocumentInbox: ({ initialDocumentId }: { initialDocumentId: string }) => <p>Inbox documento {initialDocumentId}</p> }))
const search = vi.mocked(searchLatestCardEmail)
const card: FinancialCreditCard = { id: 'card', name: 'Synthetic', issuer: 'Bank', lastFourDigits: '1234', holderUserId: null, creditLimit: '100.00', closingDay: 5, dueDay: 10, defaultPaymentAccountId: null, isArchived: false, createdAt: '', updatedAt: '', archivedAt: null, gmailQuery: 'configured-query' }
const result = (status: string) => ({ ok: true as const, value: { status, cardId: card.id, documentId: ['Found', 'AlreadyReceived', 'NeedsReview'].includes(status) ? 'doc' : null, receivedAt: '2026-10-23T12:00:00Z', fileName: 'bill.pdf', inboxStatus: 'Received', alreadyExisted: status === 'AlreadyReceived', failureCode: null } })
beforeEach(() => { search.mockReset(); vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('[]', { status: 200 })))) })
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

it('habilita busca configurada e recebe o documento na Inbox existente', async () => {
  search.mockResolvedValue(result('Found'))
  render(<CardLatestEmailSearch workspaceId="personal" card={card} />)
  const button = screen.getByRole('button', { name: 'Buscar fatura no e-mail' }) as HTMLButtonElement
  expect(button.disabled).toBe(false); fireEvent.click(button)
  await screen.findByText('Fatura encontrada.')
  expect(search).toHaveBeenCalledWith('personal', 'card')
  expect(screen.getByText(/bill.pdf/)).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'Abrir documento na Inbox' }))
  expect(screen.getByText('Inbox documento doc')).toBeTruthy()
})

it('query ausente bloqueia busca e oferece o cadastro existente', () => {
  const configure = vi.fn()
  render(<CardLatestEmailSearch workspaceId="personal" card={{ ...card, gmailQuery: null }} onConfigure={configure} />)
  expect(screen.getByText('Este cartão não possui pesquisa de faturas configurada. Configure a query Gmail no cadastro do cartão.')).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Buscar fatura no e-mail' }) as HTMLButtonElement).disabled).toBe(true)
  fireEvent.click(screen.getByRole('button', { name: 'Configurar no cadastro do cartão' }))
  expect(configure).toHaveBeenCalledWith(card.id); expect(search).not.toHaveBeenCalled()
})

it('mostra carregamento e impede cliques concorrentes', async () => {
  let resolve!: (value: ReturnType<typeof result>) => void
  search.mockImplementation(() => new Promise(r => { resolve = r }))
  render(<CardLatestEmailSearch workspaceId="personal" card={card} />)
  fireEvent.click(screen.getByRole('button', { name: 'Buscar fatura no e-mail' }))
  const button = screen.getByRole('button', { name: 'Buscando no Gmail…' }) as HTMLButtonElement
  expect(button.disabled).toBe(true); fireEvent.click(button); expect(search).toHaveBeenCalledTimes(1)
  resolve(result('Found')); await screen.findByText('Fatura encontrada.')
})

it.each([
  ['AlreadyReceived', 'Documento já recebido.'], ['NoMatch', 'Nenhuma fatura encontrada.'],
  ['Unavailable', 'Gmail indisponível. Tente novamente mais tarde.'], ['Error', 'Erro na pesquisa. Tente novamente ou refine a query Gmail.'],
  ['NeedsReview', 'A identificação do cartão requer revisão. Nenhuma associação automática foi realizada.'],
])('apresenta estado %s', async (status, label) => {
  search.mockResolvedValue(result(status)); render(<CardLatestEmailSearch workspaceId="personal" card={card} />)
  fireEvent.click(screen.getByRole('button', { name: 'Buscar fatura no e-mail' })); await screen.findByText(label)
})

it('sanitiza erros inesperados da API', async () => {
  search.mockRejectedValue(new Error('secret provider details'))
  render(<CardLatestEmailSearch workspaceId="personal" card={card} />)
  fireEvent.click(screen.getByRole('button', { name: 'Buscar fatura no e-mail' }))
  await screen.findByRole('alert'); expect(screen.queryByText(/secret/)).toBeNull()
})

it.each(['card', 'workspace'])('descarta resposta após troca de %s', async kind => {
  let resolve!: (value: ReturnType<typeof result>) => void
  search.mockImplementation(() => new Promise(r => { resolve = r }))
  const view = render(<CardLatestEmailSearch key="old" workspaceId="personal" card={card} />)
  fireEvent.click(screen.getByRole('button', { name: 'Buscar fatura no e-mail' }))
  view.rerender(<CardLatestEmailSearch key="new" workspaceId={kind === 'workspace' ? 'other' : 'personal'} card={{ ...card, id: 'other' }} />)
  resolve(result('Found')); await waitFor(() => expect(screen.queryByText('Fatura encontrada.')).toBeNull())
})

it('preserva upload manual CSV e PDF na integração de Importações', async () => {
  render(<FinancialCreditCardImports workspaceId="personal" cards={[card]} selectedCardId={card.id} />)
  expect(screen.getByLabelText('Arquivo')).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Buscar fatura no e-mail' })).toBeTruthy()
  fireEvent.change(screen.getByLabelText('Formato'), { target: { value: 'pdf' } })
  expect(screen.getByLabelText('Arquivo PDF')).toBeTruthy()
  await waitFor(() => expect(fetch).toHaveBeenCalled())
})
