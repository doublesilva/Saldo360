import type { ProjectionMonth, ProjectionScenario } from '../api/projection'
import { formatMonthYear } from '../dates'
import { isNegativeDecimal } from '../money'
import { money } from './monthlyPlanText'
import { scenarioLabels } from './projectionText'

const W = 640, H = 240, PAD = { left: 12, right: 12, top: 14, bottom: 30 }

type Point = { index: number; value: number | null; month: ProjectionMonth }

const shortMonth = (competence: string) => formatMonthYear(`${competence}-01`).slice(0, 3).toLowerCase() + '/' + competence.slice(2, 4)

/** Geometria apenas: o texto exibido é sempre o valor exato recebido da API; `null` quebra a linha em vez de virar zero. */
const numeric = (text: string | null) => text === null ? null : Number.parseFloat(text)

function scale(points: Point[][], includeZero: boolean) {
  const values = points.flat().map(point => point.value).filter((value): value is number => value !== null && Number.isFinite(value))
  let min = Math.min(...values, includeZero ? 0 : Infinity), max = Math.max(...values, includeZero ? 0 : -Infinity)
  if (!Number.isFinite(min) || !Number.isFinite(max)) { min = 0; max = 1 }
  if (min === max) { min -= 1; max += 1 }
  return { min, max }
}

function segmentsOf(points: Point[], x: (index: number) => number, y: (value: number) => number): string[] {
  const parts: string[] = []
  let current: string[] = []
  for (const point of points) {
    if (point.value === null || !Number.isFinite(point.value)) { if (current.length) parts.push(current.join(' ')); current = []; continue }
    current.push(`${current.length ? 'L' : 'M'}${x(point.index).toFixed(1)} ${y(point.value).toFixed(1)}`)
  }
  if (current.length) parts.push(current.join(' '))
  return parts
}

interface ChartProps {
  title: string; description: string; scenarios: ProjectionScenario[]; selected: string; onSelect: (competence: string) => void
  pick: (month: ProjectionMonth) => string | null; includeZero: boolean; zeroLine: boolean; worsened?: (month: ProjectionMonth) => boolean
  labelFor: (month: ProjectionMonth) => string
}

function LineChart({ title, description, scenarios, selected, onSelect, pick, includeZero, zeroLine, worsened, labelFor }: ChartProps) {
  const months = scenarios[0]?.months ?? []
  const count = months.length
  const series = scenarios.map(scenario => ({ name: scenario.name, points: scenario.months.map((month, index): Point => ({ index, value: numeric(pick(month)), month })) }))
  const { min, max } = scale(series.map(item => item.points), includeZero)
  const x = (index: number) => count <= 1 ? W / 2 : PAD.left + (index / (count - 1)) * (W - PAD.left - PAD.right)
  const y = (value: number) => PAD.top + (1 - (value - min) / (max - min)) * (H - PAD.top - PAD.bottom)
  const step = Math.max(1, Math.ceil(count / 8))
  const selectedIndex = months.findIndex(month => month.competence === selected)
  return <figure className="proj-chart">
    <figcaption><strong>{title}</strong><span className="meta">{description}</span></figcaption>
    <svg viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`${title}. ${description}`} preserveAspectRatio="xMidYMid meet">
      {zeroLine && min < 0 && max > 0 && <g><line className="proj-chart__zero" x1={PAD.left} x2={W - PAD.right} y1={y(0)} y2={y(0)} /><text className="proj-chart__axis" x={W - PAD.right} y={y(0) - 4} textAnchor="end">R$ 0</text></g>}
      {!zeroLine && <line className="proj-chart__zero" x1={PAD.left} x2={W - PAD.right} y1={y(min)} y2={y(min)} />}
      {selectedIndex >= 0 && <line className="proj-chart__selected" x1={x(selectedIndex)} x2={x(selectedIndex)} y1={PAD.top} y2={H - PAD.bottom} />}
      {series.map(item => <g key={item.name} className={`proj-series proj-series--${item.name}`}>
        {segmentsOf(item.points, x, y).map(path => <path key={path} d={path} />)}
        {item.points.map(point => point.value === null ? null : <circle key={point.index} cx={x(point.index)} cy={y(point.value)} r={point.month.competence === selected ? 5 : 3.2}
          className={worsened?.(point.month) ? 'proj-point--worse' : undefined}><title>{`${scenarioLabels[item.name]} · ${formatMonthYear(`${point.month.competence}-01`)}: ${labelFor(point.month)}`}</title></circle>)}
      </g>)}
      {months.map((month, index) => <g key={month.competence}>
        {index % step === 0 && <text className="proj-chart__axis" x={x(index)} y={H - 10} textAnchor="middle">{shortMonth(month.competence)}</text>}
        <rect className="proj-chart__hit" x={x(index) - (W / Math.max(count, 1)) / 2} y={PAD.top} width={W / Math.max(count, 1)} height={H - PAD.top - PAD.bottom} tabIndex={0} role="button"
          aria-label={`Selecionar ${formatMonthYear(`${month.competence}-01`)}`} aria-pressed={month.competence === selected}
          onClick={() => onSelect(month.competence)} onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); onSelect(month.competence) } }} />
      </g>)}
    </svg>
    <ul className="proj-legend" aria-label="Legenda">{scenarios.map(scenario => <li key={scenario.name}><i className={`proj-swatch proj-swatch--${scenario.name}`} />{scenarioLabels[scenario.name]}</li>)}</ul>
  </figure>
}

/** Evolução do saldo PF no fechamento de cada mês (`pf.closing`), uma linha por cenário e linha de referência em zero. */
export function BalanceChart(props: Pick<ChartProps, 'scenarios' | 'selected' | 'onSelect'>) {
  return <LineChart {...props} title="Evolução do saldo PF" description="Saldo final de cada mês. Meses sem saldo conhecido ficam sem ponto." pick={month => month.pf.closing}
    includeZero zeroLine labelFor={month => money(month.pf.closing)} />
}

/** Utilização do cheque especial no fechamento (`overdraft.usedEnd`). O ponto destacado marca mês em que a utilização aumentou (recuperação mensal negativa). */
export function OverdraftChart(props: Pick<ChartProps, 'scenarios' | 'selected' | 'onSelect'>) {
  return <LineChart {...props} title="Utilização do cheque especial" description="Limite usado no fechamento de cada mês. Ponto vazado: a utilização aumentou no mês." pick={month => month.overdraft?.usedEnd ?? null}
    includeZero zeroLine={false} worsened={month => month.overdraft !== null && isNegativeDecimal(month.overdraft.monthlyRecovery)}
    labelFor={month => `${money(month.overdraft?.usedEnd ?? null)}${month.overdraft && isNegativeDecimal(month.overdraft.monthlyRecovery) ? ' (aumentou)' : ''}`} />
}

