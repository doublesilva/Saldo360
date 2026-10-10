import { useEffect, useState, type FormEvent } from 'react'
import {
  ACCOUNT_INSTITUTION_MAX_LENGTH,
  ACCOUNT_NAME_MAX_LENGTH,
  accountTypeLabels,
  createAccount,
  listAccounts,
  toInput,
  updateAccount,
  type AccountInput,
  type AccountType,
  type FinancialAccount,
  type FinancialScope,
} from '../api/accounts'
import type { FamilyMember } from '../api/families'

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

type ListState =
  | { kind: 'loading' }
  | { kind: 'loaded'; accounts: FinancialAccount[] }
  | { kind: 'error'; message: string }

/** Contas da família selecionada. Todos os integrantes veem e editam as mesmas contas. */
export function Accounts({ familyId, members }: { familyId: string; members: FamilyMember[] }) {
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

const emptyInput: AccountInput = {
  name: '',
  institution: null,
  type: 'Checking',
  financialScope: 'PF',
  responsibleMemberId: null,
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
  const [errors, setErrors] = useState<Record<string, string[]>>({})
  const [busy, setBusy] = useState(false)

  const set = <K extends keyof AccountInput>(field: K, value: AccountInput[K]) =>
    setInput((current) => ({ ...current, [field]: value }))

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (input.name.trim().length === 0) {
      setErrors({ name: ['Informe o nome da conta.'] })
      return
    }

    setBusy(true)
    setErrors({})
    try {
      const payload = { ...input, institution: input.institution?.trim() || null }
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
