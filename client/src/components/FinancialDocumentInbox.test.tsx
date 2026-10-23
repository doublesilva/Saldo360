import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { FinancialDocumentInbox } from './FinancialDocumentInbox'

afterEach(() => { cleanup(); vi.restoreAllMocks() })
const json = (value: unknown, status = 200) => Promise.resolve(Response.json(value, { status }))
const card = { id: 'card-1', name: 'Platinum', issuer: 'Banco', lastFourDigits: '1234', holderUserId: null, creditLimit: '1000.00', closingDay: 8, dueDay: 15, defaultPaymentAccountId: null, isArchived: false, createdAt: '', updatedAt: '', archivedAt: null }
const document = (overrides: Record<string, unknown> = {}) => ({ id: 'doc-1', source: 'Email', originalFileName: 'fatura.pdf', receivedAt: '2026-10-18T10:00:00Z', status: 'NeedsReview', parser: 'ItauMastercard', competence: '2026-10', creditCardId: null, failureCode: 'PasswordRequired', updatedAt: '2026-10-18T10:00:00Z', documentType: null, classificationConfidence: null, classificationSource: null, classificationFailureCode: null, classifiedAt: null, ...overrides })
function common(items: unknown[]) { return vi.fn((input: string | URL | Request, init?: RequestInit) => { const url = String(input); if (url === '/auth/csrf') return json({ token: 'csrf' }); if (url.endsWith('/financial-documents/inbox')) return json(items); if (url.includes('/credit-cards?')) return json([card]); if (url.endsWith('/categories')) return json([{ id: 'cat-1', name: 'Compras', type: 'Expense', isActive: true, createdAt: '' }]); throw new Error(`${init?.method ?? 'GET'} ${url}`) }) }

describe('FinancialDocumentInbox', () => {
  it('abre diretamente o documento encontrado mesmo fora da lista recente', async () => {
    vi.stubGlobal('fetch', vi.fn((input: string | URL | Request) => {
      const url = String(input)
      if (url.endsWith('/financial-documents/inbox/doc-1')) return json(document())
      if (url.endsWith('/financial-documents/inbox')) return json([])
      if (url.includes('/credit-cards?')) return json([card])
      return json([])
    }))
    render(<FinancialDocumentInbox workspaceId="personal" initialDocumentId="doc-1" />)
    await screen.findByRole('button', { name: 'Reanalisar' })
    expect(screen.getAllByText('fatura.pdf').length).toBeGreaterThan(0)
    expect(fetch).toHaveBeenCalledWith('/api/financial-workspaces/personal/financial-documents/inbox/doc-1', expect.anything())
  })

  it('exibe Inbox vazia e aceita upload manual pela mesma área', async () => {
    let items: unknown[] = []; const uploads: FormData[] = []
    vi.stubGlobal('fetch', vi.fn((input: string | URL | Request, init?: RequestInit) => { const url = String(input); if (url === '/auth/csrf') return json({ token: 'csrf' }); if (url.endsWith('/financial-documents/inbox') && init?.method === 'POST') { uploads.push(init.body as FormData); const created = document({ source: 'Manual', status: 'ReadyForReview', failureCode: null }); items = [created]; return json(created, 201) } if (url.endsWith('/financial-documents/inbox')) return json(items); if (url.includes('/credit-cards?')) return json([card]); if (url.endsWith('/categories')) return json([]); throw new Error(url) }))
    render(<FinancialDocumentInbox workspaceId="family-1" />); expect(await screen.findByText('Nenhum documento encontrado.')).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: 'Adicionar documento' })); fireEvent.change(screen.getByLabelText('Arquivo PDF'), { target: { files: [new File(['%PDF'], 'manual.pdf', { type: 'application/pdf' })] } }); fireEvent.click(screen.getByRole('button', { name: 'Enviar para Inbox' })); await waitFor(() => expect(uploads).toHaveLength(1)); expect(uploads[0].get('file')).toBeInstanceOf(File)
  })

  it('mostra vazio e lista estados com filtros, contador e origem sanitizada', async () => {
    const fetch = common([document(), document({ id: 'doc-2', source: 'Manual', status: 'Imported', failureCode: null, originalFileName: 'ok.pdf' }), document({ id: 'doc-3', status: 'Conflict', failureCode: 'Conflict', originalFileName: 'conflict.pdf' })]); vi.stubGlobal('fetch', fetch)
    render(<FinancialDocumentInbox workspaceId="family-1" />)
    expect(await screen.findByLabelText('2 documentos exigem atenção')).toBeTruthy()
    expect(screen.getAllByText(/Recebido por e-mail/)).toHaveLength(2)
    expect(screen.queryByText('ok.pdf')).toBeNull()
    fireEvent.change(screen.getByLabelText('Status'), { target: { value: 'Imported' } })
    expect(screen.getByText('ok.pdf')).toBeTruthy(); expect(screen.queryByText('fatura.pdf')).toBeNull()
    expect(screen.queryByText(/gmail:/)).toBeNull()
  })

  it('reanálise protegida envia senha somente no FormData e a limpa depois do uso', async () => {
    const forms: FormData[] = []; const initial = document()
    const fetch = vi.fn((input: string | URL | Request, init?: RequestInit) => { const url = String(input); if (url === '/auth/csrf') return json({ token: 'csrf' }); if (url.endsWith('/financial-documents/inbox') && !init?.method) return json([initial]); if (url.includes('/credit-cards?')) return json([card]); if (url.endsWith('/categories')) return json([]); if (url.endsWith('/reanalyze')) { forms.push(init?.body as FormData); return json(document({ status: 'ReadyToConfirm', creditCardId: 'card-1', failureCode: null })) } throw new Error(url) }); vi.stubGlobal('fetch', fetch)
    render(<FinancialDocumentInbox workspaceId="family-1" />); fireEvent.click(await screen.findByText('fatura.pdf'))
    fireEvent.change(screen.getByLabelText('Cartão para revisão'), { target: { value: 'card-1' } }); const password = screen.getByLabelText('Senha temporária do PDF') as HTMLInputElement; fireEvent.change(password, { target: { value: 'temporary-secret' } })
    fireEvent.click(screen.getByRole('button', { name: 'Reanalisar' })); await waitFor(() => expect(forms).toHaveLength(1))
    expect(forms[0].get('password')).toBe('temporary-secret'); await waitFor(() => expect((screen.getByLabelText('Senha temporária, se protegida') as HTMLInputElement).value).toBe('')); expect(localStorage.length).toBe(0); expect(sessionStorage.length).toBe(0)
  })

  it('revisa ReadyToConfirm, exige categoria, confirma e bloqueia confirmação em Conflict', async () => {
    let listed = [document({ status: 'ReadyToConfirm', creditCardId: 'card-1', failureCode: null })]; const confirms: FormData[] = []
    const report = { documentKey: 'pdf-sha256:abc', parser: 'ItauMastercard', statement: { issuer: 'Banco', cardLastDigits: '1234', competence: '2026-10', closingDate: null, dueDate: null, totalAmount: '10.00' }, reconciliation: { status: 'Reconciled', officialTotal: '10.00', componentsTotal: '10.00', difference: '0.00', hasUnknownCurrentItems: false }, destination: { workspaceId: 'family-1', creditCardId: 'card-1', family: 'Família', creditCard: 'Platinum', holder: 'Validated', maskedCard: 'Validated', invoice: 'Validated', invoiceId: null, notes: [] }, counts: { create: 1, alreadyExists: 0, needsReview: 0, conflict: 0, unsupported: 0, informational: 0 }, items: [{ componentId: 'c1', rowNumber: 1, page: 1, type: 'Purchase', nature: 'Debit', transactionDate: '2026-10-01', description: 'COMPRA', amount: '10.00', installmentNumber: null, installmentCount: null, includedInCurrentTotal: true, classification: 'Create', matchedEntityType: null, matchedEntityId: null, reasons: [] }], estimatedImpact: [], limitations: [] }
    const fetch = vi.fn((input: string | URL | Request, init?: RequestInit) => { const url = String(input); if (url === '/auth/csrf') return json({ token: 'csrf' }); if (url.endsWith('/financial-documents/inbox') && !init?.method) return json(listed); if (url.includes('/credit-cards?')) return json([card]); if (url.endsWith('/categories')) return json([{ id: 'cat-1', name: 'Compras', type: 'Expense', isActive: true, createdAt: '' }]); if (url.endsWith('/pre-reconcile')) return json(report); if (url.endsWith('/confirm')) { confirms.push(init?.body as FormData); listed = [document({ status: 'Imported', creditCardId: 'card-1', failureCode: null })]; return json({ importId: 'i1', documentId: 'pdf-sha256:abc', status: 'Completed', parser: 'ItauMastercard', competence: '2026-10', createdPurchases: 1, createdHistoricalInstallments: 0, createdAdjustments: 0, alreadyExisting: 0, informational: 0, items: [], finalReconciliation: { officialTotal: '10.00', persistedTotal: '10.00', difference: '0.00' } }) } throw new Error(url) }); vi.stubGlobal('fetch', fetch)
    render(<FinancialDocumentInbox workspaceId="family-1" />); fireEvent.click(await screen.findByText('fatura.pdf')); fireEvent.click(screen.getByRole('button', { name: 'Revisar confirmação' })); await screen.findByText('Pré-conciliação')
    expect((screen.getByRole('button', { name: 'Confirmar importação' }) as HTMLButtonElement).disabled).toBe(true); fireEvent.change(screen.getByLabelText('Categoria de COMPRA'), { target: { value: 'cat-1' } }); fireEvent.click(screen.getByRole('button', { name: 'Confirmar importação' })); await waitFor(() => expect(confirms).toHaveLength(1)); expect(confirms[0].get('categories')).toBe('[{"componentId":"c1","categoryId":"cat-1"}]')
    cleanup(); vi.stubGlobal('fetch', common([document({ status: 'Conflict', failureCode: 'Conflict' })])); render(<FinancialDocumentInbox workspaceId="family-1" />); fireEvent.click(await screen.findByText('fatura.pdf')); expect(screen.queryByRole('button', { name: 'Confirmar importação' })).toBeNull(); expect(screen.getByText(/Não há opção de importação forçada/)).toBeTruthy()
  })

  it('ignora somente após confirmação explícita e preserva o item na lista', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true); let ignored = false
    vi.stubGlobal('fetch', vi.fn((input: string | URL | Request, init?: RequestInit) => { const url = String(input); if (url === '/auth/csrf') return json({ token: 'csrf' }); if (url.endsWith('/ignore')) { ignored = true; return json(document({ status: 'Ignored', failureCode: null })) } if (url.endsWith('/financial-documents/inbox')) return json([document()]); if (url.includes('/credit-cards?')) return json([card]); if (url.endsWith('/categories')) return json([]); throw new Error(`${init?.method ?? 'GET'} ${url}`) }))
    render(<FinancialDocumentInbox workspaceId="family-1" />); fireEvent.click(await screen.findByText('fatura.pdf')); fireEvent.click(screen.getByRole('button', { name: 'Ignorar documento' })); expect(ignored).toBe(false); fireEvent.click(screen.getByRole('button', { name: 'Ignorar documento' })); await waitFor(() => expect(ignored).toBe(true)); expect(confirm).toHaveBeenCalledTimes(2); expect(screen.getByText('Ignorado')).toBeTruthy()
  })

  it('exibe tipo conhecido sem tratar ausência de extractor como falha técnica', async () => {
    const item = document({ originalFileName: 'energia.pdf', documentType: 'ElectricityBill', classificationConfidence: 'High', classificationSource: 'Deterministic', failureCode: 'RecognizedDocumentWithoutExtractor' })
    vi.stubGlobal('fetch', vi.fn((input: string | URL | Request, init?: RequestInit) => { const url = String(input); if (url === '/auth/csrf') return json({ token: 'csrf' }); if (url.endsWith('/financial-documents/inbox') && !init?.method) return json([item]); if (url.includes('/credit-cards?')) return json([card]); if (url.endsWith('/categories')) return json([]); if (url.endsWith('/service-bill-preview')) return json({ assessment: 'Reliable', result: { documentType: 'ElectricityBill', issuer: 'Distribuidora', competence: '2026-10', dueDate: '2026-10-20', totalAmount: '187.43', accountReference: 'ABC1234', serviceReference: null, confidence: 'High', extractionSource: 'Deterministic', failureCode: null } }); if (url.endsWith('/service-bill-match-preview')) return json({ extraction: { assessment: 'Reliable', result: { documentType: 'ElectricityBill', issuer: 'Distribuidora', competence: '2026-10', dueDate: '2026-10-20', totalAmount: '187.43', accountReference: 'ABC1234', serviceReference: null, confidence: 'High', extractionSource: 'Deterministic', failureCode: null } }, match: { documentId: 'doc-1', documentType: 'ElectricityBill', competence: '2026-10', extractedAmount: '187.43', matchStatus: 'Matched', matchedOccurrenceId: 'occ-1', matchedRecurringTemplateId: 'rec-1', candidates: [{ occurrenceId: 'occ-1', recurringTemplateId: 'rec-1', description: 'Distribuidora', competence: '2026-10', projectedAmount: '180.00', dueDate: '2026-10-20', evidence: ['SameCompetence', 'IssuerExactNormalizedMatch'] }], reasons: [], confidence: 'High' } }); if (url.endsWith('/service-bill-confirm')) return json({ status: 'Confirmed', documentId: 'doc-1', occurrenceId: 'occ-1', recurrenceId: 'rec-1', competence: '2026-10', projectedAmount: '180.00', confirmedAmount: '187.43', difference: '7.43', confirmedAt: '2026-10-19T12:00:00Z', warnings: [], accountReferenceMasked: '***1234', serviceReferenceMasked: null }); if (url.endsWith('/service-account-binding')) return json({ status: 'Created', bindingId: 'binding-1', documentType: 'ElectricityBill', accountReferenceMasked: '***1234', serviceReferenceMasked: null }); throw new Error(url) }))
    render(<FinancialDocumentInbox workspaceId="family-1" />)
    expect(await screen.findByText(/Conta de energia · Recebido por e-mail/)).toBeTruthy()
    expect(screen.getByText('Reconhecido · Extração financeira ainda não disponível')).toBeTruthy()
    expect(screen.getAllByText('Requer revisão').length).toBeGreaterThan(0)
    fireEvent.click(screen.getByText('energia.pdf')); fireEvent.click(screen.getByRole('button', { name: 'Analisar conta' }))
    expect(await screen.findByText('Reliable')).toBeTruthy(); expect(screen.getByText('•••1234')).toBeTruthy(); expect(screen.getByText(/187,43/)).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Localizar lançamento' }))
    expect(await screen.findByText('Associação com recorrência')).toBeTruthy(); expect(screen.getByText(/Diferença:.*7,43/)).toBeTruthy()
    expect(screen.getByText(/somente o valor desta competência/i)).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: 'Confirmar valor da conta' }))
    expect(await screen.findByText('Confirmado pelo documento')).toBeTruthy(); expect(screen.getByText(/não pago/i)).toBeTruthy()
    expect(screen.getByText('***1234')).toBeTruthy(); fireEvent.click(screen.getByRole('button', { name: 'Reconhecer esta conta' }))
    expect(await screen.findByText(/Conta vinculada à recorrência/)).toBeTruthy(); expect(screen.queryByText('ABC1234')).toBeNull()
  })
})
