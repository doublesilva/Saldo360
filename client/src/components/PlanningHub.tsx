import { useState } from 'react'
import type { PlanScopeChoice } from '../api/monthlyPlan'
import type { PlanActualScope } from '../api/planActual'
import { todayInSaoPaulo } from '../dates'
import { FinancialPlanning } from './FinancialPlanning'
import { MonthlyPlanView } from './MonthlyPlanView'
import { SixMonthPlanView } from './SixMonthPlanView'
import { WorkingCalendarView } from './WorkingCalendarView'
import '../MonthlyPlan.css'

type Tab = 'monthly' | 'six' | 'calendar' | 'scenarios'
const tabs: readonly (readonly [Tab, string])[] = [['monthly', 'Plano mensal'], ['six', 'Seis meses'], ['calendar', 'Dias úteis'], ['scenarios', 'Cenários']]

/**
 * Módulo Planejamento (T10.2): o plano mensal do orçamento (metas de receitas, despesas e limites por categoria), a visão de seis meses e o calendário de
 * dias úteis, além dos cenários de recuperação já existentes. Competência e conta escolhidas são compartilhadas entre as visões do plano.
 */
export function PlanningHub({ workspaceId, refreshKey = 0, onChanged }: { workspaceId: string; refreshKey?: number; onChanged?: () => void }) {
  const [tab, setTab] = useState<Tab>('monthly')
  const [competence, setCompetence] = useState(() => todayInSaoPaulo().slice(0, 7))
  const [scope, setScope] = useState<PlanScopeChoice>({ kind: 'primary' })
  // A API é a fonte da verdade do escopo: guardamos a última lista de contas para o seletor não piscar durante o carregamento.
  const [choices, setChoices] = useState<PlanActualScope | null>(null)
  const [version, setVersion] = useState(0)
  const changed = () => { setVersion(value => value + 1); onChanged?.() }
  const value = scope.kind === 'account' ? `account:${scope.accountId}` : scope.kind
  const primaryName = choices?.primaryAccountName
  const filters = tab === 'monthly' || tab === 'six'

  return <section className="family-section planning-hub" aria-labelledby="planning-hub-title">
    <div className="section-heading"><div><h3 id="planning-hub-title">Planejamento</h3>
      <p className="meta">Metas mensais de receitas, despesas e limites por categoria. O plano é meta do orçamento: não cria lançamentos, pagamentos nem transferências e não altera saldos.</p></div></div>
    <div className="planning-hub__tabs" role="tablist" aria-label="Seções do planejamento">
      {tabs.map(([id, label]) => <button key={id} type="button" role="tab" id={`planning-tab-${id}`} aria-selected={tab === id} aria-controls="planning-panel" onClick={() => setTab(id)}>{label}</button>)}
    </div>
    {filters && <div className="planning-hub__filters">
      <label>{tab === 'six' ? 'Competência inicial' : 'Competência'}<input type="month" aria-label="Competência" value={competence} onChange={event => event.target.value && setCompetence(event.target.value)} /></label>
      <label>Conta
        <select aria-label="Conta do planejamento" value={value} onChange={event => {
          const next = event.target.value
          setScope(next.startsWith('account:') ? { kind: 'account', accountId: next.slice('account:'.length) } : { kind: next as 'primary' | 'all' })
        }}>
          <option value="primary">{primaryName ? `Conta principal · ${primaryName}` : 'Conta principal'}</option>
          <option value="all">Todas as contas (consolidado)</option>
          {(choices?.accounts ?? []).map(account => <option key={account.accountId} value={`account:${account.accountId}`}>{account.name} · {account.scope}{account.isActive ? '' : ' (inativa)'}</option>)}
        </select>
      </label>
    </div>}
    {tab === 'calendar' && <div className="planning-hub__filters"><label>Competência<input type="month" aria-label="Competência" value={competence} onChange={event => event.target.value && setCompetence(event.target.value)} /></label></div>}
    <div id="planning-panel" role="tabpanel" aria-labelledby={`planning-tab-${tab}`}>
      {tab === 'monthly' && <MonthlyPlanView workspaceId={workspaceId} competence={competence} scope={scope} refreshKey={refreshKey + version} onScope={setChoices} onChanged={changed} />}
      {tab === 'six' && <SixMonthPlanView workspaceId={workspaceId} from={competence} scope={scope} refreshKey={refreshKey + version} onScope={setChoices} />}
      {tab === 'calendar' && <WorkingCalendarView workspaceId={workspaceId} competence={competence} />}
      {tab === 'scenarios' && <FinancialPlanning workspaceId={workspaceId} refreshKey={refreshKey} />}
    </div>
  </section>
}
