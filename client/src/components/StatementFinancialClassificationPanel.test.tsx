import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import type { StatementFinancialClassification } from '../api/financialDocuments'
import { getStatementAnalysis, consolidateStatement } from '../api/financialDocuments'
import { StatementFinancialClassificationPanel } from './StatementFinancialClassificationPanel'
import { InboxStatementAnalysisPanel } from './InboxStatementAnalysisPanel'

vi.mock('../api/financialDocuments', async original => ({ ...await original<typeof import('../api/financialDocuments')>(), getStatementAnalysis: vi.fn(), consolidateStatement: vi.fn() }))
afterEach(cleanup)
const report = (): StatementFinancialClassification => ({
  readOnly: true, coverageProven: false, coverageStatus: 'FinancialCoverageRequiresReview',
  currentInvoice: { status: 'Reconciled', officialTotal: '6806.73', componentsTotal: '6806.73', difference: '0.00' },
  nextInvoice: { status: 'Divergent', officialTotal: '4286.25', componentsTotal: '4260.00', difference: '26.25' },
  diagnostics: [{ code: 'FinancialCoverageRequiresReview', page: 3 }], duplicates: [{ itemIndex: 1, otherItemIndex: null, code: 'ExistingObligationCandidate' }],
  document: { schemaVersion: '3.0', purchases: { items: [{ originalDescription: 'LOJA02/03', amount: '10.00', nature: 'Debit', section: 'FutureInstallments', page: 3, evidence: '04/07 LOJA02/03 10,00', transactionDate: null }] },
    components: [
      { description: 'CRÉDITO', kind: 'Credit', scope: 'CurrentInvoice', nature: 'Credit', unit: 'BRL', amount: '29.90', page: 2, evidence: 'CRÉDITO -29,90', contextPage: 2, contextEvidence: 'Lançamentos atuais', installmentNumber: null, installmentCount: null, confidence: 'High' },
      { description: 'PROPOSTA', kind: 'Simulation', scope: 'Offer', nature: 'Debit', unit: 'BRL', amount: '500.00', page: 3, evidence: 'PROPOSTA 500,00', contextPage: 3, contextEvidence: 'Simulação', installmentNumber: null, installmentCount: null, confidence: 'High' },
      { description: 'REFERÊNCIA', kind: 'CurrentTotal', scope: 'Control', nature: 'Debit', unit: 'BRL', amount: '6806.73', page: 1, evidence: 'Total 6.806,73', contextPage: 1, contextEvidence: 'Resumo', installmentNumber: null, installmentCount: null, confidence: 'High' },
    ] },
})

it('separa compras futuras, créditos, controles e simulações com evidência e diferenças explícitas', () => {
  render(<StatementFinancialClassificationPanel value={report()} />)
  expect(within(screen.getByRole('region', { name: 'Conciliação da fatura atual' })).getByText(/6.806,73/)).toBeTruthy()
  expect(within(screen.getByRole('region', { name: 'Conciliação da próxima fatura' })).getByText(/26,25/)).toBeTruthy()
  expect(screen.getByText(/Ofertas e simulações — não contratadas/)).toBeTruthy()
  expect(screen.getByText(/Totais e subtotais — não são lançamentos/)).toBeTruthy()
  expect(screen.getByText(/Data de compra não comprovada/)).toBeTruthy()
  expect(screen.getByText('CRÉDITO -29,90')).toBeTruthy()
  expect(screen.getByText(/ExistingObligationCandidate/)).toBeTruthy()
  expect(screen.queryByRole('button')).toBeNull()
  expect(screen.queryByRole('textbox')).toBeNull()
})

it('Inbox exibe a análise persistida sem nova extração, edição ou confirmação financeira', async () => {
  vi.mocked(getStatementAnalysis).mockResolvedValue({ status: 'Reliable', analysis: {
    schemaVersion: '2.0', cardId: 'card', status: 'Reliable', failureCode: null, categories: [],
    // Even an inconsistent response omitting block/workflow must not display a write action.
    extraction: { mode: 'AgentPrimary', primarySource: 'Agent', assessment: 'Reliable', attempts: 1, agentError: null, primary: null, financialClassification: report() },
  } })
  render(<InboxStatementAnalysisPanel workspaceId="workspace" documentId="document" cardId="card" password="" categories={[]} blocked={false} onBusy={vi.fn()} onAnalyzed={vi.fn()} />)
  await screen.findByRole('region', { name: 'Classificação financeira integral' })
  expect(screen.queryByRole('button', { name: /Solicitar consolidação|Confirmar importação/ })).toBeNull()
  expect(screen.queryByLabelText(/Corrigir categoria/)).toBeNull()
  expect(consolidateStatement).not.toHaveBeenCalled()
})

it('distingue igualdade aritmética, integridade e associação sem esconder as ocorrências originais', () => {
  const value = report()
  value.currentInvoice = { status: 'Unproven', arithmeticStatus: 'Reconciled', officialTotal: '785.33', componentsTotal: '785.33', difference: '0.00' }
  value.associations = [{ summaryComponentIndex: 0, detailComponentIndexes: [1], code: 'InternationalIofSummaryOfDetails' }]
  render(<StatementFinancialClassificationPanel value={value} />)
  const current = within(screen.getByRole('region', { name: 'Conciliação da fatura atual' }))
  expect(current.getByText(/Aritmética: Reconciled/)).toBeTruthy()
  expect(current.getByText(/Validação documental dos componentes: Unproven/)).toBeTruthy()
  expect(screen.getByRole('region', { name: 'Resumo e detalhamento associados' }).textContent).toContain('O resumo não é somado novamente')
  expect(screen.getByText(/Identidade e autorização financeira dependem/)).toBeTruthy()
  expect(screen.getByText(/Elegibilidade para importação: bloqueada/)).toBeTruthy()
  expect(screen.getByText('CRÉDITO -29,90')).toBeTruthy()
  expect(screen.getByText('PROPOSTA 500,00')).toBeTruthy()
  expect(screen.queryByRole('button')).toBeNull()
})
