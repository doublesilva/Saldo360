import { useEffect, useState, type FormEvent } from 'react'
import {
  ACCOUNT_INSTITUTION_MAX_LENGTH,
  ACCOUNT_NAME_MAX_LENGTH,
  accountTypeLabels,
  createAccount,
  getAccountBalance,
  listAccounts,
  toInput,
  updateAccount,
  type AccountBalance,
  type AccountInput,
  type AccountType,
  type FinancialAccount,
  type FinancialScope,
} from '../api/accounts'
import type { FamilyMember } from '../api/families'
import { formatAmountInput, formatBrl, formatBrlDecimal, formatDate, isNegativeDecimal, parseAmount } from '../money'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

type ListState =
  | { kind: 'loading' }
  | { kind: 'loaded'; accounts: FinancialAccount[] }
  | { kind: 'error'; message: string }

/**
 * Contas da família selecionada. Todos os integrantes veem e editam as mesmas contas.
 * `balanceVersion` muda quando lançamentos são criados, editados ou cancelados em outra
 * seção: os saldos realizados são consultados de novo.
 */
export function Accounts({
  familyId,
  members,
  balanceVersion = 0,
}: {
  familyId: string
  members: FamilyMember[]
  balanceVersion?: number
}) {
  const [list, setList] = useState<ListState>({ kind: 'loading' })
  const [includeInactive, setIncludeInactive] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)
  const [editing, setEditing] = useState<FinancialAccount | 'new' | null>(null)
  const [toggleError, setToggleError] = useState<string | null>(null)
  const [togglingId, setTogglingId] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    listAccounts(familyId, includeInactive, controller.signal)
      .then((accounts) => setList({ kind: 'loaded', accounts }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setList({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, includeInactive, reloadKey])

  const reload = () => setReloadKey((n) => n + 1)

  const handleSaved = () => {
    setEditing(null)
    reload()
  }

  const handleToggleActive = async (account: FinancialAccount) => {
    const question = account.isActive
      ? `Desativar a conta "${account.name}"? Ela deixa de aparecer na lista, mas o cadastro é preservado.`
      : `Reativar a conta "${account.name}"?`
    if (!window.confirm(question)) return

    setTogglingId(account.id)
    setToggleError(null)
    try {
      const result = await updateAccount(familyId, account.id, toInput(account), !account.isActive)
      if (!result.ok) setToggleError(Object.values(result.errors).flat()[0] ?? 'Não foi possível alterar a situação.')
      reload()
    } catch (error: unknown) {
      setToggleError(errorMessage(error))
    } finally {
      setTogglingId(null)
    }
  }

  if (editing !== null) {
    return (
      <AccountForm
        familyId={familyId}
        members={members}
        account={editing === 'new' ? null : editing}
        onSaved={handleSaved}
        onCancel={() => setEditing(null)}
      />
    )
  }

  return (
    <div className="accounts">
      <div className="accounts__header">
        <h3>Contas</h3>
        <button type="button" className="button--primary" onClick={() => setEditing('new')}>
          Nova conta
        </button>
      </div>

      <label className="checkbox">
        <input type="checkbox" checked={includeInactive} onChange={(event) => setIncludeInactive(event.target.checked)} />
        Mostrar contas inativas
      </label>

      {toggleError && (
        <p className="field-error" role="alert">
          {toggleError}
        </p>
      )}
      {list.kind === 'loading' && <p className="status status--pending">Carregando contas…</p>}
      {list.kind === 'error' && <p className="status status--down">{list.message}</p>}
      {list.kind === 'loaded' && list.accounts.length === 0 && (
        <p className="meta">
          Nenhuma conta {includeInactive ? '' : 'ativa '}cadastrada. Exemplos: “Itaú PF”, “Itaú Empresas”, “Dinheiro”.
        </p>
      )}

      {list.kind === 'loaded' && list.accounts.length > 0 && (
        <ul className="account-list">
          {list.accounts.map((account) => (
            <li key={account.id} className={account.isActive ? 'account' : 'account account--inactive'}>
              <div className="account__title">
                <span className="account__name">{account.name}</span>
                <span className={`badge badge--${account.financialScope.toLowerCase()}`}>{account.financialScope}</span>
              </div>
              <dl className="account__details">
                <div>
                  <dt>Instituição</dt>
                  <dd>{account.institution ?? '—'}</dd>
                </div>
                <div>
                  <dt>Tipo</dt>
                  <dd>{accountTypeLabels[account.type]}</dd>
                </div>
                <div>
                  <dt>Responsável</dt>
                  <dd>{account.responsible?.displayName ?? 'Conjunta'}</dd>
                </div>
                <div>
                  <dt>Situação</dt>
                  <dd>{account.isActive ? 'Ativa' : 'Inativa'}</dd>
                </div>
              </dl>
              <OpeningBalanceSummary account={account} />
              <RealizedBalance familyId={familyId} accountId={account.id} refreshKey={`${reloadKey}:${balanceVersion}`} />
              <div className="form__actions">
                <button type="button" onClick={() => setEditing(account)}>
                  Editar
                </button>
                <button
                  type="button"
                  onClick={() => void handleToggleActive(account)}
                  disabled={togglingId === account.id}
                >
                  {account.isActive ? 'Desativar' : 'Reativar'}
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/**
 * Saldo inicial configurado, nunca apresentado como saldo atual: ainda não há movimentações.
 * Sem configuração, diz isso explicitamente (não é zero).
 */
function OpeningBalanceSummary({ account }: { account: FinancialAccount }) {
  if (account.openingBalance === null || account.openingBalanceDate === null) {
    return <p className="account__opening meta">Saldo inicial não informado</p>
  }
  return (
    <p className="account__opening">
      Saldo inicial em {formatDate(account.openingBalanceDate)}:{' '}
      <strong className={account.openingBalance < 0 ? 'amount amount--negative' : 'amount'}>
        {formatBrl(account.openingBalance)}
      </strong>
      <span className="meta"> — no início do dia, antes das movimentações dessa data. Não é o saldo atual.</span>
    </p>
  )
}

type BalanceState = { kind: 'loading' } | { kind: 'loaded'; balance: AccountBalance } | { kind: 'error'; message: string }

/**
 * Saldo realizado até hoje, calculado pela API (saldo inicial + receitas − despesas não canceladas desde a
 * data de referência). Os valores chegam como texto decimal e são só formatados: nada é somado aqui.
 * Indisponível nunca aparece como zero.
 */
function RealizedBalance({ familyId, accountId, refreshKey }: { familyId: string; accountId: string; refreshKey: string }) {
  const [state, setState] = useState<BalanceState>({ kind: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    getAccountBalance(familyId, accountId, controller.signal)
      .then((balance) => setState({ kind: 'loaded', balance }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [familyId, accountId, refreshKey])

  if (state.kind === 'loading') return <p className="account__balance meta">Calculando saldo realizado…</p>
  if (state.kind === 'error') return <p className="account__balance status status--down">{state.message}</p>

  const { balance } = state
  if (
    balance.balance === null ||
    balance.totalIncome === null ||
    balance.totalExpense === null ||
    balance.totalTransferIn === null ||
    balance.totalTransferOut === null
  ) {
    const message =
      balance.unavailableReason === 'BeforeOpeningBalanceDate' && balance.openingBalanceDate
        ? `Saldo realizado indisponível: a data de referência do saldo inicial (${formatDate(balance.openingBalanceDate)}) é posterior a ${formatDate(balance.asOf)}.`
        : 'Saldo realizado indisponível. Configure o saldo inicial para calcular o saldo.'
    return <p className="account__balance account__balance--unavailable">{message}</p>
  }

  const negative = isNegativeDecimal(balance.balance)
  return (
    <div className="account__balance">
      <p>
        Saldo realizado até {formatDate(balance.asOf)}:{' '}
        <strong className={negative ? 'amount amount--negative' : 'amount'}>{formatBrlDecimal(balance.balance)}</strong>
        {negative && <span className="meta"> — saldo negativo: valor devido, não é crédito disponível.</span>}
      </p>
      <p className="meta">
        Desde {balance.openingBalanceDate ? formatDate(balance.openingBalanceDate) : '—'}: receitas{' '}
        {formatBrlDecimal(balance.totalIncome)} · despesas {formatBrlDecimal(balance.totalExpense)}
      </p>
      <p className="meta">
        Transferências: recebidas {formatBrlDecimal(balance.totalTransferIn)} · enviadas{' '}
        {formatBrlDecimal(balance.totalTransferOut)}
      </p>
    </div>
  )
}

const emptyInput: AccountInput = {
  name: '',
  institution: null,
  type: 'Checking',
  financialScope: 'PF',
  responsibleMemberId: null,
  openingBalance: null,
  openingBalanceDate: null,
}

function AccountForm({
  familyId,
  members,
  account,
  onSaved,
  onCancel,
}: {
  familyId: string
  members: FamilyMember[]
  account: FinancialAccount | null
  onSaved: () => void
  onCancel: () => void
}) {
  const [input, setInput] = useState<AccountInput>(account ? toInput(account) : emptyInput)
  // Texto digitado no valor (pt-BR); convertido sem arredondar só no envio.
  const [balanceText, setBalanceText] = useState(
    account?.openingBalance != null ? formatAmountInput(account.openingBalance) : '',
  )
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)

  const set = <K extends keyof AccountInput>(field: K, value: AccountInput[K]) =>
    setInput((current) => ({ ...current, [field]: value }))

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    const clientErrors: Record<string, string[]> = {}
    if (input.name.trim().length === 0) clientErrors.name = ['Informe o nome da conta.']

    // Valor e data juntos ou ambos em branco; mais de duas casas é erro, não arredondamento.
    const amount = parseAmount(balanceText, 'O saldo inicial')
    const date = input.openingBalanceDate || null
    if (amount.kind === 'invalid') clientErrors.openingBalance = [amount.message]
    else if (amount.kind === 'empty' && date)
      clientErrors.openingBalance = ['Informe o valor do saldo inicial ou remova a data de referência.']
    else if (amount.kind === 'ok' && !date) clientErrors.openingBalanceDate = ['Informe a data de referência do saldo inicial.']

    if (Object.keys(clientErrors).length > 0) {
      setErrors(clientErrors)
      return
    }

    setBusy(true)
    setErrors({})
    try {
      const payload: AccountInput = {
        ...input,
        institution: input.institution?.trim() || null,
        openingBalance: amount.kind === 'ok' ? amount.value : null,
        openingBalanceDate: date,
      }
      const result = account
        ? await updateAccount(familyId, account.id, payload, account.isActive)
        : await createAccount(familyId, payload)
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
      <p id={`account-${field}-error`} className="field-error" role="alert">
        {errors[field][0]}
      </p>
    )

  return (
    <form className="form accounts" onSubmit={handleSubmit} noValidate>
      <h3>{account ? 'Editar conta' : 'Nova conta'}</h3>

      <label htmlFor="account-name">Nome</label>
      <input
        id="account-name"
        value={input.name}
        onChange={(event) => set('name', event.target.value)}
        maxLength={ACCOUNT_NAME_MAX_LENGTH}
        placeholder="Ex.: Itaú PF, Itaú Empresas, Dinheiro"
        autoComplete="off"
        aria-invalid={Boolean(errors.name)}
        required
      />
      {fieldError('name')}

      <label htmlFor="account-institution">Instituição (opcional)</label>
      <input
        id="account-institution"
        value={input.institution ?? ''}
        onChange={(event) => set('institution', event.target.value)}
        maxLength={ACCOUNT_INSTITUTION_MAX_LENGTH}
        placeholder="Ex.: Itaú"
        autoComplete="off"
        aria-invalid={Boolean(errors.institution)}
      />
      {fieldError('institution')}

      <label htmlFor="account-type">Tipo</label>
      <select id="account-type" value={input.type} onChange={(event) => set('type', event.target.value as AccountType)}>
        {(Object.keys(accountTypeLabels) as AccountType[]).map((type) => (
          <option key={type} value={type}>
            {accountTypeLabels[type]}
          </option>
        ))}
      </select>
      {fieldError('type')}

      <label htmlFor="account-scope">Pessoa física ou jurídica</label>
      <select
        id="account-scope"
        value={input.financialScope}
        onChange={(event) => set('financialScope', event.target.value as FinancialScope)}
      >
        <option value="PF">PF — pessoa física</option>
        <option value="PJ">PJ — pessoa jurídica</option>
      </select>
      {fieldError('financialScope')}

      <label htmlFor="account-responsible">Responsável</label>
      <select
        id="account-responsible"
        value={input.responsibleMemberId ?? ''}
        onChange={(event) => set('responsibleMemberId', event.target.value || null)}
      >
        <option value="">Conjunta</option>
        {members.map((member) => (
          <option key={member.memberId} value={member.memberId}>
            {member.displayName}
            {member.isCurrentUser ? ' (você)' : ''}
          </option>
        ))}
      </select>
      {fieldError('responsibleMemberId')}

      <fieldset className="opening-balance" aria-describedby="account-opening-help">
        <legend>Saldo inicial (opcional)</legend>
        <p id="account-opening-help" className="meta">
          Saldo da conta no <strong>início</strong> da data de referência, antes de qualquer movimentação desse dia.
          Pode ser negativo (por exemplo, cheque especial já utilizado) — não é o limite disponível nem o saldo atual.
          Para não informar, deixe valor e data em branco.
        </p>

        <label htmlFor="account-opening-balance">Valor (R$)</label>
        <input
          id="account-opening-balance"
          value={balanceText}
          onChange={(event) => setBalanceText(event.target.value)}
          inputMode="decimal"
          placeholder="Ex.: 1.234,56 ou -250,00"
          autoComplete="off"
          aria-invalid={Boolean(errors.openingBalance)}
          aria-describedby={errors.openingBalance ? 'account-openingBalance-error' : undefined}
        />
        {fieldError('openingBalance')}

        <label htmlFor="account-opening-balance-date">Data de referência</label>
        <input
          id="account-opening-balance-date"
          type="date"
          value={input.openingBalanceDate ?? ''}
          onChange={(event) => set('openingBalanceDate', event.target.value || null)}
          aria-invalid={Boolean(errors.openingBalanceDate)}
          aria-describedby={errors.openingBalanceDate ? 'account-openingBalanceDate-error' : undefined}
        />
        {fieldError('openingBalanceDate')}
      </fieldset>

      <p className="meta">Não informe agência, número de conta nem senhas: o Saldo360 não guarda esses dados.</p>
      {fieldError('form')}

      <div className="form__actions">
        <button type="submit" className="button--primary" disabled={busy}>
          {busy ? 'Salvando…' : 'Salvar'}
        </button>
        <button type="button" onClick={onCancel} disabled={busy}>
          Cancelar
        </button>
      </div>
    </form>
  )
}
