import type { DecisionItem } from './queueModel'
import type { ReviewItem as ClassificationItem, ClassificationReview } from '../../api/pluggyAutomation'
import type { ReviewItem as ImportItem, ReviewPage } from '../../api/pluggyIntegration'

export const importItem = (over: Partial<ImportItem> = {}): ImportItem => ({
  postingId: 'p1', importedTransactionId: 't1', linkId: 'l1', linkDisplayName: 'Conta Corrente', linkKind: 'Account', state: 'NeedsReview', reason: 'TransferWithoutCounterpart',
  date: '2026-11-05', amount: '120.00', direction: 'Expense', description: 'PIX Mercado', pluggyCategory: null, pluggyStatus: 'POSTED', operationType: null,
  allowedResolutions: ['AsExpense', 'Ignore'], resolution: null, accounted: false, impact: 'OutOfTotals', reviewKind: 'Evidence', ...over,
} as ImportItem)
export const classificationItem = (over: Partial<ClassificationItem> = {}): ClassificationItem => ({
  action: 'Transaction', recordId: 'c1', cardId: null, date: '2026-11-04', description: 'Loja X', amount: '80.00', categoryId: null, categoryPath: null, expenseNature: 'Unset' as ClassificationItem['expenseNature'],
  cardName: null, accountName: 'Conta Corrente', imported: true, canChangeCategory: true, canChangeNature: false, scope: 'Este registro', ...over,
})
export const reviewPage = (items: ImportItem[], total = items.length): ReviewPage => ({ items, total, page: 1, pageSize: 25, counts: {} as ReviewPage['counts'] })
export const classificationPage = (items: ClassificationItem[], total = items.length): ClassificationReview => ({ kind: 'category', from: null, to: null, wholeBudget: true, items, total, page: 1, pageSize: 25, unclassifiableCount: 0 })

export const decisionItem = (over: Partial<DecisionItem> = {}): DecisionItem => ({
  key: 'import:x', source: 'import', recordId: 'x', typeLabel: 'Movimento importado', description: 'Item', account: 'Conta', date: '2026-11-01', amount: '10.00', direction: 'Expense',
  reason: 'Motivo', impact: 'accounted-until-decision', impactText: 'Já contabilizado', page: 1, ...over,
})
