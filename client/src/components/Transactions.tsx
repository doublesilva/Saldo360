import { useEffect, useRef, useState, type FormEvent } from 'react'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import { listCategories, type CategoryType, type FinancialCategory } from '../api/categories'
import {
  TRANSACTION_DESCRIPTION_MAX_LENGTH,
  cancelTransaction,
  changeTransactionNature,
  createTransaction,
  getTransaction,
  recategorizeTransaction,
  updateTransaction,
  type FinancialTransaction,
  type TransactionFilters,
} from '../api/transactions'
import { ENTRIES_PAGE_SIZE, listFinancialEntries, type EntriesFilters, type EntryAction, type FinancialEntriesPage, type FinancialEntry } from '../api/financialEntries'
import { addCalendarMonths, formatMonthYear, todayInSaoPaulo } from '../dates'
import { decimalToInput, formatBrlDecimal, formatDate, isZeroDecimal, parseAmount } from '../money'
import { undoRefund } from '../api/pluggyIntegration'
import { expenseNatureLabels, expenseNatures, type ExpenseNature } from '../api/expenseNature'
import { hasAvailableCategory } from '../categoryTree'
import { CategoryFilterOptions, CategoryOptions } from './CategoryOptions'
import { ExpenseNatureField } from './ExpenseNatureField'
import '../Entries.css'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

type Load<T> = { kind: 'loading' } | { kind: 'loaded'; data: T } | { kind: 'error'; message: string }

export type TransactionReferences = { accounts: FinancialAccount[]; categories: FinancialCategory[] }

const typeLabels = { Income: 'Receita', Expense: 'Despesa', Transfer: 'Transferência', Payment: 'Pagamento' } as const
const statusLabels = { Realized: 'Realizado', Pending: 'Previsto', Overdue: 'Vencido', NeedsReview: 'Em revisão', Cancelled: 'Cancelado' } as const
const dateKindLabels = { Effective: 'Data efetiva', Due: 'Vencimento previsto', Invoice: 'Vencimento da fatura', Execution: 'Data da transferência' } as const
const originLabels: Record<string, string> = {
  Manual: 'Manual', PlannedRealization: 'Previsão realizada', PluggyImport: 'Banco (Pluggy)', PluggyRefund: 'Devolução', CreditCardInvoicePayment: 'Pagamento de fatura', DebtInstallmentPayment: 'Pagamento de dívida',
  Transfer: 'Transferência', PlannedTransfer: 'Transferência prevista', CardInstallment: 'Cartão', CardAdjustment: 'Cartão (ajuste)',
}
const sourceOrigin = (entry: FinancialEntry) => entry.source === 'Planned' ? 'Previsão' : entry.source === 'Recurrence' ? 'Recorrência' : entry.source === 'Debt' ? 'Dívida'
  : entry.card ? `${originLabels[entry.origin] ?? 'Cartão'} · ${entry.card.name}` : originLabels[entry.origin] ?? entry.origin
const flagLabels: Record<string, string> = {
  Imported: 'Importado do banco', Refund: 'Devolução (não é receita)', RealizesPlanned: 'Realiza uma previsão', CategoryNeedsReview: 'Categoria a revisar', PartiallyRefunded: 'Parcialmente devolvido',
  Planned: 'Previsto: ainda não aconteceu', NoPaymentEvidence: 'Sem evidência de pagamento', CompetenceEstimated: 'Competência da fatura estimada', NoPayerAccount: 'Cartão sem conta pagadora',
}
const budgetText = { Consumes: 'Consome o orçamento', Reduces: 'Reduz o consumo do orçamento', Planned: 'Previsto no orçamento', None: 'Não afeta o orçamento' } as const
const cashText = { Immediate: 'Movimenta o caixa na data', AtInvoicePayment: 'Movimenta o caixa no pagamento da fatura', Pending: 'Caixa ainda não movimentado', None: 'Não movimenta o caixa' } as const
const actionLabel = (entry: FinancialEntry, action: EntryAction) => {
  const planned = entry.source === 'Planned'
  switch (action) {
    case 'ViewDetails': return 'Ver detalhes'
    case 'Edit': return planned ? 'Editar previsão' : 'Editar lançamento'
    case 'Cancel': return planned ? 'Cancelar previsão' : 'Cancelar lançamento'
    case 'Realize': return 'Realizar previsão'
    case 'Recategorize': return 'Corrigir categoria'
    case 'ChangeNature': return 'Corrigir tipo de despesa'
    case 'UndoRefund': return 'Desfazer devolução'
    case 'OpenCards': return 'Abrir cartões e faturas'
    case 'OpenDebts': return 'Abrir dívida'
    case 'OpenTransfers': return 'Consultar transferência'
    case 'CancelPlannedTransfer': return 'Cancelar transferência prevista'
    case 'OpenRecurrences': return 'Abrir recorrência'
    default: return action
  }
}
/** Destino de cada ação que pertence a outro módulo (previsões, cartões, dívidas, transferências, recorrências). */
const actionSection: Record<string, string> = { Realize: 'planned', OpenCards: 'cards', OpenDebts: 'debts', OpenTransfers: 'transfers', CancelPlannedTransfer: 'transfers', OpenRecurrences: 'recurrences' }

const defaultFilters = (): EntriesFilters => ({
  competence: todayInSaoPaulo().slice(0, 7), scope: 'all', accountId: '', categoryId: '', type: '', status: '', source: '', nature: '', search: '', includeCancelled: false, sort: 'date', direction: 'desc',
})

/** Os filtros e a página sobrevivem à ida a um detalhe e à volta (a página é desmontada ao trocar de seção). */
const remembered = new Map<string, { filters: EntriesFilters; page: number }>()

/** Recorte vindo de outra tela (período, conta, tipo): a competência é o mês do início do período. */
function fromInitial(initial?: Partial<TransactionFilters>): Partial<EntriesFilters> {
  if (!initial) return {}
  const next: Partial<EntriesFilters> = {}
  if (initial.from) next.competence = initial.from.slice(0, 7)
  if (initial.accountId) next.accountId = initial.accountId
  if (initial.type === 'Income' || initial.type === 'Expense' || initial.type === 'Transfer') next.type = initial.type
  if (initial.expenseNature) next.nature = initial.expenseNature
  if (initial.categoryId) next.categoryId = initial.categoryId
  if (initial.includeCancelled) next.includeCancelled = true
  return next
}

/**
 * Lançamentos (T13.1): todos os movimentos de UMA competência numa só tabela, qualquer que seja a origem (transações, previsões, recorrências, dívidas, transferências e cartões). Filtros,
 * ordenação e paginação são do servidor; o navegador só apresenta. As ações aparecem no menu "⋯" conforme o que o registro permite, e o servidor revalida cada uma.
 */
export function Transactions({
  workspaceId,
  onChanged,
  refreshKey = 0,
  initialFilters,
  startWithForm = false,
  onNavigate,
}: {
  workspaceId: string
  /** Recorte inicial vindo de outra tela (período e conta). */
  initialFilters?: Partial<TransactionFilters>
  /** Abre o formulário de novo lançamento ao entrar (atalho da Visão Geral). */
  startWithForm?: boolean
  /** Leva a ação que pertence a outro módulo (previsões, cartões, dívidas, transferências, recorrências). */
  onNavigate?: (section: string) => void
  onChanged?: () => void
  /** Muda quando outra seção altera lançamentos (ex.: realizar previsão): a lista é recarregada. */
  refreshKey?: number
}) {
  const saved = initialFilters ? undefined : remembered.get(workspaceId)
  const [filters, setFilters] = useState<EntriesFilters>(() => saved?.filters ?? { ...defaultFilters(), ...fromInitial(initialFilters) })
  const [pageNumber, setPageNumber] = useState(saved?.page ?? 1)
  const [data, setData] = useState<Load<FinancialEntriesPage>>({ kind: 'loading' })
  const [refs, setRefs] = useState<Load<TransactionReferences>>({ kind: 'loading' })
  const [reloadKey, setReloadKey] = useState(0)
  const [editing, setEditing] = useState<FinancialTransaction | 'new' | null>(startWithForm ? 'new' : null)
  const [advanced, setAdvanced] = useState(() => Boolean(filters.categoryId || filters.nature || filters.source || filters.search || filters.includeCancelled))
  const [searchDraft, setSearchDraft] = useState(filters.search)
  const [menuId, setMenuId] = useState<string | null>(null)
  const [detailId, setDetailId] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)
  const menuRef = useRef<HTMLDivElement>(null)

  useEffect(() => { remembered.set(workspaceId, { filters, page: pageNumber }) }, [workspaceId, filters, pageNumber])

  // Contas e categorias mudam em outras seções: recarrega ao abrir o formulário e após salvar.
  useEffect(() => {
    const controller = new AbortController()
    Promise.all([listAccounts(workspaceId, true, controller.signal), listCategories(workspaceId, true, controller.signal)])
      .then(([accounts, categories]) => setRefs({ kind: 'loaded', data: { accounts, categories } }))
      .catch((error: unknown) => { if (!controller.signal.aborted) setRefs({ kind: 'error', message: errorMessage(error) }) })
    return () => controller.abort()
  }, [workspaceId, reloadKey, editing])

  useEffect(() => {
    const controller = new AbortController()
    listFinancialEntries(workspaceId, filters, pageNumber, controller.signal)
      .then(result => setData({ kind: 'loaded', data: result }))
      .catch((error: unknown) => { if (!controller.signal.aborted) setData({ kind: 'error', message: errorMessage(error) }) })
    return () => controller.abort()
  }, [workspaceId, filters, pageNumber, reloadKey, refreshKey])

  // O menu de ações fecha com Esc ou clique fora.
  useEffect(() => {
    if (!menuId) return
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setMenuId(null) }
    const onClick = (event: MouseEvent) => { if (menuRef.current && !menuRef.current.contains(event.target as Node)) setMenuId(null) }
    document.addEventListener('keydown', onKey); document.addEventListener('mousedown', onClick)
    return () => { document.removeEventListener('keydown', onKey); document.removeEventListener('mousedown', onClick) }
  }, [menuId])

  const reload = () => setReloadKey(n => n + 1)
  const change = (patch: Partial<EntriesFilters>) => { setFilters(current => ({ ...current, ...patch })); setPageNumber(1); setDetailId(null) }
  const moveMonth = (months: number) => change({ competence: addCalendarMonths(`${filters.competence}-01`, months).slice(0, 7) })
  const sortBy = (sort: EntriesFilters['sort']) => change({ sort, direction: filters.sort === sort && filters.direction === 'desc' ? 'asc' : sort === 'description' ? 'asc' : 'desc' })

  const fail = (error: unknown) => setActionError(errorMessage(error))
  const firstError = (errors: Record<string, string[]>, fallback: string) => Object.values(errors).flat()[0] ?? fallback

  const run = async (entry: FinancialEntry, action: EntryAction) => {
    setMenuId(null); setActionError(null)
    const section = actionSection[action]
    if (section) { onNavigate?.(section); return }
    try {
      if (action === 'ViewDetails' || action === 'Recategorize' || action === 'ChangeNature') { setDetailId(current => current === entry.id && action === 'ViewDetails' ? null : entry.id); return }
      if ((action === 'Edit' || action === 'Cancel') && !entry.transactionId) { onNavigate?.('planned'); return }
      if (!entry.transactionId) return
      setBusyId(entry.id)
      if (action === 'Edit') { setEditing(await getTransaction(workspaceId, entry.transactionId)); return }
      if (action === 'Cancel') {
        const question = `Cancelar o lançamento "${entry.description}" de ${formatDate(entry.date)}? O registro é preservado (aparece em "Mostrar cancelados"), mas não poderá mais ser editado.` +
          (entry.plannedTransactionId ? ' Ele realiza uma previsão: a previsão voltará a ficar pendente e poderá ser realizada de novo.' : '')
        if (!window.confirm(question)) return
        const result = await cancelTransaction(workspaceId, entry.transactionId)
        if (!result.ok) setActionError(firstError(result.errors, 'Não foi possível cancelar.'))
        reload(); onChanged?.(); return
      }
      if (action === 'UndoRefund') {
        const transaction = await getTransaction(workspaceId, entry.transactionId)
        if (!transaction.refundOf?.postingId) return
        if (!window.confirm(`Desfazer a devolução de "${transaction.refundOf.originalDescription}"? O dinheiro recebido continua na conta, mas deixa de reduzir a despesa.`)) return
        await undoRefund(workspaceId, transaction.refundOf.postingId)
        reload(); onChanged?.()
      }
    } catch (error: unknown) { fail(error) } finally { setBusyId(null) }
  }

  /** Corrige só a categoria interna de um lançamento importado; valor, data e descrição vêm do banco e não mudam. */
  const recategorize = async (entry: FinancialEntry, categoryId: string) => {
    if (!entry.transactionId) return
    setBusyId(entry.id); setActionError(null)
    try {
      const result = await recategorizeTransaction(workspaceId, entry.transactionId, categoryId || null)
      if (!result.ok) setActionError(firstError(result.errors, 'Não foi possível alterar a categoria.'))
      reload(); onChanged?.()
    } catch (error: unknown) { fail(error) } finally { setBusyId(null) }
  }

  /** Corrige só o tipo de despesa (fixa/variável/não definida); categoria, valor, data e saldo não mudam. */
  const changeNature = async (entry: FinancialEntry, nature: ExpenseNature) => {
    if (!entry.transactionId) return
    setBusyId(entry.id); setActionError(null)
    try {
      const result = await changeTransactionNature(workspaceId, entry.transactionId, nature)
      if (!result.ok) setActionError(firstError(result.errors, 'Não foi possível alterar o tipo de despesa.'))
      reload()
    } catch (error: unknown) { fail(error) } finally { setBusyId(null) }
  }

  if (editing !== null) {
    if (refs.kind !== 'loaded') return <p className="status status--pending">Carregando contas e categorias…</p>
    return <TransactionForm workspaceId={workspaceId} references={refs.data} transaction={editing === 'new' ? null : editing}
      onSaved={() => { setEditing(null); reload(); onChanged?.() }} onCancel={() => setEditing(null)} />
  }

  const accounts = refs.kind === 'loaded' ? refs.data.accounts : []
  const categories = refs.kind === 'loaded' ? refs.data.categories : []
  const totalPages = data.kind === 'loaded' ? Math.max(1, Math.ceil(data.data.totalCount / (data.data.pageSize || ENTRIES_PAGE_SIZE))) : 1
  const ariaSort = (sort: EntriesFilters['sort']) => filters.sort === sort ? (filters.direction === 'asc' ? 'ascending' : 'descending') : 'none'

  return (
    <div className="entries">
      <header className="entries__header">
        <div><h3>Lançamentos</h3><p className="meta">Suas receitas, despesas e compromissos realizados ou previstos.</p></div>
        <button type="button" className="button--primary" onClick={() => setEditing('new')}>+ Novo lançamento</button>
      </header>

      <div className="entries__filters" role="search" aria-label="Filtros dos lançamentos">
        <div className="entries__month" role="group" aria-label="Navegação de competência">
          <button type="button" aria-label="Competência anterior" onClick={() => moveMonth(-1)}>‹</button>
          <input type="month" aria-label="Competência" required value={filters.competence} onChange={event => { if (/^\d{4}-\d{2}$/.test(event.target.value)) change({ competence: event.target.value }) }} />
          <button type="button" aria-label="Próxima competência" onClick={() => moveMonth(1)}>›</button>
        </div>
        <label>Escopo
          <select value={filters.scope} onChange={event => change({ scope: event.target.value as EntriesFilters['scope'] })}>
            <option value="all">Todas</option><option value="pf">PF</option><option value="pj">PJ</option>
          </select></label>
        <label>Conta ou cartão
          <select value={filters.accountId} onChange={event => change({ accountId: event.target.value })}>
            <option value="">Todas</option>
            {accounts.map(account => <option key={account.id} value={account.id}>{account.name}{account.isActive ? '' : ' (inativa)'}</option>)}
          </select></label>
        <label>Tipo
          <select value={filters.type} onChange={event => change({ type: event.target.value as EntriesFilters['type'] })}>
            <option value="">Todos</option><option value="Income">Receita</option><option value="Expense">Despesa</option><option value="Transfer">Transferência</option><option value="Payment">Pagamento</option>
          </select></label>
        <label>Situação
          <select value={filters.status} onChange={event => change({ status: event.target.value as EntriesFilters['status'] })}>
            <option value="">Todos</option><option value="Realized">Realizados</option><option value="Pending">Previstos</option><option value="Overdue">Vencidos</option>
            <option value="NeedsReview">Em revisão</option><option value="OverduePrevious">Pendências anteriores</option>
          </select></label>
        <button type="button" className="entries__toggle" aria-expanded={advanced} aria-controls="entries-advanced" onClick={() => setAdvanced(value => !value)}>Filtros avançados</button>
        {advanced && <div id="entries-advanced" className="entries__advanced">
          <label>Categoria
            <select value={filters.categoryId} onChange={event => change({ categoryId: event.target.value })}>
              <option value="">Todas</option>
              <CategoryFilterOptions categories={categories} type={filters.type === 'Income' || filters.type === 'Expense' ? filters.type : ''} />
            </select></label>
          <label>Natureza
            <select value={filters.nature} onChange={event => change({ nature: event.target.value as EntriesFilters['nature'] })}>
              <option value="">Todas</option>
              {expenseNatures.map(nature => <option key={nature} value={nature}>{expenseNatureLabels[nature]}</option>)}
            </select></label>
          <label>Origem
            <select value={filters.source} onChange={event => change({ source: event.target.value as EntriesFilters['source'] })}>
              <option value="">Todas</option><option value="Transaction">Lançamentos e banco</option><option value="Planned">Previsões</option><option value="Recurrence">Recorrências</option>
              <option value="Debt">Dívidas</option><option value="Transfer">Transferências</option><option value="CardPurchase">Compras de cartão</option><option value="CardAdjustment">Ajustes de cartão</option>
            </select></label>
          <form className="entries__search" onSubmit={(event: FormEvent) => { event.preventDefault(); change({ search: searchDraft }) }}>
            <label>Pesquisar na descrição<input type="search" value={searchDraft} onChange={event => setSearchDraft(event.target.value)} onBlur={() => { if (searchDraft !== filters.search) change({ search: searchDraft }) }} /></label>
          </form>
          <label className="checkbox"><input type="checkbox" checked={filters.includeCancelled} onChange={event => change({ includeCancelled: event.target.checked })} />Mostrar cancelados</label>
        </div>}
      </div>

      {actionError && <p className="field-error" role="alert">{actionError}</p>}
      {data.kind === 'loading' && <p className="status status--pending">Carregando lançamentos…</p>}
      {data.kind === 'error' && <p className="status status--down" role="alert">{data.message}</p>}
      {data.kind === 'loaded' && data.data.items.length === 0 && <p className="meta">Nenhum lançamento encontrado para {formatMonthYear(`${filters.competence}-01`)} com estes filtros.</p>}

      {data.kind === 'loaded' && data.data.items.length > 0 && <>
        <div className="entries__table-wrap">
          <table className="entries__table">
            <caption className="visually-hidden">Lançamentos de {formatMonthYear(`${filters.competence}-01`)}</caption>
            <thead><tr>
              <th scope="col" aria-sort={ariaSort('date')}><button type="button" className="entries__sort" onClick={() => sortBy('date')}>Data</button></th>
              <th scope="col" aria-sort={ariaSort('description')}><button type="button" className="entries__sort" onClick={() => sortBy('description')}>Descrição</button></th>
              <th scope="col" className="entries__c-type">Tipo</th><th scope="col" className="entries__c-origin">Origem</th><th scope="col" className="entries__c-category">Categoria</th>
              <th scope="col">Situação</th>
              <th scope="col" className="entries__num" aria-sort={ariaSort('amount')}><button type="button" className="entries__sort" onClick={() => sortBy('amount')}>Valor</button></th>
              <th scope="col" className="entries__c-actions"><span className="visually-hidden">Ações</span></th>
            </tr></thead>
            <tbody>{data.data.items.map(entry => <Row key={entry.id} entry={entry} categories={categories} busy={busyId === entry.id} detailOpen={detailId === entry.id} menuOpen={menuId === entry.id} menuRef={menuRef}
              onMenu={() => setMenuId(current => current === entry.id ? null : entry.id)} onAction={action => void run(entry, action)}
              onRecategorize={id => void recategorize(entry, id)} onNature={nature => void changeNature(entry, nature)} />)}</tbody>
          </table>
        </div>
        <nav className="pagination" aria-label="Paginação dos lançamentos">
          <button type="button" onClick={() => setPageNumber(n => n - 1)} disabled={pageNumber <= 1}>Anterior</button>
          <span className="meta">Página {data.data.page} de {totalPages} · {data.data.totalCount} {data.data.totalCount === 1 ? 'lançamento' : 'lançamentos'}</span>
          <button type="button" onClick={() => setPageNumber(n => n + 1)} disabled={pageNumber >= totalPages}>Próxima</button>
        </nav>
      </>}
    </div>
  )
}

function Row({ entry, categories, busy, detailOpen, menuOpen, menuRef, onMenu, onAction, onRecategorize, onNature }: {
  entry: FinancialEntry; categories: FinancialCategory[]; busy: boolean; detailOpen: boolean; menuOpen: boolean; menuRef: React.RefObject<HTMLDivElement | null>
  onMenu: () => void; onAction: (action: EntryAction) => void; onRecategorize: (categoryId: string) => void; onNature: (nature: ExpenseNature) => void
}) {
  const cancelled = entry.status === 'Cancelled'
  const sign = entry.direction === 'In' ? '+ ' : entry.direction === 'Out' ? '− ' : ''
  const counterpart = entry.counterpart ? ` → ${entry.counterpart.name}` : ''
  const where = entry.card ? entry.card.name : entry.account?.name
  const categoryText = entry.category ? entry.category.path ?? entry.category.name : entry.type === 'Transfer' || entry.type === 'Payment' ? '—' : 'Sem categoria'
  const menuId = `entries-menu-${entry.id}`
  return <>
    <tr className={`entries__row${cancelled ? ' entries__row--cancelled' : ''}`}>
      <td className="entries__date"><span className="ov-mono">{formatDate(entry.date).slice(0, 5)}</span></td>
      <td className="entries__desc"><strong>{entry.description}</strong>
        <small>{where ? `${where}${counterpart}` : 'Sem conta pagadora'}<span className="entries__narrow"> · {typeLabels[entry.type]} · {categoryText}</span></small></td>
      <td className="entries__c-type">{typeLabels[entry.type]}</td>
      <td className="entries__c-origin">{sourceOrigin(entry)}</td>
      <td className="entries__c-category">{categoryText}</td>
      <td className="entries__status"><span className={`entries__badge entries__badge--${entry.status}`}>{statusLabels[entry.status]}</span></td>
      <td className={`entries__num ov-mono entries__amount entries__amount--${entry.direction}`}><span className="visually-hidden">{entry.direction === 'In' ? 'Entrada ' : entry.direction === 'Out' ? 'Saída ' : ''}</span>{sign}{formatBrlDecimal(entry.amount)}</td>
      <td className="entries__c-actions">
        <div className="entries__menu" ref={menuOpen ? menuRef : undefined}>
          <button type="button" className="entries__more" aria-haspopup="menu" aria-expanded={menuOpen} aria-controls={menuOpen ? menuId : undefined} aria-label={`Ações de ${entry.description}`} disabled={busy} onClick={onMenu}>⋯</button>
          {menuOpen && <ul id={menuId} role="menu" className="entries__menu-list">{entry.actions.map(action => <li key={action} role="none"><button type="button" role="menuitem" onClick={() => onAction(action)}>{actionLabel(entry, action)}</button></li>)}</ul>}
        </div>
      </td>
    </tr>
    {detailOpen && <tr className="entries__detail"><td colSpan={8}>
      <dl className="entries__facts">
        <div><dt>Competência</dt><dd>{formatMonthYear(`${entry.competence}-01`)}</dd></div>
        <div><dt>{dateKindLabels[entry.dateKind]}</dt><dd>{formatDate(entry.date)}</dd></div>
        <div><dt>Origem</dt><dd>{sourceOrigin(entry)}</dd></div>
        {entry.account && <div><dt>{entry.card ? 'Conta pagadora' : 'Conta'}</dt><dd>{entry.account.name}{entry.account.scope ? ` · ${entry.account.scope}` : ''}</dd></div>}
        {entry.counterpart && <div><dt>Destino</dt><dd>{entry.counterpart.name}</dd></div>}
        {entry.card && <div><dt>Cartão</dt><dd>{entry.card.name}{entry.invoiceCompetence ? ` · fatura de ${formatMonthYear(`${entry.invoiceCompetence}-01`)}` : ''}</dd></div>}
        <div><dt>Orçamento</dt><dd>{budgetText[entry.budgetImpact]}</dd></div>
        <div><dt>Caixa</dt><dd>{cashText[entry.cashImpact]}</dd></div>
        {entry.nature && entry.type === 'Expense' && <div><dt>Tipo de despesa</dt><dd>{expenseNatureLabels[entry.nature as ExpenseNature] ?? entry.nature}</dd></div>}
        {entry.plannedTransactionId && entry.source === 'Transaction' && <div><dt>Vínculo</dt><dd>Realiza uma previsão</dd></div>}
      </dl>
      {entry.flags.filter(flag => flagLabels[flag]).length > 0 && <ul className="entries__flags" aria-label="Ressalvas">{entry.flags.filter(flag => flagLabels[flag]).map(flag => <li key={flag}>{flagLabels[flag]}</li>)}</ul>}
      {entry.actions.includes('Recategorize') && <label>Categoria
        <select aria-label={`Categoria de ${entry.description}`} value={entry.category?.id ?? ''} disabled={busy} onChange={event => onRecategorize(event.target.value)}>
          <option value="">Sem categoria</option>
          <CategoryOptions categories={categories} type={entry.type === 'Income' ? 'Income' : 'Expense'} keepId={entry.category?.id ?? undefined} />
        </select></label>}
      {entry.actions.includes('ChangeNature') && <label>Tipo de despesa
        <select aria-label={`Tipo de despesa de ${entry.description}`} value={entry.nature ?? 'Undefined'} disabled={busy} onChange={event => onNature(event.target.value as ExpenseNature)}>
          {expenseNatures.map(nature => <option key={nature} value={nature}>{expenseNatureLabels[nature]}</option>)}
        </select></label>}
    </td></tr>}
  </>
}

type FormState = {
  type: CategoryType
  accountId: string
  categoryId: string
  description: string
  amount: string
  transactionDate: string
  expenseNature: ExpenseNature
}

export function TransactionForm({
  workspaceId,
  references,
  transaction,
  onSaved,
  onCancel,
}: {
  workspaceId: string
  references: TransactionReferences
  transaction: FinancialTransaction | null
  onSaved: () => void
  onCancel: () => void
}) {
  const today = todayInSaoPaulo()
  const [form, setForm] = useState<FormState>(
    transaction
      ? {
          type: transaction.type,
          accountId: transaction.account.id,
          categoryId: transaction.category?.id ?? '',
          description: transaction.description,
          amount: decimalToInput(transaction.amount),
          transactionDate: transaction.transactionDate,
          expenseNature: transaction.expenseNature ?? 'Undefined',
        }
      : { type: 'Expense', accountId: '', categoryId: '', description: '', amount: '', transactionDate: today, expenseNature: 'Undefined' },
  )
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)

  const set = <K extends keyof FormState>(field: K, value: FormState[K]) =>
    setForm((current) => ({ ...current, [field]: value }))

  // Ativas, mais a conta/categoria inativa que o lançamento editado já usa (pode ser mantida).
  const accountOptions = references.accounts.filter(
    (account) => account.isActive || account.id === transaction?.account.id,
  )
  const hasActiveAccount = references.accounts.some((account) => account.isActive)
  const hasActiveCategory = hasAvailableCategory(references.categories, form.type)
  const singular = form.type === 'Income' ? 'receita' : 'despesa'

  const changeType = (type: CategoryType) =>
    setForm((current) => ({
      ...current,
      type,
      // Receita não tem tipo de despesa.
      expenseNature: type === 'Income' ? 'Undefined' : current.expenseNature,
      // Categoria é de um tipo só: ao trocar o tipo, a escolha anterior deixa de valer.
      categoryId: references.categories.some((c) => c.id === current.categoryId && c.type === type) ? current.categoryId : '',
    }))

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const clientErrors: Record<string, string[]> = {}
    if (!form.accountId) clientErrors.accountId = ['Escolha a conta.']
    if (!form.categoryId) clientErrors.categoryId = ['Escolha a categoria.']
    if (form.description.trim().length === 0) clientErrors.description = ['Informe a descrição.']
    if (!form.transactionDate) clientErrors.transactionDate = ['Informe a data do lançamento.']
    else if (form.transactionDate > today)
      clientErrors.transactionDate = ['A data não pode ser futura: registre só movimentações já realizadas.']

    const amount = parseAmount(form.amount)
    if (amount.kind === 'empty') clientErrors.amount = ['Informe o valor.']
    else if (amount.kind === 'invalid') clientErrors.amount = [amount.message]
    else if (amount.text.startsWith('-') || isZeroDecimal(amount.text))
      clientErrors.amount = ['O valor deve ser maior que zero. Para saídas, escolha o tipo despesa.']

    if (Object.keys(clientErrors).length > 0 || amount.kind !== 'ok') {
      setErrors(clientErrors)
      return
    }

    setBusy(true)
    setErrors({})
    try {
      const input = {
        accountId: form.accountId,
        categoryId: form.categoryId,
        type: form.type,
        description: form.description.trim(),
        amount: amount.text,
        transactionDate: form.transactionDate,
        ...(form.type === 'Expense' ? { expenseNature: form.expenseNature } : {}),
      }
      const result = transaction
        ? await updateTransaction(workspaceId, transaction.id, input)
        : await createTransaction(workspaceId, input)
      if (result.ok) onSaved()
      else setErrors(result.errors)
    } catch (error: unknown) {
      setErrors({ form: [errorMessage(error)] })
    } finally {
      setBusy(false)
    }
  }

  const fieldError = (field: string) =>
    errors[field]?.[0] && (
      <p id={`transaction-${field}-error`} className="field-error" role="alert">
        {errors[field][0]}
      </p>
    )
  const describedBy = (field: string) => (errors[field] ? `transaction-${field}-error` : undefined)

  return (
    <form className="form transactions" onSubmit={handleSubmit} noValidate>
      <h3>{transaction ? 'Editar lançamento' : 'Novo lançamento'}</h3>
      <p className="meta">
        Só movimentações já realizadas: a data é o dia em que o dinheiro entrou ou saiu da conta, e não pode ser
        futura. Datas anteriores ao saldo inicial da conta são aceitas como histórico, mas não entram de novo no saldo.
      </p>

      <fieldset className="transaction-type">
        <legend>Tipo</legend>
        {(['Income', 'Expense'] as CategoryType[]).map((type) => (
          <label key={type} className="checkbox">
            <input
              type="radio"
              name="transaction-type"
              value={type}
              checked={form.type === type}
              onChange={() => changeType(type)}
            />
            {type === 'Income' ? 'Receita (entrada)' : 'Despesa (saída)'}
          </label>
        ))}
      </fieldset>

      {!hasActiveAccount && (
        <p className="status status--pending" role="status">
          Nenhuma conta ativa. Cadastre ou reative uma conta na seção Contas antes de lançar.
        </p>
      )}
      {!hasActiveCategory && (
        <p className="status status--pending" role="status">
          Nenhuma categoria de {singular} ativa. Cadastre ou reative uma na seção Categorias.
        </p>
      )}

      <label htmlFor="transaction-account">Conta</label>
      <select
        id="transaction-account"
        value={form.accountId}
        onChange={(event) => set('accountId', event.target.value)}
        aria-invalid={Boolean(errors.accountId)}
        aria-describedby={describedBy('accountId')}
      >
        <option value="">Escolha a conta</option>
        {accountOptions.map((account) => (
          <option key={account.id} value={account.id}>
            {account.name} ({account.financialScope}){account.isActive ? '' : ' — inativa'}
          </option>
        ))}
      </select>
      {fieldError('accountId')}

      <label htmlFor="transaction-category">Categoria</label>
      <select
        id="transaction-category"
        value={form.categoryId}
        onChange={(event) => set('categoryId', event.target.value)}
        aria-invalid={Boolean(errors.categoryId)}
        aria-describedby={describedBy('categoryId')}
      >
        <option value="">Escolha a categoria de {singular}</option>
        <CategoryOptions categories={references.categories} type={form.type} keepId={transaction?.category?.id} />
      </select>
      {fieldError('categoryId')}
      {fieldError('type')}

      {form.type === 'Expense' && (
        <ExpenseNatureField
          id="transaction-expense-nature"
          value={form.expenseNature}
          onChange={(value) => set('expenseNature', value)}
          error={errors.expenseNature?.[0]}
        />
      )}

      <label htmlFor="transaction-description">Descrição</label>
      <input
        id="transaction-description"
        value={form.description}
        onChange={(event) => set('description', event.target.value)}
        maxLength={TRANSACTION_DESCRIPTION_MAX_LENGTH}
        placeholder={form.type === 'Income' ? 'Ex.: Salário de outubro' : 'Ex.: Compra no mercado'}
        autoComplete="off"
        aria-invalid={Boolean(errors.description)}
        aria-describedby={describedBy('description')}
      />
      {fieldError('description')}

      <label htmlFor="transaction-amount">Valor (R$)</label>
      <input
        id="transaction-amount"
        value={form.amount}
        onChange={(event) => set('amount', event.target.value)}
        inputMode="decimal"
        placeholder="Ex.: 1.234,56"
        autoComplete="off"
        aria-invalid={Boolean(errors.amount)}
        aria-describedby={describedBy('amount')}
      />
      {fieldError('amount')}

      <label htmlFor="transaction-date">Data do {form.type === 'Income' ? 'recebimento' : 'pagamento'}</label>
      <input
        id="transaction-date"
        type="date"
        value={form.transactionDate}
        max={today}
        onChange={(event) => set('transactionDate', event.target.value)}
        aria-invalid={Boolean(errors.transactionDate)}
        aria-describedby={describedBy('transactionDate')}
      />
      {fieldError('transactionDate')}

      {fieldError('form')}
      <div className="form__actions">
        <button type="submit" className="button--primary" disabled={busy || (!hasActiveAccount && !transaction)}>
          {busy ? 'Salvando…' : 'Salvar'}
        </button>
        <button type="button" onClick={onCancel} disabled={busy}>
          Voltar
        </button>
      </div>
    </form>
  )
}
