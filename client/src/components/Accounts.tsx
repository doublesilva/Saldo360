import { useEffect, useState, type FormEvent } from 'react'
import {
  ACCOUNT_INSTITUTION_MAX_LENGTH,
  ACCOUNT_NAME_MAX_LENGTH,
  accountTypeLabels,
  clearPrimaryAccount,
  createAccount,
  isBankAccount,
  setPrimaryAccount,
  getAccountBalance,
  getAccountProjection,
  listAccounts,
  toInput,
  updateAccount,
  type AccountBalance,
  type AccountProjection,
  type AccountInput,
  type AccountType,
  type FinancialAccount,
  type FinancialScope,
} from '../api/accounts'
import { isPluggyIntegrationAvailable } from '../api/pluggyIntegration'
import { PluggyAccountLinkForm } from './PluggyAccountLinkForm'
import { PluggyAccountPanel, PluggyIntegrationSection } from './PluggyIntegration'
type ResponsibleUserOption = { userId: string; displayName: string; isCurrentUser: boolean }
import { endOfMonth, todayInSaoPaulo } from '../dates'
import { decimalToInput, formatBrlDecimal, formatDate, isNegativeDecimal, parseAmount } from '../money'

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
  workspaceId,
  members,
  balanceVersion = 0,
}: {
  workspaceId: string
  members: ResponsibleUserOption[]
  balanceVersion?: number
}) {
  const [list, setList] = useState<ListState>({ kind: 'loading' })
  const [includeInactive, setIncludeInactive] = useState(false)
  // Data-alvo da projeção: começa no último dia do mês atual (America/Sao_Paulo).
  const today = todayInSaoPaulo()
  const [through, setThrough] = useState(() => endOfMonth(today))
  const [reloadKey, setReloadKey] = useState(0)
  const [editing, setEditing] = useState<FinancialAccount | 'new' | null>(null)
  const [toggleError, setToggleError] = useState<string | null>(null)
  const [togglingId, setTogglingId] = useState<string | null>(null)
  // T09.3: origem Pluggy só aparece quando a integração está habilitada para este usuário/orçamento.
  const [integrationAvailable, setIntegrationAvailable] = useState(false)
  const [newOrigin, setNewOrigin] = useState<'Manual' | 'Pluggy'>('Manual')

  useEffect(() => {
    const controller = new AbortController()
    isPluggyIntegrationAvailable(workspaceId, controller.signal).then(available => { if (!controller.signal.aborted) setIntegrationAvailable(available) }).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId])

  useEffect(() => {
    const controller = new AbortController()
    listAccounts(workspaceId, includeInactive, controller.signal)
      .then((accounts) => setList({ kind: 'loaded', accounts }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setList({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, includeInactive, reloadKey])

  const reload = () => setReloadKey((n) => n + 1)

  const handleSaved = () => {
    setEditing(null)
    reload()
  }

  const handleToggleActive = async (account: FinancialAccount) => {
    const question = account.isActive
      ? account.isPrimary
        ? `A conta "${account.name}" é a conta principal. Ao desativá-la ela deixa de ser a principal e o orçamento fica sem conta principal até você escolher outra. Continuar?`
        : `Desativar a conta "${account.name}"? Ela deixa de aparecer na lista, mas o cadastro é preservado.`
      : `Reativar a conta "${account.name}"?`
    if (!window.confirm(question)) return

    setTogglingId(account.id)
    setToggleError(null)
    try {
      // Desativar a principal exige remover a condição de forma explícita (a API recusa o contrário).
      const input = account.isActive && account.isPrimary ? { ...toInput(account), isPrimary: false } : toInput(account)
      const result = await updateAccount(workspaceId, account.id, input, !account.isActive)
      if (!result.ok) setToggleError(Object.values(result.errors).flat()[0] ?? 'Não foi possível alterar a situação.')
      reload()
    } catch (error: unknown) {
      setToggleError(errorMessage(error))
    } finally {
      setTogglingId(null)
    }
  }

  const handlePrimary = async (account: FinancialAccount, makePrimary: boolean) => {
    const question = makePrimary
      ? `Definir "${account.name}" como a conta principal? A principal atual deixa de ser. Saldos e movimentações não mudam.`
      : `Remover a condição de conta principal de "${account.name}"? O orçamento fica sem conta principal.`
    if (!window.confirm(question)) return

    setTogglingId(account.id)
    setToggleError(null)
    try {
      const result = makePrimary ? await setPrimaryAccount(workspaceId, account.id) : await clearPrimaryAccount(workspaceId, account.id)
      if (!result.ok) setToggleError(Object.values(result.errors).flat()[0] ?? 'Não foi possível alterar a conta principal.')
      reload()
    } catch (error: unknown) {
      setToggleError(errorMessage(error))
    } finally {
      setTogglingId(null)
    }
  }

  if (editing === 'new' && integrationAvailable) {
    const manualAccounts = list.kind === 'loaded' ? list.accounts.filter((account) => (account.origin ?? 'Manual') === 'Manual' && account.isActive) : []
    return (
      <div className="accounts">
        <fieldset>
          <legend>Origem da conta</legend>
          <label className="checkbox"><input type="radio" name="account-origin" checked={newOrigin === 'Manual'} onChange={() => setNewOrigin('Manual')} /> Manual — eu informo os dados</label>
          <label className="checkbox"><input type="radio" name="account-origin" checked={newOrigin === 'Pluggy'} onChange={() => setNewOrigin('Pluggy')} /> Pluggy — integrar uma conta ou cartão do banco</label>
        </fieldset>
        {newOrigin === 'Pluggy'
          ? <PluggyAccountLinkForm workspaceId={workspaceId} manualAccounts={manualAccounts} onLinked={handleSaved} onCancel={() => setEditing(null)} />
          : <AccountForm workspaceId={workspaceId} members={members} account={null} onSaved={handleSaved} onCancel={() => setEditing(null)} />}
      </div>
    )
  }

  if (editing !== null) {
    return (
      <AccountForm
        workspaceId={workspaceId}
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
        <button type="button" className="button--primary" onClick={() => { setNewOrigin('Manual'); setEditing('new') }}>
          Nova conta
        </button>
      </div>

      <label className="checkbox">
        <input type="checkbox" checked={includeInactive} onChange={(event) => setIncludeInactive(event.target.checked)} />
        Mostrar contas inativas
      </label>

      <div className="projection-target">
        <label htmlFor="projection-through">Projetar até</label>
        <input
          id="projection-through"
          type="date"
          value={through}
          min={today}
          onChange={(event) => setThrough(event.target.value || endOfMonth(today))}
        />
        <p className="meta">Inclui previsões pendentes vencidas e até a data escolhida.</p>
      </div>

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
                <span className="badge">{account.origin === 'Pluggy' ? 'Origem: Pluggy' : 'Origem: Manual'}</span>
                {account.isPrimary && <span className="badge badge--primary">Conta principal</span>}
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
              {account.integration && (
                <PluggyAccountPanel workspaceId={workspaceId} linkId={account.integration.linkId} refreshKey={`${reloadKey}:${balanceVersion}`} onChanged={reload} />
              )}
              <RealizedBalance workspaceId={workspaceId} accountId={account.id} refreshKey={`${reloadKey}:${balanceVersion}`} />
              <ProjectedBalance
                workspaceId={workspaceId}
                accountId={account.id}
                through={through}
                refreshKey={`${reloadKey}:${balanceVersion}`}
              />
              <div className="form__actions">
                <button type="button" onClick={() => setEditing(account)}>
                  Editar
                </button>
                {account.isPrimary ? (
                  <button type="button" onClick={() => void handlePrimary(account, false)} disabled={togglingId === account.id}>
                    Remover condição de principal
                  </button>
                ) : (
                  account.isActive && isBankAccount(account) && (
                    <button type="button" onClick={() => void handlePrimary(account, true)} disabled={togglingId === account.id}>
                      Definir como principal
                    </button>
                  )
                )}
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
      {integrationAvailable && <PluggyIntegrationSection workspaceId={workspaceId} refreshKey={`${reloadKey}:${balanceVersion}`} onChanged={reload} />}
    </div>
  )
}

/**
 * Saldo inicial configurado, nunca apresentado como saldo atual: ainda não há movimentações.
 * Sem configuração, diz isso explicitamente (não é zero).
 */
function OpeningBalanceSummary({ account }: { account: FinancialAccount }) {
  if (account.openingBalance === null || account.openingBalanceDate === null) {
    // Conta integrada: o saldo parte da referência bancária; não se pede saldo inicial manual.
    if (account.origin === 'Pluggy') return <p className="account__opening meta">Sem saldo inicial manual: o saldo parte da referência bancária (veja a conciliação acima).</p>
    return <p className="account__opening meta">Saldo inicial não informado</p>
  }
  return (
    <p className="account__opening">
      Saldo inicial em {formatDate(account.openingBalanceDate)}:{' '}
      <strong className={isNegativeDecimal(account.openingBalance) ? 'amount amount--negative' : 'amount'}>
        {formatBrlDecimal(account.openingBalance)}
      </strong>
      <span className="meta"> — no início do dia, antes das movimentações dessa data. Não é o saldo atual.</span>
    </p>
  )
}

type ProjectionState = { kind: 'loading' } | { kind: 'loaded'; projection: AccountProjection } | { kind: 'error'; message: string }

/**
 * Saldo projetado até a data-alvo, calculado pela API (saldo realizado de hoje + previsões pendentes vencidas e até
 * a data). Separado do saldo realizado. Os valores chegam como texto decimal e só são formatados: nada é somado
 * aqui. Indisponível nunca aparece como zero.
 */
function ProjectedBalance({
  workspaceId,
  accountId,
  through,
  refreshKey,
}: {
  workspaceId: string
  accountId: string
  through: string
  refreshKey: string
}) {
  const [state, setState] = useState<ProjectionState>({ kind: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    getAccountProjection(workspaceId, accountId, through, controller.signal)
      .then((projection) => setState({ kind: 'loaded', projection }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, accountId, through, refreshKey])

  if (state.kind === 'loading') return <p className="account__projection meta">Calculando saldo projetado…</p>
  if (state.kind === 'error') return <p className="account__projection status status--down">{state.message}</p>

  const { projection } = state
  const { projectedBalance, overdueIncome, overdueExpense, plannedIncome, plannedExpense } = projection
  if (
    projectedBalance === null ||
    overdueIncome === null ||
    overdueExpense === null ||
    plannedIncome === null ||
    plannedExpense === null
  ) {
    return (
      <p className="account__projection account__balance--unavailable">
        Saldo projetado indisponível: depende do saldo realizado de hoje.
      </p>
    )
  }

  const negative = isNegativeDecimal(projectedBalance)
  return (
    <div className="account__projection">
      <p>
        Saldo projetado até {formatDate(projection.through)}:{' '}
        <strong className={negative ? 'amount amount--negative' : 'amount'}>{formatBrlDecimal(projectedBalance)}</strong>
        {negative && <span className="meta"> — projeção negativa: faltará dinheiro se as previsões se confirmarem.</span>}
      </p>
      <p className="meta">
        Previstas de {formatDate(projection.baseDate)} a {formatDate(projection.through)}: receitas{' '}
        {formatBrlDecimal(plannedIncome)} · despesas {formatBrlDecimal(plannedExpense)}
      </p>
      <p className="meta">
        Vencidas pendentes: receitas {formatBrlDecimal(overdueIncome)} · despesas {formatBrlDecimal(overdueExpense)}
      </p>
    </div>
  )
}

type BalanceState = { kind: 'loading' } | { kind: 'loaded'; balance: AccountBalance } | { kind: 'error'; message: string }

/**
 * Saldo realizado até hoje, calculado pela API (saldo inicial + receitas − despesas não canceladas desde a
 * data de referência). Os valores chegam como texto decimal e são só formatados: nada é somado aqui.
 * Indisponível nunca aparece como zero.
 */
function RealizedBalance({ workspaceId, accountId, refreshKey }: { workspaceId: string; accountId: string; refreshKey: string }) {
  const [state, setState] = useState<BalanceState>({ kind: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    getAccountBalance(workspaceId, accountId, controller.signal)
      .then((balance) => setState({ kind: 'loaded', balance }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [workspaceId, accountId, refreshKey])

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
      {balance.openingSource === 'BalanceAnchor' && (
        <p className="meta">
          Saldo calculado a partir da referência bancária (saldo informado pelo banco) e dos movimentos consistentes, sem cadastro manual de saldo inicial. O histórico só existe a partir da data abaixo.
        </p>
      )}
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
  responsibleUserId: null,
  openingBalance: null,
  openingBalanceDate: null,
}

function AccountForm({
  workspaceId,
  members,
  account,
  onSaved,
  onCancel,
}: {
  workspaceId: string
  members: ResponsibleUserOption[]
  account: FinancialAccount | null
  onSaved: () => void
  onCancel: () => void
}) {
  const [input, setInput] = useState<AccountInput>(account ? toInput(account) : emptyInput)
  // Texto digitado no valor (pt-BR); convertido sem arredondar só no envio.
  const [balanceText, setBalanceText] = useState(
    account?.openingBalance != null ? decimalToInput(account.openingBalance) : '',
  )
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)
  // Conta principal (T09.3.3): só conta bancária ativa; a escolha só é enviada quando muda.
  const [wantsPrimary, setWantsPrimary] = useState(account?.isPrimary === true)

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
        openingBalance: amount.kind === 'ok' ? amount.text : null,
        openingBalanceDate: date,
      }
      const primaryNow = input.type !== 'Cash' && wantsPrimary
      if (primaryNow !== (account?.isPrimary === true)) payload.isPrimary = primaryNow
      const result = account
        ? await updateAccount(workspaceId, account.id, payload, account.isActive)
        : await createAccount(workspaceId, payload)
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

      {input.type !== 'Cash' && (account?.isActive ?? true) && (
        <label className="checkbox" htmlFor="account-primary">
          <input id="account-primary" type="checkbox" checked={wantsPrimary} onChange={(event) => setWantsPrimary(event.target.checked)} /> Definir como conta principal
        </label>
      )}
      <label htmlFor="account-responsible">Responsável</label>
      <select
        id="account-responsible"
        value={input.responsibleUserId ?? ''}
        onChange={(event) => set('responsibleUserId', event.target.value || null)}
      >
        <option value="">Conjunta</option>
        {members.map((member) => (
          <option key={member.userId} value={member.userId}>
            {member.displayName}
            {member.isCurrentUser ? ' (você)' : ''}
          </option>
        ))}
      </select>
      {fieldError('responsibleUserId')}

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
