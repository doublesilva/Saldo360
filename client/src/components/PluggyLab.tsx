import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import {
  addConnection, getDiagnostics, getLabStatus, listConnections, listPluggyAccounts, listPluggyBills, listPluggyTransactions,
  type PluggyAccount, type PluggyBill, type PluggyConnection, type PluggyDiagnostics, type PluggyTransaction, type ResourceCoverage,
} from '../api/pluggyPoc'
import { PluggyImported } from './PluggyImported'
import { connectionStates, day, money, NOT_INFORMED, resourceStates, text } from '../pluggyFormat'

const errorText = (error: unknown) => (error instanceof Error ? error.message : 'Falha inesperada.')

function Table({ caption, head, children }: { caption: string; head: string[]; children: ReactNode }) {
  return <div className="planning-table-wrap"><table>
    <caption className="meta">{caption}</caption>
    <thead><tr>{head.map(label => <th key={label} scope="col">{label}</th>)}</tr></thead>
    <tbody>{children}</tbody>
  </table></div>
}

/** Laboratório experimental da Pluggy: somente leitura, sem relação com os dados financeiros operacionais. */
export function PluggyLab({ workspaceId, embedded = false }: { workspaceId: string; embedded?: boolean }) {
  const [connections, setConnections] = useState<PluggyConnection[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [accounts, setAccounts] = useState<PluggyAccount[] | null>(null)
  const [accountError, setAccountError] = useState<string | null>(null)

  const [version, setVersion] = useState(0)
  const [importEnabled, setImportEnabled] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    getLabStatus(workspaceId, controller.signal).then(status => { if (!controller.signal.aborted) setImportEnabled(status?.importEnabled === true) }).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId])

  useEffect(() => {
    const controller = new AbortController()
    listConnections(workspaceId, controller.signal)
      .then(result => { setConnections(result); setError(null) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) setError(errorText(cause)) })
    return () => controller.abort()
  }, [workspaceId, version])

  useEffect(() => {
    if (!selected) return
    const controller = new AbortController()
    listPluggyAccounts(workspaceId, selected, controller.signal)
      .then(result => { setAccounts(result); setAccountError(null) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) { setAccounts(null); setAccountError(errorText(cause)) } })
    return () => controller.abort()
  }, [workspaceId, selected])

  const select = (id: string) => { setAccounts(null); setAccountError(null); setSelected(id) }

  return <div className="pluggy-lab">
    <section className="card" aria-labelledby="pluggy-lab-title">
      <h3 id="pluggy-lab-title">{embedded ? 'Diagnóstico técnico da Pluggy' : 'Laboratório Pluggy'}</h3>
      <p className="notice">Experimento somente leitura. Nada aqui altera contas, lançamentos, saldos, previsões, cartões ou faturas do Saldo360, e os dados da Pluggy não são importados.</p>
    </section>
    <ConnectionsSection connections={connections} error={error} selected={selected} onSelect={select} workspaceId={workspaceId} onAdded={() => setVersion(value => value + 1)} showForm={!embedded} />
    {selected && <>
      <AccountsSection accounts={accounts} error={accountError} />
      {accounts && <TransactionsSection workspaceId={workspaceId} accounts={accounts} />}
      {accounts && <CardsSection workspaceId={workspaceId} accounts={accounts} />}
      <DiagnosticsSection key={selected} workspaceId={workspaceId} connectionId={selected} />
      {importEnabled && accounts && connections?.find(c => c.id === selected) && <PluggyImported key={selected} workspaceId={workspaceId} connection={connections.find(c => c.id === selected)!} accounts={accounts} />}
    </>}
  </div>
}

/** Cadastro do Item da Pluggy (só o itemId; nenhuma credencial bancária é solicitada). Usado na central de automação e no diagnóstico. */
export function AddConnectionForm({ workspaceId, onAdded }: { workspaceId: string; onAdded: () => void }) {
  const [itemId, setItemId] = useState('')
  const [label, setLabel] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true); setFormError(null)
    try { await addConnection(workspaceId, { itemId: itemId.trim(), label: label.trim() || undefined }); setItemId(''); setLabel(''); onAdded() }
    catch (cause) { setFormError(errorText(cause)) }
    finally { setBusy(false) }
  }
  return <form onSubmit={submit} aria-label="Cadastrar Item da Pluggy">
    <p className="meta">O cadastro guarda apenas o itemId. Nenhuma credencial bancária é solicitada.</p>
    <label>itemId (UUID) <input value={itemId} onChange={event => setItemId(event.target.value)} required autoComplete="off" spellCheck={false} /></label>{' '}
    <label>Rótulo <input value={label} maxLength={120} onChange={event => setLabel(event.target.value)} placeholder="Ex.: Itaú PF" /></label>{' '}
    <button type="submit" disabled={busy || !itemId.trim()}>Cadastrar Item</button>
    {formError && <p role="alert" className="notice notice--error">{formError}</p>}
  </form>
}

function ConnectionsSection({ connections, error, selected, onSelect, workspaceId, onAdded, showForm }: {
  connections: PluggyConnection[] | null; error: string | null; selected: string | null; onSelect: (id: string) => void; workspaceId: string; onAdded: () => void; showForm: boolean
}) {
  return <section className="card" aria-labelledby="pluggy-connections-title">
    <h4 id="pluggy-connections-title">1. Conexões cadastradas</h4>
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {connections === null && !error && <p className="meta">Carregando conexões…</p>}
    {connections?.length === 0 && <p className="meta">Nenhum Item cadastrado. Informe abaixo o itemId obtido no Dashboard da Pluggy (Meu Pluggy).</p>}
    {connections && connections.length > 0 && <Table caption="Itens da Pluggy vinculados a este orçamento" head={['Instituição', 'Referência', 'Estado', 'Última atualização', 'Autorização até', 'Erro', '']}>
      {connections.map(c => <tr key={c.id} aria-selected={selected === c.id}>
        <td>{text(c.institution)}{c.label ? <small className="meta"> · {c.label}</small> : null}</td>
        <td><code>{c.id.slice(0, 8)}</code> <small className="meta">item {c.itemReference}</small></td>
        <td>{connectionStates[c.state] ?? c.state}{c.status ? <small className="meta"> ({c.status})</small> : null}</td>
        <td>{day(c.lastUpdatedAt)}</td>
        <td>{day(c.consentExpiresAt)}</td>
        <td>{c.integrationError ? `Falha de integração: ${c.integrationError}` : c.errorCode ? `${c.errorCode}${c.errorMessage ? ` — ${c.errorMessage}` : ''}` : NOT_INFORMED}</td>
        <td><button type="button" onClick={() => onSelect(c.id)} aria-pressed={selected === c.id}>Inspecionar</button></td>
      </tr>)}
    </Table>}
    {showForm && <AddConnectionForm workspaceId={workspaceId} onAdded={onAdded} />}
  </section>
}

function AccountsSection({ accounts, error }: { accounts: PluggyAccount[] | null; error: string | null }) {
  return <section className="card" aria-labelledby="pluggy-accounts-title">
    <h4 id="pluggy-accounts-title">2. Contas e saldos</h4>
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {accounts === null && !error && <p className="meta">Carregando contas…</p>}
    {accounts?.length === 0 && <p className="meta">A Pluggy não retornou contas para este Item.</p>}
    {accounts && accounts.length > 0 && <Table caption="Contas retornadas (números mascarados)" head={['Conta', 'Tipo', 'Agência/conta', 'Número', 'Moeda', 'Saldo', 'Limites', 'Atualizada em']}>
      {accounts.map(a => <tr key={a.id}>
        <td>{text(a.marketingName ?? a.name)}</td>
        <td>{text(a.type)} / {text(a.subtype)}</td>
        <td>{text(a.maskedBranchAccount)}</td>
        <td>{text(a.maskedNumber)}</td>
        <td>{text(a.currency)}</td>
        <td>{money(a.balance, a.currency)}</td>
        <td>{limits(a)}</td>
        <td>{day(a.updatedAt)}</td>
      </tr>)}
    </Table>}
  </section>
}

function limits(a: PluggyAccount): string {
  const parts = [
    a.creditLimit !== null && `Limite ${money(a.creditLimit, a.currency)}`, a.availableCreditLimit !== null && `Disponível ${money(a.availableCreditLimit, a.currency)}`,
    a.overdraftContractedLimit !== null && `Cheque especial ${money(a.overdraftContractedLimit, a.currency)}`, a.overdraftUsedLimit !== null && `Utilizado ${money(a.overdraftUsedLimit, a.currency)}`,
  ].filter(Boolean)
  return parts.length ? parts.join(' · ') : NOT_INFORMED
}

function TransactionsSection({ workspaceId, accounts }: { workspaceId: string; accounts: PluggyAccount[] }) {
  const [accountId, setAccountId] = useState(accounts[0]?.id ?? '')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [rows, setRows] = useState<PluggyTransaction[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const load = async (after?: string) => {
    setBusy(true); setError(null)
    try {
      const page = await listPluggyTransactions(workspaceId, accountId, { from, to, after })
      setRows(previous => (after ? [...previous, ...page.items] : page.items)); setCursor(page.nextCursor); setLoaded(true)
    } catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }
  if (accounts.length === 0) return null
  return <section className="card" aria-labelledby="pluggy-transactions-title">
    <h4 id="pluggy-transactions-title">3. Transações</h4>
    <form onSubmit={event => { event.preventDefault(); void load() }} aria-label="Filtro de transações">
      <label>Conta <select value={accountId} onChange={event => { setAccountId(event.target.value); setRows([]); setLoaded(false); setCursor(null) }}>
        {accounts.map(a => <option key={a.id} value={a.id}>{text(a.marketingName ?? a.name)} {a.maskedNumber ?? ''}</option>)}
      </select></label>{' '}
      <label>De <input type="date" value={from} onChange={event => setFrom(event.target.value)} /></label>{' '}
      <label>Até <input type="date" value={to} onChange={event => setTo(event.target.value)} /></label>{' '}
      <button type="submit" disabled={busy}>Consultar</button>
    </form>
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {loaded && rows.length === 0 && <p className="meta">Nenhuma transação retornada para o período.</p>}
    {rows.length > 0 && <Table caption={`${rows.length} transações carregadas`} head={['Data', 'Descrição', 'Descrição original', 'Valor', 'Tipo', 'Status', 'Categoria', 'Operação', 'Comerciante', 'Parcela', 'Fatura']}>
      {rows.map(t => <tr key={t.id}>
        <td>{day(t.date)}</td><td>{text(t.description)}</td><td>{text(t.descriptionRaw)}</td><td>{money(t.amount, t.currency)}</td>
        <td>{text(t.type)}</td><td>{text(t.status)}</td><td>{text(t.category)}</td>
        <td>{text(t.operationType ?? t.paymentMethod)}</td><td>{text(t.merchant)}</td>
        <td>{t.totalInstallments ? `${text(t.installmentNumber)}/${t.totalInstallments}` : NOT_INFORMED}</td><td>{text(t.billId)}</td>
      </tr>)}
    </Table>}
    {cursor && <button type="button" disabled={busy} onClick={() => void load(cursor)}>Carregar mais</button>}
  </section>
}

function CardsSection({ workspaceId, accounts }: { workspaceId: string; accounts: PluggyAccount[] }) {
  const cards = accounts.filter(a => a.type === 'CREDIT')
  const [bills, setBills] = useState<Record<string, PluggyBill[] | string>>({})
  const load = async (account: PluggyAccount) => {
    try { setBills(previous => ({ ...previous, [account.id]: [] })); const page = await listPluggyBills(workspaceId, account.id); setBills(previous => ({ ...previous, [account.id]: page.items })) }
    catch (cause) { setBills(previous => ({ ...previous, [account.id]: errorText(cause) })) }
  }
  return <section className="card" aria-labelledby="pluggy-cards-title">
    <h4 id="pluggy-cards-title">4. Cartões e faturas</h4>
    {cards.length === 0 && <p className="meta">Este Item não possui contas de cartão de crédito.</p>}
    {cards.map(card => {
      const result = bills[card.id]
      return <div key={card.id}>
        <h5>{text(card.marketingName ?? card.name)} <small className="meta">{text(card.maskedNumber)} · {text(card.cardBrand)} {card.cardLevel ?? ''}</small></h5>
        <p className="meta">Limite {money(card.creditLimit, card.currency)} · Disponível {money(card.availableCreditLimit, card.currency)} · Pagamento mínimo {money(card.minimumPayment, card.currency)} · Fechamento {day(card.balanceCloseDate)} · Vencimento {day(card.balanceDueDate)}</p>
        <button type="button" onClick={() => void load(card)}>Consultar faturas</button>
        {typeof result === 'string' && <p role="alert" className="notice notice--error">{result}</p>}
        {Array.isArray(result) && result.length === 0 && <p className="meta">Nenhuma fatura retornada.</p>}
        {Array.isArray(result) && result.length > 0 && <Table caption="Faturas retornadas" head={['Fechamento', 'Vencimento', 'Total', 'Pagamento mínimo', 'Pagamentos', 'Parcelamento', 'Encargos']}>
          {result.map(b => <tr key={b.id}>
            <td>{day(b.closingDate)}</td><td>{day(b.dueDate)}</td><td>{money(b.totalAmount, b.currency)}</td><td>{money(b.minimumPayment, b.currency)}</td>
            <td>{b.payments.length ? b.payments.map(p => `${day(p.paymentDate)} ${money(p.amount, p.currency)}`).join(' · ') : NOT_INFORMED}</td>
            <td>{b.allowsInstallments === null ? NOT_INFORMED : b.allowsInstallments ? 'Permite' : 'Não permite'}</td>
            <td>{b.financeCharges.length ? b.financeCharges.map(c => `${text(c.type)} ${money(c.amount, c.currency)}`).join(' · ') : NOT_INFORMED}</td>
          </tr>)}
        </Table>}
      </div>
    })}
  </section>
}

function DiagnosticsSection({ workspaceId, connectionId }: { workspaceId: string; connectionId: string }) {
  const [report, setReport] = useState<PluggyDiagnostics | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const run = async () => {
    setBusy(true); setError(null)
    try { setReport(await getDiagnostics(workspaceId, connectionId)) } catch (cause) { setError(errorText(cause)) } finally { setBusy(false) }
  }
  return <section className="card" aria-labelledby="pluggy-diagnostics-title">
    <h4 id="pluggy-diagnostics-title">5. Diagnóstico de cobertura</h4>
    <p className="meta">Percorre contas, transações e faturas do Item. Pode levar alguns instantes e consome requisições da Pluggy.</p>
    <button type="button" disabled={busy} onClick={() => void run()}>{busy ? 'Gerando…' : 'Gerar diagnóstico'}</button>
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {report && <>
      <p className="meta">{text(report.institution)} · estado {connectionStates[report.state] ?? report.state} · última sincronização {day(report.lastSync)} · produtos do conector: {report.connectorProducts.length ? report.connectorProducts.join(', ') : NOT_INFORMED}</p>
      <Coverage title="Item" resources={report.itemResources} />
      {report.accounts.map(a => <Coverage key={a.accountId} title={`${text(a.name)} (${text(a.type)}/${text(a.subtype)})`} resources={a.resources} />)}
      <ul>{report.notes.map(note => <li key={note} className="meta">{note}</li>)}</ul>
    </>}
  </section>
}

function Coverage({ title, resources }: { title: string; resources: ResourceCoverage[] }) {
  return <Table caption={title} head={['Recurso', 'Situação', 'Registros', 'Intervalo', 'Campos retornados', 'Campos ausentes', 'Limitações']}>
    {resources.map(r => <tr key={r.resource}>
      <td>{r.resource}</td>
      <td>{resourceStates[r.state] ?? r.state}{r.errorKind ? <small className="meta"> ({r.errorKind})</small> : null}</td>
      <td>{r.count}</td>
      <td>{r.earliestDate || r.latestDate ? `${day(r.earliestDate)} – ${day(r.latestDate)}` : NOT_INFORMED}</td>
      <td>{r.fieldsReturned.length ? r.fieldsReturned.join(', ') : NOT_INFORMED}</td>
      <td>{r.fieldsMissing.length ? r.fieldsMissing.join(', ') : '—'}</td>
      <td>{r.limitations.length ? r.limitations.join(' ') : '—'}</td>
    </tr>)}
  </Table>
}
