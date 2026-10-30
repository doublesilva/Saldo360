import { useEffect, useState } from 'react'
import { getBudgetComposition, type BudgetComposition as CompositionData, type BudgetCompositionItem } from '../../api/budgets'
import { expenseNatureLabels } from '../../api/expenseNature'
import { formatBrlDecimal, formatDate } from '../../money'
import { factStateLabels } from './budgetText'

const PAGE_SIZE = 10

type State = { kind: 'loading' } | { kind: 'loaded'; data: CompositionData } | { kind: 'error'; message: string }

/**
 * Composição de uma meta: os registros que formam o consumo, com a mesma lista e a mesma regra do resumo (o total e os valores vêm do servidor, paginados
 * no servidor). Para todos os cartões, mostra a participação de cada cartão apenas como informação.
 */
export function BudgetComposition({ workspaceId, budgetId, targetId, version, onReviewClassification }: {
  workspaceId: string; budgetId: string; targetId: string; version: number; onReviewClassification?: () => void
}) {
  const [page, setPage] = useState(1)
  const [state, setState] = useState<State>({ kind: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    getBudgetComposition(workspaceId, budgetId, targetId, page, PAGE_SIZE, controller.signal)
      .then((data) => setState({ kind: 'loaded', data }))
      .catch((error: unknown) => { if (!controller.signal.aborted) setState({ kind: 'error', message: error instanceof Error ? error.message : 'Falha ao carregar a composição.' }) })
    return () => controller.abort()
  }, [workspaceId, budgetId, targetId, page, version])

  if (state.kind === 'loading') return <p className="status status--pending">Carregando composição…</p>
  if (state.kind === 'error') return <p className="status status--down" role="alert">{state.message}</p>
  const { data } = state
  const pages = Math.max(1, Math.ceil(data.totalCount / data.pageSize))
  return <div className="budget-composition" role="region" aria-label="Composição da meta">
    <p className="meta">{data.totalCount === 0 ? 'Nenhum registro compõe esta meta nesta competência.' : `${data.totalCount} registro(s) compõem esta meta · consumido ${formatBrlDecimal(data.amounts.consumed)}${data.amounts.committed !== '0.00' ? ` · previsto adicional ${formatBrlDecimal(data.amounts.committed)}` : ''}`}</p>
    {data.groupingType === 'AllCards' && data.cards.length > 0 && <ul className="budget-composition__cards" aria-label="Participação de cada cartão (informativa)">
      {data.cards.map((card) => <li key={card.cardId}><span>{card.cardName}</span><strong>{formatBrlDecimal(card.consumed)}</strong></li>)}
    </ul>}
    {data.coverage.undefinedNatureCount > 0 && data.groupingType === 'FixedExpenses' && <p className="notice" role="status">
      {data.coverage.undefinedNatureCount} despesa(s) do mês sem tipo definido ({formatBrlDecimal(data.coverage.undefinedNatureAmount)}) não entram nas fixas.{' '}
      {onReviewClassification && <button type="button" className="button--link" onClick={onReviewClassification}>Revisar classificação</button>}
    </p>}
    {data.items.length > 0 && <ul className="budget-composition__list">{data.items.map((item) => <CompositionItem key={`${item.origin}:${item.recordId}`} item={item} />)}</ul>}
    {pages > 1 && <nav className="pagination" aria-label="Paginação da composição">
      <button type="button" onClick={() => setPage((n) => n - 1)} disabled={page <= 1}>Anterior</button>
      <span className="meta">Página {data.page} de {pages}</span>
      <button type="button" onClick={() => setPage((n) => n + 1)} disabled={page >= pages}>Próxima</button>
    </nav>}
  </div>
}

function CompositionItem({ item }: { item: BudgetCompositionItem }) {
  const credit = item.amount.startsWith('-')
  return <li className="budget-composition__item">
    <div className="budget-composition__main"><span className="budget-composition__description">{item.description}</span><strong className={credit ? 'amount amount--income' : 'amount'}>{credit ? '−' : ''}{formatBrlDecimal(item.amount.replace('-', ''))}</strong></div>
    <div className="meta budget-composition__meta">
      <span>{formatDate(item.date)}{item.invoiceCompetence ? ` · fatura ${item.invoiceCompetence}` : ''}</span>
      {item.categoryPath && <span>{item.categoryPath}</span>}
      {(item.origin === 'Transaction' || item.origin === 'PlannedTransaction' || item.origin === 'CardInstallment' || item.origin.startsWith('Debt')) && <span className="badge badge--nature">Tipo de despesa: {expenseNatureLabels[item.expenseNature]}</span>}
      {item.cardName && <span>Cartão {item.cardName}</span>}
      {!item.cardName && item.accountName && <span>{item.accountName}</span>}
      <span className={item.state === 'Pending' ? 'badge badge--overdue' : 'badge'}>{factStateLabels[item.state]}</span>
      {!item.competenceReliable && <span className="badge badge--cancelled">Competência estimada</span>}
    </div>
  </li>
}
