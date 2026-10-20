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
  realizePlanned,
  updatePlanned,
  type PlannedFilters,
  type PlannedPage,
  type PlannedTransaction,
} from '../api/planned'
import { getTransaction, type FinancialTransaction } from '../api/transactions'
import { todayInSaoPaulo } from '../dates'
import { decimalToInput, formatBrlDecimal, formatDate, isZeroDecimal, parseAmount } from '../money'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

const typeLabels: Record<CategoryType, string> = { Income: 'Receita prevista', Expense: 'Despesa prevista' }

type Load<T> = { kind: 'loading' } | { kind: 'loaded'; data: T } | { kind: 'error'; message: string }

export type PlannedReferences = { accounts: FinancialAccount[]; categories: FinancialCategory[] }

/**
 * Receitas e despesas previstas únicas: o que se espera receber ou pagar. Pendentes ficam fora do saldo e,
 * vencidas, continuam pendentes ("Vencida" é calculado na tela com a data de hoje em America/Sao_Paulo e não é
 * gravado). "Realizar" cria o lançamento efetivo vinculado. `onChanged` avisa saldos, projeções e lançamentos a
 * cada mudança (criar, editar, cancelar ou realizar). `refreshKey` muda quando outra seção mexe nas previsões
 * (ex.: cancelar o lançamento efetivo reabre a previsão).
 */
export function Planned({
  workspaceId,
  onChanged,
  refreshKey = 0,
}: {
  workspaceId: string
  onChanged?: () => void
  refreshKey?: number
}) {
  const [refs, setRefs] = useState<Load<PlannedReferences>>({ kind: 'loading' })
  const [page, setPage] = useState<Load<PlannedPage>>({ kind: 'loading' })
  const [filters, setFilters] = useState<PlannedFilters>(emptyPlannedFilters)
  const [pageNumber, setPageNumber] = useState(1)
  const [reloadKey, setReloadKey] = useState(0)
  const [editing, setEditing] = useState<PlannedTransaction | 'new' | null>(null)
  const [realizing, setRealizing] = useState<PlannedTransaction | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [cancellingId, setCancellingId] = useState<string | null>(null)
  const today = todayInSaoPaulo()

  // Contas e categorias mudam em outras seções: recarrega ao abrir o formulário e após salvar.
  useEffect(() => {
    const controller = new AbortController()
    Promise.all([listAccounts(workspaceId, true, controller.signal), listCategories(workspaceId, true, controller.signal)])
      .then(([accounts, categories]) => setRefs({ kind: 'loaded', data: { accounts, categories } }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setRefs({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, reloadKey, editing])

  useEffect(() => {
    const controller = new AbortController()
    listPlanned(workspaceId, filters, pageNumber, controller.signal)
      .then((data) => setPage({ kind: 'loaded', data }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setPage({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, filters, pageNumber, reloadKey, refreshKey])

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
      const result = await cancelPlanned(workspaceId, planned.id)
      if (!result.ok) setActionError(Object.values(result.errors).flat()[0] ?? 'Não foi possível cancelar.')
      reload()
      onChanged?.() // muda a projeção
    } catch (error: unknown) {
      setActionError(errorMessage(error))
    } finally {
      setCancellingId(null)
    }
  }

  if (realizing !== null) {
    return (
      <RealizeForm
        workspaceId={workspaceId}
        planned={realizing}
        onRealized={() => {
          setRealizing(null)
          reload()
          onChanged?.()
        }}
        onCancel={() => setRealizing(null)}
      />
    )
  }

  if (editing !== null) {
    if (refs.kind !== 'loaded') return <p className="status status--pending">Carregando contas e categorias…</p>
    return (
      <PlannedForm
        workspaceId={workspaceId}
        references={refs.data}
        planned={editing === 'new' ? null : editing}
        onSaved={() => {
          setEditing(null)
          reload()
          onChanged?.() // muda a projeção
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
        Valores que você <strong>espera</strong> receber ou pagar. Previsões pendentes não afetam o saldo realizado: uma
        previsão vencida continua pendente até você clicar em <strong>Realizar</strong>, que registra o lançamento
        efetivo (com o valor e a data reais) e atualiza o saldo.
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
            checked={filters.includeRealized}
            onChange={(event) => changeFilter('includeRealized', event.target.checked)}
          />
          Mostrar realizadas
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
              if (planned.status === 'Cancelled') classes.push('transaction--cancelled')
              if (planned.status === 'Realized') classes.push('planned-item--realized')
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
                    {planned.status === 'Realized' && <span className="badge badge--realized">Realizada</span>}
                    {planned.status === 'Cancelled' && <span className="badge badge--cancelled">Cancelada</span>}
                  </div>
                  {planned.realization && <RealizationSummary workspaceId={workspaceId} planned={planned} />}
                  {planned.status === 'Pending' && (
                    <div className="form__actions">
                      <button type="button" className="button--primary" onClick={() => setRealizing(planned)}>
                        Realizar
                      </button>
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

/**
 * Previsão realizada: valor previsto e valor efetivo lado a lado (nunca somados ou subtraídos aqui), data efetiva
 * e acesso ao lançamento efetivo, carregado sob demanda.
 */
function RealizationSummary({ workspaceId, planned }: { workspaceId: string; planned: PlannedTransaction }) {
  const [details, setDetails] = useState<Load<FinancialTransaction> | null>(null)
  const realization = planned.realization!

  const toggle = () => {
    if (details) {
      setDetails(null)
      return
    }

    setDetails({ kind: 'loading' })
    getTransaction(workspaceId, realization.transactionId)
      .then((data) => setDetails({ kind: 'loaded', data }))
      .catch((error: unknown) => setDetails({ kind: 'error', message: errorMessage(error) }))
  }

  return (
    <div className="planned-realization">
      <p>
        Previsto <strong className="amount">{formatBrlDecimal(planned.amount)}</strong> · Efetivo{' '}
        <strong className="amount">{formatBrlDecimal(realization.amount)}</strong> em{' '}
        {formatDate(realization.transactionDate)}
      </p>
      <button type="button" className="button--link" onClick={toggle} aria-expanded={details !== null}>
        {details ? 'Ocultar lançamento' : 'Ver lançamento'}
      </button>
      {details?.kind === 'loading' && <p className="meta">Carregando lançamento…</p>}
      {details?.kind === 'error' && <p className="status status--down">{details.message}</p>}
      {details?.kind === 'loaded' && (
        <p className="meta">
          Lançamento efetivo: {details.data.description} · {formatBrlDecimal(details.data.amount)} ·{' '}
          {formatDate(details.data.transactionDate)} · {details.data.account.name} · {details.data.category.name}. Para
          corrigir, cancele-o em Lançamentos: a previsão volta a ficar pendente.
        </p>
      )}
    </div>
  )
}

/**
 * Realização: valor e data efetivos começam com o valor previsto e hoje (America/Sao_Paulo); ambos podem mudar.
 * Conta, categoria, tipo e descrição vêm da previsão. Pede confirmação antes de criar o lançamento efetivo.
 */
export function RealizeForm({
  workspaceId,
  planned,
  onRealized,
  onCancel,
}: {
  workspaceId: string
  planned: PlannedTransaction
  onRealized: () => void
  onCancel: () => void
}) {
  const today = todayInSaoPaulo()
  const [amountText, setAmountText] = useState(decimalToInput(planned.amount))
  const [date, setDate] = useState(today)
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)
  const singular = planned.type === 'Income' ? 'recebimento' : 'pagamento'

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const clientErrors: Record<string, string[]> = {}
    const amount = parseAmount(amountText)
    if (amount.kind === 'empty') clientErrors.amount = ['Informe o valor efetivo.']
    else if (amount.kind === 'invalid') clientErrors.amount = [amount.message]
    else if (amount.text.startsWith('-') || isZeroDecimal(amount.text)) clientErrors.amount = ['O valor deve ser maior que zero.']
    if (!date) clientErrors.transactionDate = ['Informe a data efetiva.']
    else if (date > today) clientErrors.transactionDate = ['A data efetiva não pode ser futura.']

    if (Object.keys(clientErrors).length > 0 || amount.kind !== 'ok') {
      setErrors(clientErrors)
      return
    }

    const question =
      `Realizar "${planned.description}" por ${formatBrlDecimal(toDecimalText(amount.text))} em ${formatDate(date)}? ` +
      `Será criado um lançamento efetivo na conta ${planned.account.name}, que passa a contar no saldo. ` +
      `O valor previsto (${formatBrlDecimal(planned.amount)}) é mantido na previsão.`
    if (!window.confirm(question)) return

    setBusy(true)
    setErrors({})
    try {
      const result = await realizePlanned(workspaceId, planned.id, { amount: amount.text, transactionDate: date })
      if (result.ok) onRealized()
      else setErrors(result.errors)
    } catch (error: unknown) {
      setErrors({ form: [errorMessage(error)] })
    } finally {
      setBusy(false)
    }
  }

  const fieldError = (field: string) =>
    errors[field]?.[0] && (
      <p id={`realize-${field}-error`} className="field-error" role="alert">
        {errors[field][0]}
      </p>
    )

  return (
    <form className="form planned" onSubmit={handleSubmit} noValidate>
      <h3>Realizar previsão</h3>
      <p>
        <strong>{planned.description}</strong> — {typeLabels[planned.type]} de {formatBrlDecimal(planned.amount)} prevista
        para {formatDate(planned.dueDate)}, em {planned.account.name} ({planned.category.name}).
      </p>
      <p className="meta">
        Informe o valor e a data em que o {singular} realmente aconteceu. Eles podem diferir do previsto; a previsão
        guarda o valor original.
      </p>
      {fieldError('accountId')}
      {fieldError('categoryId')}

      <label htmlFor="realize-amount">Valor efetivo (R$)</label>
      <input
        id="realize-amount"
        value={amountText}
        onChange={(event) => setAmountText(event.target.value)}
        inputMode="decimal"
        autoComplete="off"
        aria-invalid={Boolean(errors.amount)}
        aria-describedby={errors.amount ? 'realize-amount-error' : undefined}
      />
      {fieldError('amount')}

      <label htmlFor="realize-date">Data efetiva</label>
      <input
        id="realize-date"
        type="date"
        value={date}
        max={today}
        onChange={(event) => setDate(event.target.value)}
        aria-invalid={Boolean(errors.transactionDate)}
        aria-describedby={errors.transactionDate ? 'realize-transactionDate-error' : undefined}
      />
      {fieldError('transactionDate')}

      {fieldError('form')}
      <div className="form__actions">
        <button type="submit" className="button--primary" disabled={busy}>
          {busy ? 'Realizando…' : 'Realizar'}
        </button>
        <button type="button" onClick={onCancel} disabled={busy}>
          Voltar
        </button>
      </div>
    </form>
  )
}

/** Texto de parseAmount ("210", "0.5") → decimal com duas casas ("210.00", "0.50") só para exibir na confirmação. */
function toDecimalText(text: string): string {
  const negative = text.startsWith('-')
  const [integer, decimals = ''] = (negative ? text.slice(1) : text).split('.')
  return `${negative ? '-' : ''}${integer.replace(/^0+(?=\d)/, '') || '0'}.${decimals.padEnd(2, '0')}`
}

type FormState = {
  type: CategoryType
  accountId: string
  categoryId: string
  description: string
  amount: string
  dueDate: string
}

export function PlannedForm({
  workspaceId,
  references,
  planned,
  onSaved,
  onCancel,
}: {
  workspaceId: string
  references: PlannedReferences
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
      const result = planned ? await updatePlanned(workspaceId, planned.id, input) : await createPlanned(workspaceId, input)
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
