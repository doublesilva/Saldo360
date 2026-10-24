import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { analyzeStatement, correctStatementCategory, getStatementAnalysis, type StatementAnalysisResult } from '../api/financialDocuments'
import { InboxStatementAnalysisPanel } from './InboxStatementAnalysisPanel'

vi.mock('../api/financialDocuments', async original => ({ ...await original<typeof import('../api/financialDocuments')>(), analyzeStatement: vi.fn(), correctStatementCategory: vi.fn(), getStatementAnalysis: vi.fn() }))
const analyze = vi.mocked(analyzeStatement), get = vi.mocked(getStatementAnalysis), correct = vi.mocked(correctStatementCategory)
const result = (status = 'Reliable', proposed = false): StatementAnalysisResult => ({ status, analysis: { schemaVersion: '2.0', cardId: 'card', status, failureCode: null,
  categories: [{ key: 'key', itemIndex: 0, categoryId: proposed ? null : 'health', categoryName: proposed ? 'Instrumentos' : 'Saúde', isProposal: proposed, confidence: 'High', source: proposed ? 'Proposed' : 'Existing' }],
  extraction: { mode: 'AgentPrimary', primarySource: 'Agent', assessment: status, attempts: 1, agentError: null, primary: { header: { officialTotal: '10.00' }, reconciliation: { status: status === 'Reliable' ? 'Reconciled' : 'Divergent', officialTotal: '10.00', componentsTotal: '10.00', difference: '0.00' }, items: [{ originalDescription: 'SHOP', normalizedDescription: 'Compra SHOP', transactionDate: '2026-10-01', amount: '10.00', type: 'Purchase', nature: 'Debit', section: 'CurrentInvoice', includedInCurrentTotal: true }] } } } })
const props = { workspaceId: 'personal', documentId: 'doc', cardId: 'card', password: '', categories: [{ id: 'health', name: 'Saúde', type: 'Expense' as const, isActive: true, createdAt: '' }, { id: 'other', name: 'Outros', type: 'Expense' as const, isActive: true, createdAt: '' }], blocked: false, onBusy: vi.fn(), onAnalyzed: vi.fn() }
beforeEach(() => { get.mockReset().mockResolvedValue({ status: 'NotAnalyzed', analysis: null }); analyze.mockReset(); correct.mockReset(); props.onBusy.mockClear(); props.onAnalyzed.mockClear() })
afterEach(cleanup)

it('analisa o documento armazenado sem upload e já seleciona categoria segura', async () => {
  analyze.mockResolvedValue({ ok: true, value: result() })
  render(<InboxStatementAnalysisPanel {...props} />)
  fireEvent.click(screen.getByRole('button', { name: 'Analisar fatura armazenada' }))
  await screen.findByText(/Resultado: Reliable/)
  expect(analyze).toHaveBeenCalledWith('personal', 'doc', 'card', undefined)
  expect((screen.getByLabelText('Corrigir categoria de SHOP') as HTMLSelectElement).value).toBe('health')
  expect(screen.getByText(/Total oficial:/)).toBeTruthy(); expect(screen.getByText(/Original: SHOP/)).toBeTruthy()
  expect(screen.queryByRole('button', { name: /Confirmar importação/ })).toBeNull()
})

it('mostra proposta sem criar categoria ou exigir seleção manual', async () => {
  get.mockResolvedValue(result('Reliable', true)); render(<InboxStatementAnalysisPanel {...props} />)
  await screen.findByText(/Proposta de categoria: Instrumentos/)
  expect(screen.getByRole('option', { name: 'Instrumentos (proposta, não criada)' })).toBeTruthy()
  expect(correct).not.toHaveBeenCalled()
})

it('permite correção manual explícita sem importação financeira', async () => {
  get.mockResolvedValue(result()); correct.mockResolvedValue({ ok: true, value: result() })
  render(<InboxStatementAnalysisPanel {...props} />)
  fireEvent.change(await screen.findByLabelText('Corrigir categoria de SHOP'), { target: { value: 'other' } })
  await waitFor(() => expect(correct).toHaveBeenCalledWith('personal', 'doc', 'key', 'other'))
  expect(analyze).not.toHaveBeenCalled()
})

it('bloqueia concorrência e mostra estado de análise', async () => {
  let resolve!: (value: Awaited<ReturnType<typeof analyzeStatement>>) => void
  analyze.mockImplementation(() => new Promise(r => { resolve = r }))
  render(<InboxStatementAnalysisPanel {...props} />)
  fireEvent.click(screen.getByRole('button', { name: 'Analisar fatura armazenada' }))
  const button = screen.getByRole('button', { name: 'Analisando fatura…' }) as HTMLButtonElement
  expect(button.disabled).toBe(true); fireEvent.click(button); expect(analyze).toHaveBeenCalledTimes(1)
  resolve({ ok: true, value: result() }); await screen.findByText(/Resultado: Reliable/)
})

it.each(['NeedsReview', 'Rejected'])('mantém revisão quando o resultado é %s', async status => {
  get.mockResolvedValue(result(status)); render(<InboxStatementAnalysisPanel {...props} />)
  await screen.findByText(/Há divergências ou evidências pendentes/)
})

it('não analisa sem cartão explícito e não envia senha vazia', async () => {
  render(<InboxStatementAnalysisPanel {...props} cardId="" />)
  expect((screen.getByRole('button', { name: 'Analisar fatura armazenada' }) as HTMLButtonElement).disabled).toBe(true)
  expect(screen.getByText(/Selecione explicitamente o cartão/)).toBeTruthy()
  await waitFor(() => expect(get).toHaveBeenCalled())
})

it('descarta resposta de documento anterior após troca', async () => {
  let resolve!: (value: Awaited<ReturnType<typeof analyzeStatement>>) => void
  analyze.mockImplementation(() => new Promise(r => { resolve = r }))
  const view = render(<InboxStatementAnalysisPanel key="old" {...props} />)
  fireEvent.click(screen.getByRole('button', { name: 'Analisar fatura armazenada' }))
  view.rerender(<InboxStatementAnalysisPanel key="new" {...props} documentId="other-doc" />)
  resolve({ ok: true, value: result() })
  await waitFor(() => expect(screen.queryByText(/Resultado: Reliable/)).toBeNull())
})

it('GET atrasado não sobrescreve o resultado da análise concluída', async () => {
  let resolve!: (value: StatementAnalysisResult) => void
  get.mockImplementation(() => new Promise(r => { resolve = r }))
  analyze.mockResolvedValue({ ok: true, value: result() })
  render(<InboxStatementAnalysisPanel {...props} />)
  fireEvent.click(screen.getByRole('button', { name: 'Analisar fatura armazenada' }))
  await screen.findByText(/Resultado: Reliable/)
  resolve({ status: 'NotAnalyzed', analysis: null })
  await waitFor(() => expect(screen.getByText(/Resultado: Reliable/)).toBeTruthy())
})

it('não mostra análise de outro cartão e sanitiza erros inesperados', async () => {
  get.mockResolvedValue(result()); analyze.mockRejectedValue(new Error('secret details'))
  render(<InboxStatementAnalysisPanel {...props} cardId="other-card" />)
  await waitFor(() => expect(get).toHaveBeenCalled())
  expect(screen.queryByText(/Resultado: Reliable/)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Analisar fatura armazenada' }))
  await screen.findByRole('alert'); expect(screen.queryByText(/secret details/)).toBeNull()
})
