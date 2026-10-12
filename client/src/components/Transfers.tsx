import { useEffect, useState, type FormEvent } from 'react'
import { listAccounts, type FinancialAccount } from '../api/accounts'
import {
  TRANSFER_DESCRIPTION_MAX_LENGTH,
  cancelTransfer,
  createTransfer,
  emptyTransferFilters,
  listTransfers,
  type FinancialTransfer,
  type TransferAccount,
  type TransferFilters,
  type TransferPage,
} from '../api/transfers'
import { todayInSaoPaulo } from '../dates'
import { formatBrlDecimal, formatDate, parseAmount } from '../money'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

type Load<T> = { kind: 'loading' } | { kind: 'loaded'; data: T } | { kind: 'error'; message: string }

function accountLabel(account: TransferAccount): string {
  return `${account.name} (${account.financialScope})${account.isActive ? '' : ' — inativa'}`
}

/**
 * Transferências já realizadas entre contas da família (PF ↔ PJ ou mesmo escopo). Não são receita nem
 * despesa: movem saldo da origem para o destino. Não há edição: para corrigir, cancelar e cadastrar de novo.
 * `onChanged` avisa quem exibe saldos que uma transferência foi criada ou cancelada.
 */
export function Transfers({ familyId, onChanged }: { familyId: string; onChanged?: () => void }) {
  const [accounts, setAccounts] = useState<Load<FinancialAccount[]>>({ kind: 'loading' })
  const [page, setPage] = useState<Load<TransferPage>>({ kind: 'loading' })
  const [filters, setFilters] = useState<TransferFilters>(emptyTransferFilters)
  const [pageNumber, setPageNumber] = useState(1)
  const [reloadKey, setReloadKey] = useState(0)
  const [creating, setCreating] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)
  const [cancellingId, setCancellingId] = useState<string | null>(null)

  // Contas mudam em outra seção: recarrega ao abrir o formulário e após salvar.
  useEffect(() => {
    const controller = new AbortController()
    listAccounts(familyId, true, controller.signal)
      .then((data) => setAccounts({ kind: 'loaded', data }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setAccounts({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, reloadKey, creating])

  useEffect(() => {
    const controller = new AbortController()
    listTransfers(familyId, filters, pageNumber, controller.signal)
      .then((data) => setPage({ kind: 'loaded', data }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setPage({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, filters, pageNumber, reloadKey])

  const reload = () => setReloadKey((n) => n + 1)

  const changeFilter = <K extends keyof TransferFilters>(field: K, value: TransferFilters[K]) => {
    setFilters((current) => ({ ...current, [field]: value }))
    setPageNumber(1)
  }

  const handleCancel = async (transfer: FinancialTransfer) => {
    const question =
      `Cancelar a transferência de ${formatBrlDecimal(transfer.amount)} de ${transfer.sourceAccount.name} para ` +
      `${transfer.destinationAccount.name} (${formatDate(transfer.transferDate)})? Os saldos das duas contas voltam ` +
      'ao que eram; o registro é preservado (aparece em "Mostrar canceladas").'
    if (!window.confirm(question)) return

    setCancellingId(transfer.id)
    setActionError(null)
    try {
      const result = await cancelTransfer(familyId, transfer.id)
      if (!result.ok) setActionError(Object.values(result.errors).flat()[0] ?? 'Não foi possível cancelar.')
      reload()
      onChanged?.()
    } catch (error: unknown) {
      setActionError(errorMessage(error))
    } finally {
      setCancellingId(null)
    }
  }

  if (creating) {
    if (accounts.kind !== 'loaded') return <p className="status status--pending">Carregando contas…</p>
    return (
      <TransferForm
        familyId={familyId}
        accounts={accounts.data}
        onSaved={() => {
          setCreating(false)
          reload()
          onChanged?.()
        }}
        onCancel={() => setCreating(false)}
      />
    )
  }

  const accountOptions = accounts.kind === 'loaded' ? accounts.data : []
  const totalPages = page.kind === 'loaded' ? Math.max(1, Math.ceil(page.data.totalCount / page.data.pageSize)) : 1

  return (
    <div className="transfers">
      <div className="accounts__header">
        <h3>Transferências</h3>
        <button type="button" className="button--primary" onClick={() => setCreating(true)}>
          Nova transferência
        </button>
      </div>
      <p className="meta">
        Dinheiro que <strong>já passou</strong> de uma conta sua para outra (por exemplo, PJ → PF). Não é receita nem
        despesa: sai da origem e entra no destino. Para corrigir, cancele e cadastre de novo.
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
          Conta (origem ou destino)
          <select value={filters.accountId} onChange={(event) => changeFilter('accountId', event.target.value)}>
            <option value="">Todas</option>
            {accountOptions.map((account) => (
              <option key={account.id} value={account.id}>
                {account.name}
                {account.isActive ? '' : ' (inativa)'}
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
      {page.kind === 'loading' && <p className="status status--pending">Carregando transferências…</p>}
      {page.kind === 'error' && <p className="status status--down">{page.message}</p>}
      {page.kind === 'loaded' && page.data.items.length === 0 && <p className="meta">Nenhuma transferência encontrada.</p>}

      {page.kind === 'loaded' && page.data.items.length > 0 && (
        <>
          <ul className="transaction-list">
            {page.data.items.map((transfer) => (
              <li key={transfer.id} className={transfer.isCancelled ? 'transaction transaction--cancelled' : 'transaction'}>
                <div className="transaction__main">
                  <span className="transaction__description">
                    De {accountLabel(transfer.sourceAccount)} para {accountLabel(transfer.destinationAccount)}
                  </span>
                  <strong className="amount amount--transfer">{formatBrlDecimal(transfer.amount)}</strong>
                </div>
                <div className="transaction__meta meta">
                  <span>{formatDate(transfer.transferDate)}</span>
                  <span>{transfer.description}</span>
                  {transfer.isCancelled && <span className="badge badge--cancelled">Cancelada</span>}
                </div>
                {!transfer.isCancelled && (
                  <div className="form__actions">
                    <button
                      type="button"
                      onClick={() => void handleCancel(transfer)}
                      disabled={cancellingId === transfer.id}
                    >
                      Cancelar transferência
                    </button>
                  </div>
                )}
              </li>
            ))}
          </ul>
          <nav className="pagination" aria-label="Paginação das transferências">
            <button type="button" onClick={() => setPageNumber((n) => n - 1)} disabled={pageNumber <= 1}>
              Anterior
            </button>
            <span className="meta">
              Página {page.data.page} de {totalPages} · {page.data.totalCount}{' '}
              {page.data.totalCount === 1 ? 'transferência' : 'transferências'}
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

type FormState = { sourceAccountId: string; destinationAccountId: string; amount: string; transferDate: string; description: string }

function TransferForm({
  familyId,
  accounts,
  onSaved,
  onCancel,
}: {
  familyId: string
  accounts: FinancialAccount[]
  onSaved: () => void
  onCancel: () => void
}) {
  const today = todayInSaoPaulo()
  const [form, setForm] = useState<FormState>({
    sourceAccountId: '',
    destinationAccountId: '',
    amount: '',
    transferDate: today,
    description: '',
  })
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)

  const active = accounts.filter((account) => account.isActive)
  const set = <K extends keyof FormState>(field: K, value: FormState[K]) => setForm((current) => ({ ...current, [field]: value }))

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const clientErrors: Record<string, string[]> = {}
    if (!form.sourceAccountId) clientErrors.sourceAccountId = ['Escolha a conta de origem.']
    if (!form.destinationAccountId) clientErrors.destinationAccountId = ['Escolha a conta de destino.']
    else if (form.destinationAccountId === form.sourceAccountId)
      clientErrors.destinationAccountId = ['A conta de destino deve ser diferente da origem.']
    if (form.description.trim().length === 0) clientErrors.description = ['Informe a descrição.']
    if (!form.transferDate) clientErrors.transferDate = ['Informe a data da transferência.']
    else if (form.transferDate > today)
      clientErrors.transferDate = ['A data não pode ser futura: registre só transferências já realizadas.']

    const amount = parseAmount(form.amount)
    if (amount.kind === 'empty') clientErrors.amount = ['Informe o valor.']
    else if (amount.kind === 'invalid') clientErrors.amount = [amount.message]
    else if (amount.text.startsWith('-') || /^0*(\.0*)?$/.test(amount.text)) clientErrors.amount = ['O valor deve ser maior que zero.']

    if (Object.keys(clientErrors).length > 0 || amount.kind !== 'ok') {
      setErrors(clientErrors)
      return
    }

    setBusy(true)
    setErrors({})
    try {
      const result = await createTransfer(familyId, {
        sourceAccountId: form.sourceAccountId,
        destinationAccountId: form.destinationAccountId,
        amount: amount.text,
        transferDate: form.transferDate,
        description: form.description.trim(),
      })
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
      <p id={`transfer-${field}-error`} className="field-error" role="alert">
        {errors[field][0]}
      </p>
    )
  const describedBy = (field: string) => (errors[field] ? `transfer-${field}-error` : undefined)

  const accountSelect = (field: 'sourceAccountId' | 'destinationAccountId', id: string, placeholder: string) => (
    <select
      id={id}
      value={form[field]}
      onChange={(event) => set(field, event.target.value)}
      aria-invalid={Boolean(errors[field])}
      aria-describedby={describedBy(field)}
    >
      <option value="">{placeholder}</option>
      {active.map((account) => (
        <option key={account.id} value={account.id}>
          {account.name} ({account.financialScope})
        </option>
      ))}
    </select>
  )

  return (
    <form className="form transfers" onSubmit={handleSubmit} noValidate>
      <h3>Nova transferência</h3>
      <p className="meta">
        Só transferências já realizadas, entre contas da família. A origem pode ficar negativa: o Saldo360 não bloqueia
        por saldo insuficiente. Não há categoria: transferência não é receita nem despesa.
      </p>

      {active.length < 2 && (
        <p className="status status--pending" role="status">
          São necessárias duas contas ativas. Cadastre ou reative contas na seção Contas.
        </p>
      )}

      <label htmlFor="transfer-source">De (origem)</label>
      {accountSelect('sourceAccountId', 'transfer-source', 'Escolha a conta de origem')}
      {fieldError('sourceAccountId')}

      <label htmlFor="transfer-destination">Para (destino)</label>
      {accountSelect('destinationAccountId', 'transfer-destination', 'Escolha a conta de destino')}
      {fieldError('destinationAccountId')}

      <label htmlFor="transfer-amount">Valor (R$)</label>
      <input
        id="transfer-amount"
        value={form.amount}
        onChange={(event) => set('amount', event.target.value)}
        inputMode="decimal"
        placeholder="Ex.: 500,00"
        autoComplete="off"
        aria-invalid={Boolean(errors.amount)}
        aria-describedby={describedBy('amount')}
      />
      {fieldError('amount')}

      <label htmlFor="transfer-description">Descrição</label>
      <input
        id="transfer-description"
        value={form.description}
        onChange={(event) => set('description', event.target.value)}
        maxLength={TRANSFER_DESCRIPTION_MAX_LENGTH}
        placeholder="Ex.: Pró-labore de outubro"
        autoComplete="off"
        aria-invalid={Boolean(errors.description)}
        aria-describedby={describedBy('description')}
      />
      {fieldError('description')}

      <label htmlFor="transfer-date">Data da transferência</label>
      <input
        id="transfer-date"
        type="date"
        value={form.transferDate}
        max={today}
        onChange={(event) => set('transferDate', event.target.value)}
        aria-invalid={Boolean(errors.transferDate)}
        aria-describedby={describedBy('transferDate')}
      />
      {fieldError('transferDate')}

      {fieldError('form')}
      <div className="form__actions">
        <button type="submit" className="button--primary" disabled={busy || active.length < 2}>
          {busy ? 'Salvando…' : 'Salvar'}
        </button>
        <button type="button" onClick={onCancel} disabled={busy}>
          Voltar
        </button>
      </div>
    </form>
  )
}
