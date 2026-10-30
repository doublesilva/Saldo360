import { useEffect, useState } from 'react'
import { getClassificationReview, changePurchaseCategory, type ClassificationReview, type ReviewItem, type ReviewKind } from '../api/pluggyAutomation'
import { listCategories, type FinancialCategory } from '../api/categories'
import { changeTransactionNature, recategorizeTransaction } from '../api/transactions'
import { changePurchaseNature } from '../api/creditCards'
import { expenseNatureLabels, expenseNatures, type ExpenseNature } from '../api/expenseNature'
import { toUserMessage } from '../api/apiErrors'
import { endOfMonth, startOfMonth, todayInSaoPaulo } from '../dates'
import { formatBrlDecimal, formatDate } from '../money'
import { CategoryOptions } from './CategoryOptions'
import { OperationalEmpty, OperationalError, OperationalLoading } from './OperationalFeedback'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: ClassificationReview }
interface Edit { nature: ExpenseNature; categoryId: string }

const PAGE_SIZE = 10
const rowKey = (item: ReviewItem) => `${item.action}:${item.recordId}`
const origin = (item: ReviewItem) => item.action === 'CardPurchase' ? `Cartão${item.cardName ? ` ${item.cardName}` : ''}` : item.action === 'Planned' ? 'Previsão' : `Conta${item.accountName ? ` ${item.accountName}` : ''}`

/** Resultado de uma gravação: algumas funções devolvem {ok:false, errors} em vez de lançar. */
function failure(result: unknown): string | null {
  if (typeof result !== 'object' || result === null || !('ok' in result) || (result as { ok: unknown }).ok !== false) return null
  const errors = (result as { errors?: Record<string, string[]> }).errors
  return Object.values(errors ?? {}).flat()[0] ?? 'Não foi possível salvar.'
}

/**
 * Revisão da classificação (R04): os mesmos registros que originam as contagens de "sem tipo de despesa" (período) e "sem categoria segura" (orçamento inteiro),
 * inclusive compras de cartão importadas. É só edição de classificação — categoria e tipo — e cada correção vale para o próprio registro; não concilia nem
 * confere nada. O que não tem onde ser classificado (encargos, estornos, parcelas históricas, dívidas) fica explicitamente sem tipo.
 */
export function ClassificationReviewList({ workspaceId, refreshKey = '', initial, onChanged }: { workspaceId: string; refreshKey?: string; initial?: { kind: ReviewKind; from?: string; to?: string }; onChanged?: () => void }) {
  const today = todayInSaoPaulo()
  const [kind, setKind] = useState<ReviewKind>(initial?.kind ?? 'nature')
  const [from, setFrom] = useState(initial?.from ?? startOfMonth(today))
  const [to, setTo] = useState(initial?.to ?? endOfMonth(today))
  const [page, setPage] = useState(1)
  const [reload, setReload] = useState(0)
  const [categories, setCategories] = useState<FinancialCategory[] | null>(null)
  const [edits, setEdits] = useState<Record<string, Edit>>({})
  const [busy, setBusy] = useState<string | null>(null)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [saved, setSaved] = useState<string | null>(null)
  const invalidPeriod = kind === 'nature' && from > to
  const key = `${workspaceId}|${refreshKey}|${kind}|${from}|${to}|${page}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }

  useEffect(() => {
    const controller = new AbortController()
    listCategories(workspaceId, false, controller.signal).then(setCategories).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId, reload])

  useEffect(() => {
    if (invalidPeriod) return
    const controller = new AbortController()
    getClassificationReview(workspaceId, { kind, ...(kind === 'nature' ? { from, to } : {}), page, pageSize: PAGE_SIZE }, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, key, kind, from, to, page, invalidPeriod])

  const data = visible.state === 'loaded' ? visible.data : null
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1
  const editOf = (item: ReviewItem): Edit => edits[rowKey(item)] ?? { nature: item.expenseNature, categoryId: item.categoryId ?? '' }
  const setEdit = (item: ReviewItem, change: Partial<Edit>) => setEdits(current => ({ ...current, [rowKey(item)]: { ...editOf(item), ...change } }))

  const save = async (item: ReviewItem) => {
    const edit = editOf(item)
    const natureChanged = item.canChangeNature && edit.nature !== item.expenseNature
    const categoryChanged = item.canChangeCategory && edit.categoryId !== '' && edit.categoryId !== (item.categoryId ?? '')
    if (!natureChanged && !categoryChanged) { setErrors(current => ({ ...current, [rowKey(item)]: 'Escolha uma categoria ou um tipo de despesa diferente do atual.' })); return }
    setBusy(rowKey(item)); setSaved(null); setErrors(current => ({ ...current, [rowKey(item)]: '' }))
    try {
      if (categoryChanged) {
        const result = item.action === 'CardPurchase' && item.cardId ? await changePurchaseCategory(workspaceId, item.cardId, item.recordId, edit.categoryId) : await recategorizeTransaction(workspaceId, item.recordId, edit.categoryId)
        const problem = failure(result); if (problem) throw new Error(problem)
      }
      if (natureChanged) {
        const result = item.action === 'CardPurchase' && item.cardId ? await changePurchaseNature(workspaceId, item.cardId, item.recordId, edit.nature) : await changeTransactionNature(workspaceId, item.recordId, edit.nature)
        const problem = failure(result); if (problem) throw new Error(problem)
      }
      setSaved(`Classificação de "${item.description}" salva.`)
      setEdits(current => { const next = { ...current }; delete next[rowKey(item)]; return next })
      setReload(value => value + 1); onChanged?.()
    } catch (cause) {
      setErrors(current => ({ ...current, [rowKey(item)]: toUserMessage(cause) }))
    } finally { setBusy(null) }
  }

  return <section className="card" aria-labelledby="classification-review-title" id="classification-review">
    <h4 id="classification-review-title">Revisar classificação</h4>
    <p className="meta">Corrija a categoria e o tipo de despesa dos registros que ainda não têm classificação segura, inclusive compras de cartão importadas do banco. A sua escolha é guardada e não é desfeita por novas sincronizações nem pela IA.</p>
    <form className="automation-history__filters" onSubmit={event => event.preventDefault()} aria-label="Filtros da revisão">
      <label>O que revisar<select value={kind} onChange={event => { setKind(event.target.value as ReviewKind); setPage(1) }}>
        <option value="nature">Despesas sem tipo definido (fixa ou variável)</option>
        <option value="category">Importados do banco sem categoria segura</option></select></label>
      {kind === 'nature' && <>
        <label>De<input type="date" value={from} onChange={event => { setFrom(event.target.value); setPage(1) }} /></label>
        <label>Até<input type="date" value={to} onChange={event => { setTo(event.target.value); setPage(1) }} /></label>
      </>}
    </form>
    {invalidPeriod && <p role="alert" className="field-error">O início do período não pode ser posterior ao fim.</p>}
    {data && <p className="meta" role="status">{data.wholeBudget
      ? `Orçamento inteiro (sem recorte de mês): ${data.total} registro(s) importado(s) sem categoria segura.`
      : `Período de ${data.from ? formatDate(data.from) : ''} a ${data.to ? formatDate(data.to) : ''}: ${data.total} despesa(s) sem tipo definido que podem ser classificadas aqui.`}</p>}
    {data && data.unclassifiableCount > 0 && <p className="notice notice--warning" role="status">
      {data.unclassifiableCount} despesa(s) do período (encargos e estornos de fatura, parcelas históricas ou dívidas) também estão sem tipo, mas não têm onde ser classificadas aqui: continuam explicitamente sem tipo e ficam fora das despesas fixas.</p>}
    {saved && <p role="status" className="notice">{saved}</p>}
    {!invalidPeriod && visible.state === 'loading' && <OperationalLoading>Carregando a lista…</OperationalLoading>}
    {!invalidPeriod && visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}
    {!invalidPeriod && data && data.items.length === 0 && <OperationalEmpty>{kind === 'nature' ? 'Nenhuma despesa classificável sem tipo neste período.' : 'Nenhum lançamento importado aguardando categoria.'}</OperationalEmpty>}
    {!invalidPeriod && data && data.items.length > 0 && <>
      <ul className="classification-review">
        {data.items.map(item => {
          const edit = editOf(item)
          const id = rowKey(item).replace(':', '-')
          return <li key={rowKey(item)} className="classification-review__item">
            <div className="classification-review__main">
              <strong>{item.description}</strong>
              <span className="amount amount--negative">−{formatBrlDecimal(item.amount)}</span>
            </div>
            <p className="meta">{formatDate(item.date)} · {origin(item)}{item.imported ? ' · importado do banco' : ''} · categoria atual: {item.categoryPath ?? 'sem categoria'} · tipo atual: {expenseNatureLabels[item.expenseNature]}</p>
            <p className="meta">Alcance: {item.scope}.</p>
            {item.action === 'Planned' || (!item.canChangeNature && !item.canChangeCategory)
              ? <p className="meta">Esta classificação é editada na tela de origem.</p>
              : <div className="classification-review__edit">
                {item.canChangeNature && <label htmlFor={`${id}-nature`}>Tipo de despesa<select id={`${id}-nature`} value={edit.nature} onChange={event => setEdit(item, { nature: event.target.value as ExpenseNature })}>
                  {expenseNatures.map(nature => <option key={nature} value={nature}>{expenseNatureLabels[nature]}</option>)}</select></label>}
                {item.canChangeCategory && categories && <label htmlFor={`${id}-category`}>Categoria<select id={`${id}-category`} value={edit.categoryId} onChange={event => setEdit(item, { categoryId: event.target.value })}>
                  <option value="">{item.categoryId ? 'Manter a atual' : 'Escolha uma categoria'}</option>
                  <CategoryOptions categories={categories} type="Expense" keepId={item.categoryId} /></select></label>}
                <button type="button" disabled={busy !== null} onClick={() => void save(item)}>{busy === rowKey(item) ? 'Salvando…' : 'Salvar classificação'}</button>
              </div>}
            {errors[rowKey(item)] && <p role="alert" className="field-error">{errors[rowKey(item)]}</p>}
          </li>
        })}
      </ul>
      <nav className="automation-history__pager" aria-label="Paginação da revisão">
        <button type="button" disabled={page <= 1} onClick={() => setPage(value => value - 1)}>Anteriores</button>
        <span className="meta">Página {data.page} de {pages}</span>
        <button type="button" disabled={page >= pages} onClick={() => setPage(value => value + 1)}>Próximas</button>
      </nav>
    </>}
  </section>
}
