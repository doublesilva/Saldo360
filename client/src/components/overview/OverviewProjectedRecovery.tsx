import { useCallback, useState } from 'react'
import { getFinancialProjection, PROJECTION_HORIZONS, type ProjectionHorizon } from '../../api/projection'
import { OperationalError, OperationalLoading } from '../OperationalFeedback'
import { Amount } from '../ProjectionPanels'
import { scenarioLabels } from '../projectionText'
import { monthTitle } from './overviewText'
import { useLoad } from './useLoad'

/**
 * Resumo enxuto da projeção financeira na Visão Geral: uso do cheque especial na abertura da competência escolhida e no fechamento do horizonte, e a
 * recuperação acumulada em cada cenário. Os valores vêm de `monthly-plan/projection`; detalhes e gráficos ficam no Planejamento.
 */
export function OverviewProjectedRecovery({ workspaceId, competence, refreshKey, onOpen }: { workspaceId: string; competence: string; refreshKey: number; onOpen: () => void }) {
  const [horizon, setHorizon] = useState<ProjectionHorizon>(6)
  const { load, previous, retry } = useLoad(`${workspaceId}|${competence}|${horizon}|${refreshKey}`, useCallback((signal: AbortSignal) => getFinancialProjection(workspaceId, competence, horizon, signal), [workspaceId, competence, horizon]))
  const data = load.state === 'loaded' ? load.data : previous
  const first = data?.scenarios[0]?.months[0]
  const last = data?.scenarios[0]?.months.at(-1)
  return <section className="ov-card ov-projected-recovery" aria-labelledby="ov-projected-recovery-title">
    <header><div><h3 id="ov-projected-recovery-title">Recuperação do limite projetada</h3>
      <p className="ov-meta">Projeção financeira automática da conta PF</p></div>
      <label className="ov-inline-filter">Horizonte
        <select aria-label="Horizonte da recuperação do limite" value={horizon} onChange={event => setHorizon(Number(event.target.value) as ProjectionHorizon)}>
          {PROJECTION_HORIZONS.map(value => <option key={value} value={value}>{value} meses</option>)}
        </select></label></header>
    {!data && load.state === 'loading' && <OperationalLoading>Carregando a projeção do limite…</OperationalLoading>}
    {load.state === 'error' && <OperationalError error={load.error} onRetry={retry} />}
    {data && first && last && <div className={load.state === 'loading' ? 'ov-stale' : undefined}>
      <dl className="account__details">
        <div><dt>Utilização na abertura de {monthTitle(first.competence)} (posição de referência)</dt><dd><Amount value={first.overdraft?.usedStart ?? null} /></dd></div>
        {data.scenarios.map(scenario => {
          const end = scenario.months.at(-1)
          return <div key={scenario.name} className="ov-projected-recovery__scenario">
            <dt>{scenarioLabels[scenario.name]} · fechamento de {monthTitle(last.competence)}</dt>
            <dd>Utilização <Amount value={end?.overdraft?.usedEnd ?? null} /> · Recuperação acumulada <Amount value={end?.overdraft?.cumulativeRecovery ?? null} signed />{end && !end.complete ? ' · projeção parcial' : ''}</dd>
          </div>
        })}
      </dl>
      {data.notes.includes('OverdraftChargesNotEstimated') && <p className="ov-meta" role="note">Juros e IOF futuros do cheque especial não são estimados.</p>}
    </div>}
    <div className="form__actions"><button type="button" onClick={onOpen}>Abrir a Projeção financeira</button></div>
  </section>
}
