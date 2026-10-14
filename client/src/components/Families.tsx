import { useEffect, useState, type FormEvent } from 'react'
import {
  createFamily,
  FAMILY_NAME_MAX_LENGTH,
  getFamily,
  listFamilies,
  type FamilyDetails,
  type FamilyRole,
  type FamilySummary,
} from '../api/families'
import { Accounts } from './Accounts'
import { Categories } from './Categories'
import { Transactions } from './Transactions'
import { Planned } from './Planned'
import { Transfers } from './Transfers'
import { InvitePanel } from './InvitePanel'
import { FinancialCalendarPage } from './FinancialCalendar'

const roleLabels: Record<FamilyRole, string> = { Owner: 'Responsável', Member: 'Membro' }

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Erro desconhecido.'
}

type ListState =
  | { kind: 'loading' }
  | { kind: 'loaded'; families: FamilySummary[] }
  | { kind: 'error'; message: string }

/** Núcleos familiares do usuário autenticado: lista, criação e detalhe (opcionalmente já aberto numa família). */
export function Families({ initialFamily = null }: { initialFamily?: FamilyDetails | null }) {
  const [list, setList] = useState<ListState>({ kind: 'loading' })
  const [selected, setSelected] = useState<FamilyDetails | string | null>(initialFamily)
  const [showForm, setShowForm] = useState(false)
  const [reloadKey, setReloadKey] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    listFamilies(controller.signal)
      .then((families) => setList({ kind: 'loaded', families }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setList({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [reloadKey])

  const handleCreated = (family: FamilyDetails) => {
    setShowForm(false)
    setSelected(family)
    setReloadKey((n) => n + 1)
  }

  if (selected !== null) {
    return <FamilyView family={selected} onBack={() => setSelected(null)} />
  }

  return (
    <section className="card" aria-labelledby="families-title">
      <h2 id="families-title">Núcleo familiar</h2>

      {list.kind === 'loading' && <p className="status status--pending">Carregando…</p>}
      {list.kind === 'error' && <p className="status status--down">{list.message}</p>}

      {list.kind === 'loaded' && list.families.length === 0 && (
        <>
          <p className="meta">Você ainda não participa de nenhum núcleo familiar.</p>
          <CreateFamilyForm onCreated={handleCreated} />
        </>
      )}

      {list.kind === 'loaded' && list.families.length > 0 && (
        <>
          <ul className="family-list">
            {list.families.map((family) => (
              <li key={family.id}>
                <button type="button" className="family-list__item" onClick={() => setSelected(family.id)}>
                  <span className="family-list__name">{family.name}</span>
                  <span className="meta">
                    {roleLabels[family.myRole]} · {family.memberCount} {family.memberCount === 1 ? 'membro' : 'membros'}
                  </span>
                </button>
              </li>
            ))}
          </ul>
          {showForm ? (
            <CreateFamilyForm onCreated={handleCreated} onCancel={() => setShowForm(false)} />
          ) : (
            <button type="button" className="button--link" onClick={() => setShowForm(true)}>
              Criar outro núcleo familiar
            </button>
          )}
        </>
      )}
    </section>
  )
}

function CreateFamilyForm({ onCreated, onCancel }: { onCreated: (family: FamilyDetails) => void; onCancel?: () => void }) {
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (name.trim().length === 0) {
      setError('Informe o nome do núcleo familiar.')
      return
    }

    setBusy(true)
    setError(null)
    try {
      const result = await createFamily(name)
      if (result.ok) {
        onCreated(result.family)
      } else {
        setError(result.errors.name?.[0] ?? 'Verifique os dados informados.')
      }
    } catch (failure: unknown) {
      setError(errorMessage(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="form" onSubmit={handleSubmit} noValidate>
      <h3>Criar núcleo familiar</h3>
      <label htmlFor="family-name">Nome</label>
      <input
        id="family-name"
        name="name"
        value={name}
        onChange={(event) => setName(event.target.value)}
        maxLength={FAMILY_NAME_MAX_LENGTH}
        placeholder="Ex.: Família Silva"
        autoComplete="off"
        aria-invalid={error !== null}
        aria-describedby={error ? 'family-name-error' : undefined}
        required
      />
      {error && (
        <p id="family-name-error" className="field-error" role="alert">
          {error}
        </p>
      )}
      <div className="form__actions">
        <button type="submit" className="button--primary" disabled={busy}>
          {busy ? 'Criando…' : 'Criar'}
        </button>
        {onCancel && (
          <button type="button" onClick={onCancel} disabled={busy}>
            Cancelar
          </button>
        )}
      </div>
    </form>
  )
}

type DetailState =
  | { kind: 'loading' }
  | { kind: 'loaded'; family: FamilyDetails }
  | { kind: 'not-found' }
  | { kind: 'error'; message: string }

type FamilySection = 'overview' | 'calendar' | 'accounts' | 'categories' | 'transactions' | 'transfers' | 'planned'

const familySections: Array<{ id: FamilySection; label: string }> = [
  { id: 'overview', label: 'Família' },
  { id: 'calendar', label: 'Agenda' },
  { id: 'accounts', label: 'Contas' },
  { id: 'categories', label: 'Categorias' },
  { id: 'transactions', label: 'Lançamentos' },
  { id: 'transfers', label: 'Transferências' },
  { id: 'planned', label: 'Previsões' },
]

/** Detalhe de uma família: recebe os dados prontos (após criar) ou o Id para buscar. */
function FamilyView({ family, onBack }: { family: FamilyDetails | string; onBack: () => void }) {
  const [state, setState] = useState<DetailState>(
    typeof family === 'string' ? { kind: 'loading' } : { kind: 'loaded', family },
  )
  // Muda a cada mudança financeira (lançamento, transferência ou realização criados, editados ou cancelados):
  // saldos, lançamentos e previsões consultam de novo. Ex.: realizar cria lançamento; cancelar o lançamento
  // efetivo reabre a previsão.
  const [financeVersion, setFinanceVersion] = useState(0)
  const [section, setSection] = useState<FamilySection>('overview')
  const financeChanged = () => setFinanceVersion((n) => n + 1)

  useEffect(() => {
    if (typeof family !== 'string') return
    const controller = new AbortController()
    getFamily(family, controller.signal)
      .then((details) => setState(details ? { kind: 'loaded', family: details } : { kind: 'not-found' }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ kind: 'error', message: errorMessage(error) })
      })
    return () => controller.abort()
  }, [family])

  return (
    <section className="card" aria-live="polite">
      <button type="button" className="button--link" onClick={onBack}>
        ← Núcleos familiares
      </button>

      {state.kind === 'loading' && <p className="status status--pending">Carregando…</p>}
      {state.kind === 'not-found' && <p className="status status--down">Família não encontrada.</p>}
      {state.kind === 'error' && <p className="status status--down">{state.message}</p>}

      {state.kind === 'loaded' && (
        <>
          <h2>{state.family.name}</h2>
          <nav className="family-navigation" aria-label="Navegação principal da família">
            {familySections.map((item) => (
              <button
                key={item.id}
                type="button"
                className={section === item.id ? 'family-navigation__item family-navigation__item--active' : 'family-navigation__item'}
                aria-current={section === item.id ? 'page' : undefined}
                onClick={() => setSection(item.id)}
              >
                {item.label}
              </button>
            ))}
          </nav>

          {section === 'overview' && (
            <div className="family-section">
              <h3>Membros</h3>
              <ul className="checks">
                {state.family.members.map((member, index) => (
                  <li key={`${member.joinedAt}-${index}`}>
                    <span>
                      {member.displayName}
                      {member.isCurrentUser && <span className="meta"> (você)</span>}
                    </span>
                    <span>{roleLabels[member.role]}</span>
                  </li>
                ))}
              </ul>
              {state.family.myRole === 'Owner' && <InvitePanel familyId={state.family.id} />}
            </div>
          )}
          {section === 'calendar' && <FinancialCalendarPage key={state.family.id} familyId={state.family.id} />}
          {section === 'accounts' && (
            <Accounts familyId={state.family.id} members={state.family.members} balanceVersion={financeVersion} />
          )}
          {section === 'categories' && <Categories familyId={state.family.id} />}
          {section === 'transactions' && (
            <Transactions familyId={state.family.id} onChanged={financeChanged} refreshKey={financeVersion} />
          )}
          {section === 'transfers' && <Transfers familyId={state.family.id} onChanged={financeChanged} />}
          {/* Toda mudança em previsões muda a projeção (e realizar muda o saldo realizado): avisa. */}
          {section === 'planned' && (
            <Planned familyId={state.family.id} onChanged={financeChanged} refreshKey={financeVersion} />
          )}
        </>
      )}
    </section>
  )
}
