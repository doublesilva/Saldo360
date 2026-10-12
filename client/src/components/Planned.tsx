import { useEffect, useState, type FormEvent } from 'react'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import { listCategories, type CategoryType, type FinancialCategory } from '../api/categories'
import {
  PLANNED_DESCRIPTION_MAX_LENGTH,
  cancelPlanned,
  createPlanned,
  emptyPlannedFilters,
  isOverdue,
  listPlanned,
  updatePlanned,
  type PlannedFilters,
  type PlannedPage,
  type PlannedTransaction,
} from '../api/planned'
import { todayInSaoPaulo } from '../dates'
import { decimalToInput, formatBrlDecimal, formatDate, isZeroDecimal, parseAmount } from '../money'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

const typeLabels: Record<CategoryType, string> = { Income: 'Receita prevista', Expense: 'Despesa prevista' }

type Load<T> = { kind: 'loading' } | { kind: 'loaded'; data: T } | { kind: 'error'; message: string }

type References = { accounts: FinancialAccount[]; categories: FinancialCategory[] }

/**
 * Receitas e despesas previstas únicas: o que se espera receber ou pagar. Ficam fora do saldo realizado (nada
 * aqui avisa os saldos) e, vencidas, continuam pendentes. "Vencida" é calculado na tela com a data de hoje em
 * America/Sao_Paulo e não é gravado.
 */
export function Planned({ familyId }: { familyId: string }) {
  const [refs, setRefs] = useState<Load<References>>({ kind: 'loading' })
  const [page, setPage] = useState<Load<PlannedPage>>({ kind: 'loading' })
  const [filters, setFilters] = useState<PlannedFilters>(emptyPlannedFilters)
  const [pageNumber, setPageNumber] = useState(1)
  const [reloadKey, setReloadKey] = useState(0)
  const [editing, setEditing] = useState<PlannedTransaction | 'new' | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [cancellingId, setCancellingId] = useState<string | null>(null)
  const today = todayInSaoPaulo()

  // Contas e categorias mudam em outras seções: recarrega ao abrir o formulário e após salvar.
  useEffect(() => {
    const controller = new AbortController()
    Promise.all([listAccounts(familyId, true, controller.signal), listCategories(familyId, true, controller.signal)])
      .then(([accounts, categories]) => setRefs({ kind: 'loaded', data: { accounts, categories } }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setRefs({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, reloadKey, editing])

  useEffect(() => {
    const controller = new AbortController()
    listPlanned(familyId, filters, pageNumber, controller.signal)
      .then((data) => setPage({ kind: 'loaded', data }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setPage({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, filters, pageNumber, reloadKey])

  const reload = () => setReloadKey((n) => n + 1)

  const changeFilter = <K extends keyof PlannedFilters>(field: K, value: PlannedFilters[K]) => {
    setFilters((current) => ({ ...current, [field]: value }))
    setPageNumber(1)
  }

  const handleCancel = async (planned: PlannedTransaction) => {
    const question =
      `Cancelar a previsão "${planned.description}" de ${formatDate(planned.dueDate)}? ` +
      'O registro é preservado (aparece em "Mostrar canceladas"), mas não poderá mais ser editado.'
    if (!window.confirm(question)) return

    setCancellingId(planned.id)
    setActionError(null)
    try {
      const result = await cancelPlanned(familyId, planned.id)
      if (!result.ok) setActionError(Object.values(result.errors).flat()[0] ?? 'Não foi possível cancelar.')
      reload()
    } catch (error: unknown) {
      setActionError(errorMessage(error))
    } finally {
      setCancellingId(null)
    }
  }

  if (editing !== null) {
    if (refs.kind !== 'loaded') return <p className="status status--pending">Carregando contas e categorias…</p>
    return (
      <PlannedForm
        familyId={familyId}
        references={refs.data}
        planned={editing === 'new' ? null : editing}
        onSaved={() => {
          setEditing(null)
          reload()
        }}
        onCancel={() => setEditing(null)}
      />
    )
  }

  const accounts = refs.kind === 'loaded' ? refs.data.accounts : []
  const categories = refs.kind === 'loaded' ? refs.data.categories : []
  const totalPages = page.kind === 'loaded' ? Math.max(1, Math.ceil(page.data.totalCount / page.data.pageSize)) : 1

  return (
    <div className="planned">
      <div className="accounts__header">
        <h3>Previsões</h3>
        <button type="button" className="button--primary" onClick={() => setEditing('new')}>
          Nova previsão
        </button>
      </div>
      <p className="meta">
        Valores que você <strong>espera</strong> receber ou pagar. Previsões não afetam o saldo realizado: uma previsão
        vencida continua pendente até que você registre o lançamento realizado em Lançamentos.
      </p>

      <fieldset className="transaction-filters">
        <legend>Filtros</legend>
        <label>
          Prevista de
          <input type="date" value={filters.from} onChange={(event) => changeFilter('from', event.target.value)} />
        </label>
        <label>
          Prevista até
          <input type="date" value={filters.to} onChange={(event) => changeFilter('to', event.target.value)} />
        </label>
        <label>
          Tipo
          <select value={filters.type} onChange={(event) => changeFilter('type', event.target.value as CategoryType | '')}>
            <option value="">Todos</option>
            <option value="Income">Receitas previstas</option>
            <option value="Expense">Despesas previstas</option>
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
          Mostrar canceladas
        </label>
      </fieldset>

      {actionError && (
        <p className="field-error" role="alert">
          {actionError}
        </p>
      )}
      {page.kind === 'loading' && <p className="status status--pending">Carregando previsões…</p>}
      {page.kind === 'error' && <p className="status status--down">{page.message}</p>}
      {page.kind === 'loaded' && page.data.items.length === 0 && <p className="meta">Nenhuma previsão encontrada.</p>}

      {page.kind === 'loaded' && page.data.items.length > 0 && (
        <>
          <ul className="transaction-list">
            {page.data.items.map((planned) => {
              const overdue = isOverdue(planned, today)
              const classes = ['transaction', 'planned-item']
              if (planned.isCancelled) classes.push('transaction--cancelled')
              if (overdue) classes.push('planned-item--overdue')
              return (
                <li key={planned.id} className={classes.join(' ')}>
                  <div className="transaction__main">
                    <span className="transaction__description">{planned.description}</span>
                    <strong className={planned.type === 'Income' ? 'amount amount--income' : 'amount amount--negative'}>
                      {planned.type === 'Income' ? '+' : '−'}
                      {formatBrlDecimal(planned.amount)}
                    </strong>
                  </div>
                  <div className="transaction__meta meta">
                    <span>Data prevista: {formatDate(planned.dueDate)}</span>
                    <span>{typeLabels[planned.type]}</span>
                    <span>
                      {planned.category.name}
                      {planned.category.isActive ? '' : ' (inativa)'}
                    </span>
                    <span>
                      {planned.account.name}
                      {planned.account.isActive ? '' : ' (inativa)'}
                    </span>
                    {overdue && <span className="badge badge--overdue">Vencida</span>}
                    {planned.isCancelled && <span className="badge badge--cancelled">Cancelada</span>}
                  </div>
                  {!planned.isCancelled && (
                    <div className="form__actions">
                      <button type="button" onClick={() => setEditing(planned)}>
                        Editar
                      </button>
                      <button
                        type="button"
                        onClick={() => void handleCancel(planned)}
                        disabled={cancellingId === planned.id}
                      >
                        Cancelar previsão
                      </button>
                    </div>
                  )}
                </li>
              )
            })}
          </ul>
          <nav className="pagination" aria-label="Paginação das previsões">
            <button type="button" onClick={() => setPageNumber((n) => n - 1)} disabled={pageNumber <= 1}>
              Anterior
            </button>
            <span className="meta">
              Página {page.data.page} de {totalPages} · {page.data.totalCount}{' '}
              {page.data.totalCount === 1 ? 'previsão' : 'previsões'}
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

type FormState = {
  type: CategoryType
  accountId: string
  categoryId: string
  description: string
  amount: string
  dueDate: string
}

function PlannedForm({
  familyId,
  references,
  planned,
  onSaved,
  onCancel,
}: {
  familyId: string
  references: References
  planned: PlannedTransaction | null
  onSaved: () => void
  onCancel: () => void
}) {
  const [form, setForm] = useState<FormState>(
    planned
      ? {
          type: planned.type,
          accountId: planned.account.id,
          categoryId: planned.category.id,
          description: planned.description,
          amount: decimalToInput(planned.amount),
          dueDate: planned.dueDate,
        }
      : { type: 'Expense', accountId: '', categoryId: '', description: '', amount: '', dueDate: '' },
  )
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)

  const set = <K extends keyof FormState>(field: K, value: FormState[K]) => setForm((current) => ({ ...current, [field]: value }))

  // Ativas, mais a conta/categoria inativa que a previsão editada já usa (pode ser mantida).
  const accountOptions = references.accounts.filter((account) => account.isActive || account.id === planned?.account.id)
  const categoryOptions = references.categories.filter(
    (category) => category.type === form.type && (category.isActive || category.id === planned?.category.id),
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
    if (!form.dueDate) clientErrors.dueDate = ['Informe a data prevista.']

    const amount = parseAmount(form.amount)
    if (amount.kind === 'empty') clientErrors.amount = ['Informe o valor.']
    else if (amount.kind === 'invalid') clientErrors.amount = [amount.message]
    else if (amount.text.startsWith('-') || isZeroDecimal(amount.text))
      clientErrors.amount = ['O valor deve ser maior que zero. Para saídas, escolha o tipo despesa prevista.']

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
        dueDate: form.dueDate,
      }
      const result = planned ? await updatePlanned(familyId, planned.id, input) : await createPlanned(familyId, input)
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
      <p id={`planned-${field}-error`} className="field-error" role="alert">
        {errors[field][0]}
      </p>
    )
  const describedBy = (field: string) => (errors[field] ? `planned-${field}-error` : undefined)

  return (
    <form className="form planned" onSubmit={handleSubmit} noValidate>
      <h3>{planned ? 'Editar previsão' : 'Nova previsão'}</h3>
      <p className="meta">
        Previsão não é movimentação: não entra no saldo realizado e não vira lançamento sozinha. A data prevista pode
        ser passada, hoje ou futura.
      </p>

      <fieldset className="transaction-type">
        <legend>Tipo</legend>
        {(['Income', 'Expense'] as CategoryType[]).map((type) => (
          <label key={type} className="checkbox">
            <input type="radio" name="planned-type" value={type} checked={form.type === type} onChange={() => changeType(type)} />
            {type === 'Income' ? 'Receita prevista' : 'Despesa prevista'}
          </label>
        ))}
      </fieldset>

      {!hasActiveAccount && (
        <p className="status status--pending" role="status">
          Nenhuma conta ativa. Cadastre ou reative uma conta na seção Contas.
        </p>
      )}
      {!hasActiveCategory && (
        <p className="status status--pending" role="status">
          Nenhuma categoria de {singular} ativa. Cadastre ou reative uma na seção Categorias.
        </p>
      )}

      <label htmlFor="planned-account">Conta</label>
      <select
        id="planned-account"
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

      <label htmlFor="planned-category">Categoria</label>
      <select
        id="planned-category"
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

      <label htmlFor="planned-description">Descrição</label>
      <input
        id="planned-description"
        value={form.description}
        onChange={(event) => set('description', event.target.value)}
        maxLength={PLANNED_DESCRIPTION_MAX_LENGTH}
        placeholder={form.type === 'Income' ? 'Ex.: Salário de novembro' : 'Ex.: Aluguel de novembro'}
        autoComplete="off"
        aria-invalid={Boolean(errors.description)}
        aria-describedby={describedBy('description')}
      />
      {fieldError('description')}

      <label htmlFor="planned-amount">Valor (R$)</label>
      <input
        id="planned-amount"
        value={form.amount}
        onChange={(event) => set('amount', event.target.value)}
        inputMode="decimal"
        placeholder="Ex.: 1.234,56"
        autoComplete="off"
        aria-invalid={Boolean(errors.amount)}
        aria-describedby={describedBy('amount')}
      />
      {fieldError('amount')}

      <label htmlFor="planned-due-date">Data prevista</label>
      <input
        id="planned-due-date"
        type="date"
        value={form.dueDate}
        onChange={(event) => set('dueDate', event.target.value)}
        aria-invalid={Boolean(errors.dueDate)}
        aria-describedby={describedBy('dueDate')}
      />
      {fieldError('dueDate')}

      {fieldError('form')}
      <div className="form__actions">
        <button type="submit" className="button--primary" disabled={busy || (!hasActiveAccount && !planned)}>
          {busy ? 'Salvando…' : 'Salvar'}
        </button>
        <button type="button" onClick={onCancel} disabled={busy}>
          Voltar
        </button>
      </div>
    </form>
  )
}
