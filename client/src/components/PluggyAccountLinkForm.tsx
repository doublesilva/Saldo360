import { useEffect, useState, type FormEvent } from 'react'
import { ACCOUNT_NAME_MAX_LENGTH, accountTypeLabels, type AccountType, type FinancialAccount, type FinancialScope } from '../api/accounts'
import { listAvailableAccounts, listIntegrationConnections, linkAccount, type AvailableAccount, type LinkMode, type LinkRequest } from '../api/pluggyIntegration'
import type { PluggyConnection } from '../api/pluggyPoc'
import { decimalToInput, parseAmount } from '../money'
import { money, text } from '../pluggyFormat'

const errorText = (error: unknown) => (error instanceof Error ? error.message : 'Falha inesperada.')

/**
 * Cadastro de conta com origem Pluggy: o usuário escolhe o Item já cadastrado, a conta ou o cartão devolvidos pela Pluggy e confirma. Nenhum
 * identificador externo é digitado. A criação usa os metadados da Pluggy como sugestão, sempre confirmada aqui; o saldo inicial nunca é
 * preenchido automaticamente.
 */
export function PluggyAccountLinkForm({ workspaceId, manualAccounts, onLinked, onCancel }: {
  workspaceId: string
  /** Contas manuais ainda sem integração, para vincular a uma já existente. */
  manualAccounts: FinancialAccount[]
  onLinked: () => void
  onCancel: () => void
}) {
  const [connections, setConnections] = useState<PluggyConnection[] | null>(null)
  const [connectionId, setConnectionId] = useState('')
  const [available, setAvailable] = useState<AvailableAccount[] | null>(null)
  const [selectedId, setSelectedId] = useState('')
  const [mode, setMode] = useState<LinkMode>('CreateNew')
  const [name, setName] = useState('')
  const [accountType, setAccountType] = useState<AccountType>('Checking')
  const [scope, setScope] = useState<FinancialScope>('PF')
  const [importFrom, setImportFrom] = useState('')
  const [existingAccountId, setExistingAccountId] = useState('')
  const [closingDay, setClosingDay] = useState('')
  const [dueDay, setDueDay] = useState('')
  const [limitText, setLimitText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    listIntegrationConnections(workspaceId, controller.signal).then(setConnections).catch((cause: unknown) => { if (!controller.signal.aborted) setError(errorText(cause)) })
    return () => controller.abort()
  }, [workspaceId])

  const chooseConnection = (id: string) => {
    setConnectionId(id); setSelectedId(''); setAvailable(null); setError(null)
    if (!id) return
    listAvailableAccounts(workspaceId, id).then(setAvailable).catch((cause: unknown) => setError(errorText(cause)))
  }

  const selected = available?.find(account => account.pluggyAccountId === selectedId) ?? null
  const chooseAccount = (account: AvailableAccount) => {
    setSelectedId(account.pluggyAccountId); setError(null); setMode('CreateNew'); setExistingAccountId('')
    setName(account.displayName.replace(/\s*••••\d+$/, ''))
    setAccountType(account.suggestedAccountType ?? 'Checking')
    setClosingDay(account.card?.closingDay?.toString() ?? '')
    setDueDay(account.card?.dueDay?.toString() ?? '')
    setLimitText(account.card?.creditLimit ? decimalToInput(account.card.creditLimit) : '')
  }

  const isCard = selected?.kind === 'Card'

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!selected) { setError('Selecione a conta da Pluggy.'); return }
    if (mode === 'LinkExisting' && !importFrom) { setError('Informe a data de início da importação: o histórico anterior a ela não é importado, para não duplicar lançamentos já feitos.'); return }
    if (mode === 'LinkExisting' && !existingAccountId) { setError('Selecione a conta do Saldo360.'); return }
    const request: LinkRequest = { connectionId, pluggyAccountId: selected.pluggyAccountId, mode, importFrom: importFrom || null }
    if (mode === 'LinkExisting') request.existingAccountId = existingAccountId
    else if (isCard) {
      const limit = parseAmount(limitText, 'O limite')
      if (limit.kind === 'invalid') { setError(limit.message); return }
      request.name = name.trim(); request.closingDay = Number(closingDay) || null; request.dueDay = Number(dueDay) || null
      request.creditLimit = limit.kind === 'ok' ? limit.text : null
    } else { request.name = name.trim(); request.accountType = accountType; request.financialScope = scope }
    setBusy(true); setError(null)
    try { await linkAccount(workspaceId, request); onLinked() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }

  return <form className="form accounts" onSubmit={submit} noValidate aria-labelledby="pluggy-link-title">
    <h3 id="pluggy-link-title">Nova conta integrada (Pluggy)</h3>
    <p className="meta">A conta ou o cartão é escolhido entre os que a Pluggy devolve para o Item cadastrado. O Saldo360 só lê os dados: nenhuma operação financeira é iniciada no banco.</p>

    {connections && connections.length === 0 && <p className="meta">Nenhum Item cadastrado neste orçamento. Cadastre o Item no Laboratório Pluggy antes de integrar contas.</p>}
    <label htmlFor="pluggy-connection">Conexão (Item)</label>
    <select id="pluggy-connection" value={connectionId} onChange={event => chooseConnection(event.target.value)}>
      <option value="">Selecione…</option>
      {connections?.map(connection => <option key={connection.id} value={connection.id}>{connection.label ?? connection.institution ?? connection.itemReference}{connection.institution && connection.label ? ` — ${connection.institution}` : ''}</option>)}
    </select>

    {available && <fieldset>
      <legend>Conta ou cartão na Pluggy</legend>
      {available.length === 0 && <p className="meta">A Pluggy não devolveu contas para esta conexão.</p>}
      {available.map(account => <label key={account.pluggyAccountId} className="checkbox">
        <input type="radio" name="pluggy-account" value={account.pluggyAccountId} checked={selectedId === account.pluggyAccountId} disabled={!account.linkable} onChange={() => chooseAccount(account)} />
        {account.displayName} · {account.kind === 'Card' ? 'Cartão' : account.kind === 'Bank' ? 'Conta bancária' : 'Tipo não suportado'}
        {account.kind === 'Bank' && account.balance !== null ? ` · saldo informado ${money(account.balance)}` : ''}
        {account.notLinkableReason === 'AlreadyLinked' ? ' · já integrada' : account.notLinkableReason === 'UnsupportedType' ? ' · não suportado' : ''}
      </label>)}
    </fieldset>}

    {selected && <>
      {!isCard && <fieldset>
        <legend>Conta do Saldo360</legend>
        <label className="checkbox"><input type="radio" name="pluggy-mode" checked={mode === 'CreateNew'} onChange={() => setMode('CreateNew')} /> Criar uma nova conta com os dados da Pluggy</label>
        <label className="checkbox"><input type="radio" name="pluggy-mode" checked={mode === 'LinkExisting'} onChange={() => setMode('LinkExisting')} disabled={manualAccounts.length === 0} /> Vincular a uma conta já cadastrada</label>
      </fieldset>}

      {mode === 'CreateNew' && <>
        <label htmlFor="pluggy-name">{isCard ? 'Nome do cartão' : 'Nome da conta'}</label>
        <input id="pluggy-name" value={name} maxLength={ACCOUNT_NAME_MAX_LENGTH} onChange={event => setName(event.target.value)} autoComplete="off" />
        {!isCard && <>
          <label htmlFor="pluggy-type">Tipo</label>
          <select id="pluggy-type" value={accountType} onChange={event => setAccountType(event.target.value as AccountType)}>
            {(['Checking', 'Savings'] as AccountType[]).map(type => <option key={type} value={type}>{accountTypeLabels[type]}</option>)}
          </select>
          {!selected.subtypeRecognized && <p className="meta">A Pluggy não informou um subtipo conhecido ({text(selected.pluggySubtype)}). Confirme o tipo.</p>}
          <label htmlFor="pluggy-scope">Pessoa física ou jurídica</label>
          <select id="pluggy-scope" value={scope} onChange={event => setScope(event.target.value as FinancialScope)}>
            <option value="PF">PF — pessoa física</option><option value="PJ">PJ — pessoa jurídica</option>
          </select>
        </>}
        {isCard && <>
          <label htmlFor="pluggy-closing">Dia de fechamento da fatura</label>
          <input id="pluggy-closing" inputMode="numeric" value={closingDay} onChange={event => setClosingDay(event.target.value)} />
          <label htmlFor="pluggy-due">Dia de vencimento da fatura</label>
          <input id="pluggy-due" inputMode="numeric" value={dueDay} onChange={event => setDueDay(event.target.value)} />
          <label htmlFor="pluggy-limit">Limite de crédito (R$)</label>
          <input id="pluggy-limit" inputMode="decimal" value={limitText} onChange={event => setLimitText(event.target.value)} />
          <p className="meta">Dias e limite sugeridos pela Pluggy; confira antes de confirmar.</p>
        </>}
      </>}

      {mode === 'LinkExisting' && <>
        <label htmlFor="pluggy-existing">Conta do Saldo360</label>
        <select id="pluggy-existing" value={existingAccountId} onChange={event => setExistingAccountId(event.target.value)}>
          <option value="">Selecione…</option>
          {manualAccounts.map(account => <option key={account.id} value={account.id}>{account.name}</option>)}
        </select>
      </>}

      <label htmlFor="pluggy-import-from">{mode === 'LinkExisting' ? 'Importar movimentos a partir de' : 'Importar movimentos a partir de (opcional)'}</label>
      <input id="pluggy-import-from" type="date" value={importFrom} onChange={event => setImportFrom(event.target.value)} />
      <p className="meta">{mode === 'LinkExisting'
        ? 'Obrigatório: movimentos anteriores a esta data não são importados, para não duplicar o que já foi lançado nesta conta.'
        : 'Em branco = todo o histórico que a Pluggy devolver. O saldo inicial não é preenchido sozinho: depois da primeira sincronização o Saldo360 sugere um valor e você decide se aplica.'}</p>
    </>}

    {error && <p role="alert" className="field-error">{error}</p>}
    <div className="form__actions">
      <button type="submit" className="button--primary" disabled={busy || !selected}>{busy ? 'Vinculando…' : 'Confirmar e vincular'}</button>
      <button type="button" onClick={onCancel} disabled={busy}>Cancelar</button>
    </div>
  </form>
}
