import { useEffect, useMemo, useState } from 'react'
import { getFinancialProjection, PROJECTION_HORIZONS, type FinancialProjection, type ProjectionHorizon } from '../api/projection'
import { formatMonthYear } from '../dates'
import { FoodProvisionSettings } from './FoodProvisionSettings'
import { IncomeForecastEditor } from './IncomeForecastEditor'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import { BalanceChart, OverdraftChart } from './ProjectionCharts'
import { ProjectionDetail, ProjectionIndicators, ProjectionSummaryTable } from './ProjectionPanels'
import { generalNotes, groupQuality, modeLabels, scenarioHints, scenarioLabels, type ScenarioMode } from './projectionText'

type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: FinancialProjection }

/**
 * Projeção financeira (Entrega 3): saldo PF e uso do cheque especial ao fim de cada mês, nos cenários automáticos Atual e Tetos (não confundir com os
 * "Cenários" configuráveis). Consome `monthly-plan/projection`; o navegador só apresenta os valores e nunca grava nada ao consultar.
 */
export function FinancialProjectionView({ workspaceId, from, refreshKey }: { workspaceId: string; from: string; refreshKey: number }) {
  const [horizon, setHorizon] = useState<ProjectionHorizon>(6)
  const [mode, setMode] = useState<ScenarioMode>('both')
  const [picked, setPicked] = useState<string | null>(null)
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${from}|${horizon}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }
  useEffect(() => {
    const controller = new AbortController()
    getFinancialProjection(workspaceId, from, horizon, controller.signal)
      .then(data => { if (!controller.signal.aborted) setLoaded({ key, state: 'loaded', data }) })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, from, horizon, key])

  const data = visible.state === 'loaded' ? visible.data : null
  // Mantém a última projeção carregada na tela durante a recarga, para os filtros não piscarem vazios.
  const [last, setLast] = useState<FinancialProjection | null>(null)
  useEffect(() => { if (data) setLast(data) }, [data])
  const shown = data ?? last
  const scenarios = useMemo(() => (shown?.scenarios ?? []).filter(scenario => mode === 'both' || scenario.name === mode), [shown, mode])
  const months = shown?.scenarios[0]?.months ?? []
  const selected = picked && months.some(month => month.competence === picked) ? picked : months[0]?.competence ?? from
  const groups = useMemo(() => groupQuality(scenarios), [scenarios])
  const notes = useMemo(() => generalNotes(shown?.notes ?? [], groups), [shown, groups])
  const partial = [...groups, ...notes].some(group => group.message.severity === 'partial')

  return <div className="monthly-plan proj">
    <div className="planning-hub__filters" role="group" aria-label="Filtros da projeção">
      <label>Horizonte
        <select aria-label="Horizonte" value={horizon} onChange={event => setHorizon(Number(event.target.value) as ProjectionHorizon)}>
          {PROJECTION_HORIZONS.map(value => <option key={value} value={value}>{value} meses</option>)}
        </select></label>
      <label>Cenário
        <select aria-label="Cenário" value={mode} onChange={event => setMode(event.target.value as ScenarioMode)}>
          {(Object.keys(modeLabels) as ScenarioMode[]).map(value => <option key={value} value={value}>{modeLabels[value]}</option>)}
        </select></label>
      <label>Mês em detalhe
        <select aria-label="Mês em detalhe" value={selected} onChange={event => setPicked(event.target.value)} disabled={months.length === 0}>
          {months.map(month => <option key={month.competence} value={month.competence}>{formatMonthYear(`${month.competence}-01`)}</option>)}
        </select></label>
    </div>

    {!shown && visible.state === 'loading' && <OperationalLoading>Carregando a projeção financeira…</OperationalLoading>}
    {visible.state === 'error' && <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />}

    {shown && <div className={visible.state === 'loading' ? 'ov-stale' : undefined} aria-busy={visible.state === 'loading' || undefined}>
      <p className="meta">{formatMonthYear(`${shown.from}-01`)} · {shown.monthCount} meses · posição em {shown.referenceDate.split('-').reverse().join('/')}. Cenários automáticos, só leitura: nada é gravado nem executado.</p>
      <ul className="proj-scenario-hints">{scenarios.map(scenario => <li key={scenario.name}><strong>{scenarioLabels[scenario.name]}:</strong> {scenarioHints[scenario.name]}</li>)}</ul>
      {partial && <p className="notice notice--warning" role="note">Projeção parcial: há avisos abaixo. Os valores mostram só o que o sistema conhece e não são garantidos.</p>}

      <ProjectionIndicators scenarios={scenarios} competence={selected} />
      <div className="proj-charts">
        <BalanceChart scenarios={scenarios} selected={selected} onSelect={setPicked} />
        <OverdraftChart scenarios={scenarios} selected={selected} onSelect={setPicked} />
      </div>
      <ProjectionSummaryTable scenarios={scenarios} selected={selected} onSelect={setPicked} />
      <ProjectionDetail scenarios={scenarios} competence={selected} />

      <section aria-labelledby="proj-quality-title" className="proj-quality">
        <h4 id="proj-quality-title">Qualidade dos dados</h4>
        {groups.length + notes.length === 0 ? <p className="meta">Nenhum aviso: a cobertura é completa para o horizonte consultado.</p>
          : <ul className="plan-card__notes">
            {[...groups, ...notes].map(group => <li key={group.code} data-severity={group.message.severity}>
              <strong>{group.message.severity === 'partial' ? 'Projeção parcial · ' : 'Informação · '}</strong>{group.message.text}
              {group.competences.length > 0 && <details><summary>Competências ({group.competences.length})</summary>
                <p className="meta">{group.competences.map(competence => formatMonthYear(`${competence}-01`)).join(', ')}{group.scenarios.length === 1 ? ` · somente no cenário ${scenarioLabels[group.scenarios[0]]}` : ''}</p></details>}
            </li>)}
          </ul>}
      </section>
    </div>}

    <FoodProvisionSettings workspaceId={workspaceId} competence={selected} onSaved={() => setReload(value => value + 1)} />
    <IncomeForecastEditor workspaceId={workspaceId} from={from} onChanged={() => setReload(value => value + 1)} />
  </div>
}
