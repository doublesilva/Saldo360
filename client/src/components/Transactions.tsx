import { useEffect, useState, type FormEvent } from 'react'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import { listCategories, type CategoryType, type FinancialCategory } from '../api/categories'
import {
  TRANSACTION_DESCRIPTION_MAX_LENGTH,
  cancelTransaction,
  createTransaction,
  emptyFilters,
  listTransactions,
  recategorizeTransaction,
  updateTransaction,
  type FinancialTransaction,
  type TransactionFilters,
  type TransactionPage,
} from '../api/transactions'
import { todayInSaoPaulo } from '../dates'
import { decimalToInput, formatBrlDecimal, formatDate, isZeroDecimal, parseAmount } from '../money'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

const typeLabels: Record<CategoryType, string> = { Income: 'Receita', Expense: 'Despesa' }

type Load<T> = { kind: 'loading' } | { kind: 'loaded'; data: T } | { kind: 'error'; message: string }

export type TransactionReferences = { accounts: FinancialAccount[]; categories: FinancialCategory[] }

/**
 * Lançamentos únicos já realizados da família: entradas e saídas efetivas. Contas e categorias são carregadas
 * com as inativas para que o histórico e os filtros continuem mostrando seus nomes.
 * `onChanged` avisa quem exibe saldos que um lançamento foi criado, editado ou cancelado.
 */
export function Transactions({
  workspaceId,
  onChanged,
  refreshKey = 0,
}: {
  workspaceId: string
  onChanged?: () => void
  /** Muda quando outra seção altera lançamentos (ex.: realizar previsão): a lista é recarregada. */
  refreshKey?: number
}) {
  const [refs, setRefs] = useState<Load<TransactionReferences>>({ kind: 'loading' })
  const [page, setPage] = useState<Load<TransactionPage>>({ kind: 'loading' })
  const [filters, setFilters] = useState<TransactionFilters>(emptyFilters)
  const [pageNumber, setPageNumber] = useState(1)
  const [reloadKey, setReloadKey] = useState(0)
  const [editing, setEditing] = useState<FinancialTransaction | 'new' | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [cancellingId, setCancellingId] = useState<string | null>(null)
  const [recategorizingId, setRecategorizingId] = useState<string | null>(null)

  // Contas e categorias mudam em outras seções: recarrega ao abrir o formulário e após salvar.
  useEffect(() => {
    const controller = new AbortController()
    Promise.all([
      listAccounts(workspaceId, true, controller.signal),
      listCategories(workspaceId, true, controller.signal),
    ])
      .then(([accounts, categories]) => setRefs({ kind: 'loaded', data: { accounts, categories } }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setRefs({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, reloadKey, editing])

  useEffect(() => {
    const controller = new AbortController()
    listTransactions(workspaceId, filters, pageNumber, controller.signal)
      .then((data) => setPage({ kind: 'loaded', data }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setPage({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, filters, pageNumber, reloadKey, refreshKey])

  const reload = () => setReloadKey((n) => n + 1)

  const changeFilter = <K extends keyof TransactionFilters>(field: K, value: TransactionFilters[K]) => {
    setFilters((current) => ({ ...current, [field]: value }))
    setPageNumber(1)
  }

  /** Corrige só a categoria interna de um lançamento importado; valor, data e descrição vêm do banco e não mudam. */
  const handleRecategorize = async (transaction: FinancialTransaction, categoryId: string) => {
    setRecategorizingId(transaction.id)
    setActionError(null)
    try {
      const result = await recategorizeTransaction(workspaceId, transaction.id, categoryId || null)
      if (!result.ok) setActionError(Object.values(result.errors).flat()[0] ?? 'Não foi possível alterar a categoria.')
      reload()
      onChanged?.()
    } catch (error: unknown) {
      setActionError(errorMessage(error))
    } finally {
      setRecategorizingId(null)
    }
  }

  const handleCancel = async (transaction: FinancialTransaction) => {
    const question =
      `Cancelar o lançamento "${transaction.description}" de ${formatDate(transaction.transactionDate)}? ` +
      'O registro é preservado (aparece em "Mostrar cancelados"), mas não poderá mais ser editado.' +
      (transaction.plannedTransactionId
        ? ' Ele realiza uma previsão: a previsão voltará a ficar pendente e poderá ser realizada de novo.'
        : '')
    if (!window.confirm(question)) return

    setCancellingId(transaction.id)
    setActionError(null)
    try {
      const result = await cancelTransaction(workspaceId, transaction.id)
      if (!result.ok) setActionError(Object.values(result.errors).flat()[0] ?? 'Não foi possível cancelar.')
      reload()
      onChanged?.()
    } catch (error: unknown) {
      setActionError(errorMessage(error))
    } finally {
      setCancellingId(null)
    }
  }

  if (editing !== null) {
    if (refs.kind !== 'loaded') {
      return <p className="status status--pending">Carregando contas e categorias…</p>
    }
    return (
      <TransactionForm
        workspaceId={workspaceId}
        references={refs.data}
        transaction={editing === 'new' ? null : editing}
        onSaved={() => {
          setEditing(null)
          reload()
          onChanged?.()
        }}
        onCancel={() => setEditing(null)}
      />
    )
  }

  const accounts = refs.kind === 'loaded' ? refs.data.accounts : []
  const categories = refs.kind === 'loaded' ? refs.data.categories : []
  const totalPages = page.kind === 'loaded' ? Math.max(1, Math.ceil(page.data.totalCount / page.data.pageSize)) : 1

  return (
    <div className="transactions">
      <div className="accounts__header">
        <h3>Lançamentos</h3>
        <button type="button" className="button--primary" onClick={() => setEditing('new')}>
          Novo lançamento
        </button>
      </div>
      <p className="meta">
        Registre aqui entradas e saídas que <strong>já aconteceram</strong>: recebimentos e pagamentos efetivos. Valores
        previstos, parcelas futuras e transferências entre contas (inclusive PJ → PF) terão fluxo próprio.
      </p>

      <fieldset className="transaction-filters">
        <legend>Filtros</legend>
        <label>
          De
          <input type="date" value={filters.from} onChange={(event) => changeFilter('from', event.target.value)} />
        </label>
        <label>
          Até
          <input type="date" value={filters.to} onChange={(event) => changeFilter('to', event.target.value)} />
        </label>
        <label>
          Tipo
          <select
            value={filters.type}
            onChange={(event) => changeFilter('type', event.target.value as CategoryType | '')}
          >
            <option value="">Todos</option>
            <option value="Income">Receitas</option>
            <option value="Expense">Despesas</option>
          </select>
        </label>
        <label>
          Conta
          <select value={filters.accountId} onChange={(event) => changeFilter('accountId', event.target.value)}>
            <option value="">Todas</option>
            {accounts.map((account) => (
              <option key={account.id} value={account.id}>
                {account.name}
                {account.isActive ? '' : ' (inativa)'}
              </option>
            ))}
          </select>
        </label>
        <label>
          Categoria
          <select value={filters.categoryId} onChange={(event) => changeFilter('categoryId', event.target.value)}>
            <option value="">Todas</option>
            {categories
              .filter((category) => !filters.type || category.type === filters.type)
              .map((category) => (
                <option key={category.id} value={category.id}>
                  {category.name}
                  {category.isActive ? '' : ' (inativa)'}
                </option>
              ))}
          </select>
        </label>
        <label className="checkbox">
          <input
            type="checkbox"
            checked={filters.includeCancelled}
            onChange={(event) => changeFilter('includeCancelled', event.target.checked)}
          />
          Mostrar cancelados
        </label>
      </fieldset>

      {actionError && (
        <p className="field-error" role="alert">
          {actionError}
        </p>
      )}
      {page.kind === 'loading' && <p className="status status--pending">Carregando lançamentos…</p>}
      {page.kind === 'error' && <p className="status status--down">{page.message}</p>}
      {page.kind === 'loaded' && page.data.items.length === 0 && (
        <p className="meta">Nenhum lançamento encontrado.</p>
      )}

      {page.kind === 'loaded' && page.data.items.length > 0 && (
        <>
          <ul className="transaction-list">
            {page.data.items.map((transaction) => (
              <li
                key={transaction.id}
                className={transaction.isCancelled ? 'transaction transaction--cancelled' : 'transaction'}
              >
                <div className="transaction__main">
                  <span className="transaction__description">{transaction.description}</span>
                  <Amount transaction={transaction} />
                </div>
                <div className="transaction__meta meta">
                  <span>{formatDate(transaction.transactionDate)}</span>
                  <span>{typeLabels[transaction.type]}</span>
                  <span>
                    {transaction.category ? transaction.category.name : 'Sem categoria'}
                    {transaction.category && !transaction.category.isActive ? ' (inativa)' : ''}
                  </span>
                  <span>
                    {transaction.account.name}
                    {transaction.account.isActive ? '' : ' (inativa)'}
                  </span>
                  {transaction.plannedTransactionId && <span className="badge badge--realization">Realiza previsão</span>}
                  {transaction.origin === 'PluggyImport' && <span className="badge badge--realization">Importado da Pluggy</span>}
                  {transaction.isCancelled && <span className="badge badge--cancelled">Cancelado</span>}
                </div>
                {!transaction.isCancelled && (
                  <div className="form__actions">
                    {transaction.origin === 'PluggyImport' && (
                      <label>
                        Categoria
                        <select
                          aria-label={`Categoria de ${transaction.description}`}
                          value={transaction.category?.id ?? ''}
                          disabled={recategorizingId === transaction.id}
                          onChange={(event) => void handleRecategorize(transaction, event.target.value)}
                        >
                          <option value="">Sem categoria</option>
                          {categories
                            .filter((category) => category.type === transaction.type && (category.isActive || category.id === transaction.category?.id))
                            .map((category) => (
                              <option key={category.id} value={category.id}>
                                {category.name}
                              </option>
                            ))}
                        </select>
                      </label>
                    )}
                    {/* Vinculado a previsão ou importado do banco não se edita: previsão se cancela e realiza de novo; importado só troca a categoria. */}
                    {!transaction.plannedTransactionId && transaction.origin !== 'PluggyImport' && (
                      <button type="button" onClick={() => setEditing(transaction)}>
                        Editar
                      </button>
                    )}
                    <button
                      type="button"
                      onClick={() => void handleCancel(transaction)}
                      disabled={cancellingId === transaction.id}
                    >
                      Cancelar lançamento
                    </button>
                  </div>
                )}
              </li>
            ))}
          </ul>
          <nav className="pagination" aria-label="Paginação dos lançamentos">
            <button type="button" onClick={() => setPageNumber((n) => n - 1)} disabled={pageNumber <= 1}>
              Anterior
            </button>
            <span className="meta">
              Página {page.data.page} de {totalPages} · {page.data.totalCount}{' '}
              {page.data.totalCount === 1 ? 'lançamento' : 'lançamentos'}
            </span>
            <button type="button" onClick={() => setPageNumber((n) => n + 1)} disabled={pageNumber >= totalPages}>
              Próxima
            </button>
          </nav>
        </>
      )}
    </div>
  )
}

/** Receita com "+", despesa com "−": só formatação do valor positivo, sem cálculo. */
function Amount({ transaction }: { transaction: FinancialTransaction }) {
  const income = transaction.type === 'Income'
  return (
    <strong className={income ? 'amount amount--income' : 'amount amount--negative'}>
      {income ? '+' : '−'}
      {formatBrlDecimal(transaction.amount)}
    </strong>
  )
}

type FormState = {
  type: CategoryType
  accountId: string
  categoryId: string
  description: string
  amount: string
  transactionDate: string
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
        }
      : { type: 'Expense', accountId: '', categoryId: '', description: '', amount: '', transactionDate: today },
  )
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)

  const set = <K extends keyof FormState>(field: K, value: FormState[K]) =>
    setForm((current) => ({ ...current, [field]: value }))

  // Ativas, mais a conta/categoria inativa que o lançamento editado já usa (pode ser mantida).
  const accountOptions = references.accounts.filter(
    (account) => account.isActive || account.id === transaction?.account.id,
  )
  const categoryOptions = references.categories.filter(
    (category) =>
      category.type === form.type && (category.isActive || category.id === transaction?.category?.id),
  )
  const hasActiveAccount = references.accounts.some((account) => account.isActive)
  const hasActiveCategory = references.categories.some((category) => category.isActive && category.type === form.type)
  const singular = form.type === 'Income' ? 'receita' : 'despesa'

  const changeType = (type: CategoryType) =>
    setForm((current) => ({
      ...current,
      type,
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
        {categoryOptions.map((category) => (
          <option key={category.id} value={category.id}>
            {category.name}
            {category.isActive ? '' : ' — inativa'}
          </option>
        ))}
      </select>
      {fieldError('categoryId')}
      {fieldError('type')}

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
