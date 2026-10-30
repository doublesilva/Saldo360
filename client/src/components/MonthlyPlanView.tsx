import { useCallback, useEffect, useState } from 'react'
import { getMonthlyPlan, removeCategoryLimit, removePlanLine, type CategoryLimit, type CopyResult, type MonthlyPlan, type PlanLine, type PlanLineKind, type PlanScopeChoice } from '../api/monthlyPlan'
import type { PlanActualScope, PlanIndicator } from '../api/planActual'
import { listCategories, type FinancialCategory } from '../api/categories'
import { toUserMessage } from '../api/apiErrors'
import { formatMonthYear } from '../dates'
import { OperationalError, OperationalLoading } from './OperationalFeedback'
import { CategoryLimitForm, CopyPlanDialog, PlanHistoryPanel, PlanLineForm } from './MonthlyPlanForms'
import { kindLabels, limitStatusLabels, money, natureLabels, pendencyText, periodLabels, plannedMoney, sourceText } from './monthlyPlanText'
import '../PlanActual.css'
import '../MonthlyPlan.css'

type Panel =
  | { type: 'line'; kind: PlanLineKind; line?: PlanLine }
  | { type: 'limit'; limit?: CategoryLimit }
  | { type: 'copy' }
  | { type: 'history'; subjectId?: string; title: string }
type Load = { key: string; state: 'loading' } | { key: string; state: 'error'; error: unknown } | { key: string; state: 'loaded'; data: MonthlyPlan }

const percent = (value: string | null) => value === null ? null : `${value.replace('.', ',')}%`

/** Visão mensal do planejamento: metas, limites, linhas e a realização do mês (que vem do Dashboard, não é recalculada aqui). */
export function MonthlyPlanView({ workspaceId, competence, scope, refreshKey, onScope, onChanged }: {
  workspaceId: string; competence: string; scope: PlanScopeChoice; refreshKey: number; onScope: (choices: PlanActualScope) => void; onChanged: () => void
}) {
  const [reload, setReload] = useState(0)
  const key = `${workspaceId}|${competence}|${scope.kind}|${scope.accountId ?? ''}|${refreshKey}|${reload}`
  const [loaded, setLoaded] = useState<Load>({ key, state: 'loading' })
  const [categories, setCategories] = useState<FinancialCategory[]>([])
  const [panel, setPanel] = useState<Panel | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [failure, setFailure] = useState<string | null>(null)
  const visible: Load = loaded.key === key ? loaded : { key, state: 'loading' }

  useEffect(() => {
    const controller = new AbortController()
    getMonthlyPlan(workspaceId, competence, scope, controller.signal)
      .then(data => { if (!controller.signal.aborted) { setLoaded({ key, state: 'loaded', data }); onScope(data.scope) } })
      .catch((error: unknown) => { if (!controller.signal.aborted) setLoaded({ key, state: 'error', error }) })
    return () => controller.abort()
  }, [workspaceId, competence, scope, key]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const controller = new AbortController()
    listCategories(workspaceId, true, controller.signal).then(items => { if (!controller.signal.aborted) setCategories(items) }).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId])

  const refresh = useCallback(() => { setReload(value => value + 1); onChanged() }, [onChanged])
  const saved = (message: string) => { setPanel(null); setNotice(message); setFailure(null); refresh() }
  const run = async (task: () => Promise<unknown>, message: string) => {
    try { await task(); setNotice(message); setFailure(null); refresh() } catch (error) { setFailure(toUserMessage(error)); setNotice(null) }
  }

  if (visible.state === 'loading') return <OperationalLoading>Carregando o planejamento mensal…</OperationalLoading>
  if (visible.state === 'error') return <OperationalError error={visible.error} onRetry={() => setReload(value => value + 1)} />
  const plan = visible.data
  const accounts = plan.scope.accounts
  const allAccounts = plan.scope.kind === 'all'

  return <div className="monthly-plan">
    <p className="meta">{formatMonthYear(`${plan.competence}-01`)} · {periodLabels[plan.periodKind]} · {plan.scope.kind === 'all' ? 'Todas as contas' : plan.scope.accountName ? `${plan.scope.accountName}${plan.scope.accountScope ? ` · ${plan.scope.accountScope}` : ''}` : 'Conta'}</p>
    {plan.scope.primaryMissing && <p className="notice" role="status">Nenhuma conta principal definida: exibindo todas as contas.</p>}
    {!plan.execution.snapshotConsistent && <p className="notice notice--warning" role="status">Os dados mudaram durante a leitura. Atualize para ver um retrato consistente.</p>}
    {notice && <p className="status" role="status">{notice}</p>}
    {failure && <p className="status status--down" role="alert">{failure}</p>}

    <div className="monthly-plan__actions" role="group" aria-label="Ações do planejamento">
      <button type="button" className="button--primary" onClick={() => setPanel({ type: 'line', kind: 'Income' })}>Adicionar receita</button>
      <button type="button" className="button--primary" onClick={() => setPanel({ type: 'line', kind: 'Expense' })}>Adicionar despesa</button>
      <button type="button" onClick={() => setPanel({ type: 'limit' })}>Definir limite por categoria</button>
      <button type="button" onClick={() => setPanel({ type: 'line', kind: 'Transfer' })}>Planejar transferência</button>
      <button type="button" onClick={() => setPanel({ type: 'copy' })}>Copiar mês anterior</button>
      <button type="button" onClick={() => setPanel({ type: 'history', title: `Histórico de ${formatMonthYear(`${plan.competence}-01`)}` })}>Histórico do mês</button>
    </div>

    {panel?.type === 'line' && <PlanLineForm key={panel.line?.id ?? panel.kind} workspaceId={workspaceId} competence={plan.competence} kind={panel.kind} line={panel.line} accounts={accounts} categories={categories}
      onSaved={() => saved(panel.line ? 'Linha revisada. O valor original foi preservado no histórico.' : 'Linha adicionada ao planejamento.')} onCancel={() => setPanel(null)} />}
    {panel?.type === 'limit' && <CategoryLimitForm key={panel.limit?.categoryId ?? 'new'} workspaceId={workspaceId} competence={plan.competence} limit={panel.limit} categories={categories}
      onSaved={() => saved('Limite salvo.')} onCancel={() => setPanel(null)} />}
    {panel?.type === 'copy' && <CopyPlanDialog workspaceId={workspaceId} competence={plan.competence} onCancel={() => setPanel(null)}
      onCopied={(result: CopyResult) => saved(`Planejamento copiado: ${result.linesCopied} linha(s) e ${result.limitsCopied} limite(s)${result.linesSkipped + result.limitsSkipped > 0 ? `; ${result.linesSkipped + result.limitsSkipped} item(ns) não puderam ser copiados (conta ou categoria inativa, ou limite já existente)` : ''}${result.recalculatedHourlyLines.length > 0 ? `; ${result.recalculatedHourlyLines.length} receita(s) por hora tiveram os dias faturáveis limitados ao calendário do mês` : ''}.`)} />}
    {panel?.type === 'history' && <PlanHistoryPanel workspaceId={workspaceId} competence={plan.competence} subjectId={panel.subjectId} title={panel.title} onClose={() => setPanel(null)} />}

    {plan.pendencies.length > 0 && <section className="monthly-plan__pendencies" aria-label="Pendências do planejamento"><h5>Pendências</h5>
      <ul>{plan.pendencies.map(item => <li key={item.code}>{pendencyText[item.code] ?? item.code}{item.code !== 'NoPlan' ? ` (${item.count})` : ''}</li>)}</ul></section>}

    <div className="plan-actual__grid">
      <ExecutionCard title="Receitas" ariaLabel="Receitas: planejado, projetado e realizado" indicator={plan.execution.income} realizedLabel="Recebido" />
      <ExecutionCard title="Despesas (consumo)" ariaLabel="Despesas: planejado, projetado e realizado" indicator={plan.execution.consumption} realizedLabel="Efetivo" />
      <article className="card plan-card" aria-label="Resultado e reservas planejados"><header><h5>Resultado planejado</h5></header>
        <p className="plan-card__main"><small>Receita planejada − despesa planejada</small><strong>{plannedMoney(plan.planned.result)}</strong></p>
        <dl className="plan-card__facts">
          <div><dt>Receitas planejadas</dt><dd>{plannedMoney(plan.planned.income)}</dd></div>
          <div><dt>Despesas planejadas</dt><dd>{plannedMoney(plan.planned.expense)}</dd></div>
          <div><dt>Reservas e provisões</dt><dd>{plan.planned.reserveLines > 0 ? money(plan.planned.reserve) : 'Não definido'}</dd></div>
          {plan.planned.transferCount > 0 && <div><dt>Transferências planejadas (saem · entram)</dt><dd>{money(plan.planned.transfersOut)} · {money(plan.planned.transfersIn)}</dd></div>}
        </dl>
        <p className="meta">Reservas e transferências ficam fora da comparação com o realizado: não são despesa nem receita consolidada.</p>
      </article>
      <article className="card plan-card" aria-label="Meta dos cartões"><header><h5>Meta dos cartões</h5></header>
        <p className="plan-card__main"><small>Meta existente do Dashboard</small><strong>{plannedMoney(plan.execution.cards.target)}</strong></p>
        <dl className="plan-card__facts">
          <div><dt>Projetado</dt><dd>{money(plan.execution.cards.projected)}</dd></div>
          <div><dt>Confirmado</dt><dd>{money(plan.execution.cards.confirmed)}</dd></div>
        </dl>
        <p className="meta">A meta dos cartões é independente das linhas e dos limites; edite-a no painel Planejado × Realizado da Visão Geral.</p>
      </article>
    </div>

    <section className="monthly-plan__section" aria-labelledby="plan-limits-title">
      <h5 id="plan-limits-title">Limites por categoria</h5>
      {plan.planned.limitsNotAttributable && <p className="meta">Os limites por categoria valem para todas as contas; escolha “Todas as contas” para comparar o total planejado.</p>}
      {plan.categoryLimits.length === 0 ? <p className="meta">Nenhum limite definido para este mês.</p> : <div className="monthly-plan__table-wrap"><table className="monthly-plan__table">
        <caption className="sr-only">Limites mensais por categoria e execução</caption>
        <thead><tr><th scope="col">Categoria</th><th scope="col">Limite</th><th scope="col">Realizado</th><th scope="col">Comprometido</th><th scope="col">Projetado</th><th scope="col">Disponível</th><th scope="col">Situação</th><th scope="col">Ações</th></tr></thead>
        <tbody>{plan.categoryLimits.map(limit => <tr key={limit.targetId}>
          <th scope="row" data-label="Categoria">{limit.categoryName}{limit.itemizedAboveLimit && <small className="monthly-plan__warn"> · linhas detalhadas somam {money(limit.itemizedAmount)}</small>}</th>
          <td data-label="Limite">{money(limit.amount)}{limit.revised && limit.originalAmount && <small> · original {money(limit.originalAmount)}</small>}</td>
          <td data-label="Realizado">{limit.execution ? money(limit.execution.realized) : '—'}</td>
          <td data-label="Comprometido">{limit.execution ? money(limit.execution.committed) : '—'}</td>
          <td data-label="Projetado">{limit.execution ? `${money(limit.execution.projected)}${percent(limit.execution.percent) ? ` · ${percent(limit.execution.percent)}` : ''}` : '—'}</td>
          <td data-label="Disponível">{limit.execution ? money(limit.execution.available) : '—'}</td>
          <td data-label="Situação">{limit.execution ? limitStatusLabels[limit.execution.status] ?? limit.execution.status : '—'}</td>
          <td data-label="Ações" className="monthly-plan__row-actions">
            <button type="button" onClick={() => setPanel({ type: 'limit', limit })}>Revisar</button>
            <button type="button" onClick={() => setPanel({ type: 'history', subjectId: limit.targetId, title: `Histórico do limite · ${limit.categoryName}` })}>Histórico</button>
            <button type="button" onClick={() => { if (window.confirm(`Remover o limite de ${limit.categoryName}? O histórico será preservado.`)) void run(() => removeCategoryLimit(workspaceId, plan.competence, limit.categoryId), 'Limite removido.') }}>Remover</button>
          </td>
        </tr>)}</tbody>
      </table></div>}
    </section>

    <section className="monthly-plan__section" aria-labelledby="plan-lines-title">
      <h5 id="plan-lines-title">Receitas, despesas e transferências planejadas</h5>
      {plan.lines.length === 0 ? <p className="meta">Nenhuma linha neste mês{allAccounts ? '' : ' para a conta selecionada'}.</p> : <div className="monthly-plan__table-wrap"><table className="monthly-plan__table">
        <caption className="sr-only">Linhas do planejamento do mês</caption>
        <thead><tr><th scope="col">Descrição</th><th scope="col">Tipo</th><th scope="col">Conta</th><th scope="col">Categoria</th><th scope="col">Original</th><th scope="col">Vigente</th><th scope="col">Data esperada</th><th scope="col">Ações</th></tr></thead>
        <tbody>{plan.lines.map(line => <tr key={line.id}>
          <th scope="row" data-label="Descrição">{line.description}{line.hourly && <small> · {line.hourly.billableDays} dia(s) × {line.hourly.hoursPerDay} h × {money(line.hourly.hourlyRate)}/h{line.hourly.exceedsCalendar ? ` (calendário: ${line.hourly.businessDays} dias úteis)` : ''}</small>}</th>
          <td data-label="Tipo">{kindLabels[line.kind]}{line.nature ? ` · ${natureLabels[line.nature]}` : ''}</td>
          <td data-label="Conta">{line.kind === 'Transfer' ? `${line.accountName ?? '—'} → ${line.destinationAccountName ?? '—'}` : line.accountName ? `${line.accountName}${line.accountScope ? ` · ${line.accountScope}` : ''}${line.accountActive ? '' : ' (inativa)'}` : 'Consolidado'}</td>
          <td data-label="Categoria">{line.categoryName ?? '—'}</td>
          <td data-label="Original">{money(line.originalAmount)}</td>
          <td data-label="Vigente">{money(line.amount)}{line.revised && <small> · {line.difference.startsWith('-') ? '' : '+'}{money(line.difference)}</small>}</td>
          <td data-label="Data esperada">{line.expectedDate ? `${line.expectedDate.slice(8, 10)}/${line.expectedDate.slice(5, 7)}/${line.expectedDate.slice(0, 4)}` : '—'}</td>
          <td data-label="Ações" className="monthly-plan__row-actions">
            <button type="button" onClick={() => setPanel({ type: 'line', kind: line.kind, line })}>Revisar</button>
            <button type="button" onClick={() => setPanel({ type: 'history', subjectId: line.id, title: `Histórico · ${line.description}` })}>Histórico</button>
            <button type="button" onClick={() => { if (window.confirm(`Remover “${line.description}” do planejamento? O histórico será preservado.`)) void run(() => removePlanLine(workspaceId, line.id), 'Linha removida.') }}>Remover</button>
          </td>
        </tr>)}</tbody>
      </table></div>}
    </section>

    {plan.execution.coverage.reasons.length > 0 && !plan.execution.coverage.complete && <p className="meta">A realização do mês tem cobertura parcial; veja os detalhes no painel Planejado × Realizado.</p>}
    {plan.planned.incomeSource && plan.execution.income.plannedSource && plan.execution.income.plannedSource !== plan.planned.incomeSource && <p className="meta">Fonte do planejado: {sourceText[plan.execution.income.plannedSource] ?? plan.execution.income.plannedSource}.</p>}
  </div>
}

function ExecutionCard({ title, ariaLabel, indicator, realizedLabel }: { title: string; ariaLabel: string; indicator: PlanIndicator; realizedLabel: string }) {
  const defined = indicator.plannedState === 'Defined'
  return <article className="card plan-card" aria-label={ariaLabel}><header><h5>{title}</h5></header>
    <p className="plan-card__main" aria-label={realizedLabel}><small>{realizedLabel}</small><strong>{money(indicator.realized)}</strong></p>
    <dl className="plan-card__facts">
      <div><dt>Planejado</dt><dd>{defined ? money(indicator.planned) : 'Não definido'}</dd></div>
      <div><dt>Projetado</dt><dd>{money(indicator.projected)}</dd></div>
      <div><dt>Pendente</dt><dd>{money(indicator.pending)}</dd></div>
      {defined && indicator.realizedDifference !== null && <div><dt>Desvio do realizado</dt><dd>{money(indicator.realizedDifference)}</dd></div>}
      {defined && indicator.projectedDifference !== null && <div><dt>Desvio do projetado</dt><dd>{money(indicator.projectedDifference)}</dd></div>}
      {defined && indicator.projectedPercent !== null && <div><dt>Projetado do planejado</dt><dd>{percent(indicator.projectedPercent)}</dd></div>}
    </dl>
    {defined && indicator.plannedSource && <p className="meta">Planejado: {sourceText[indicator.plannedSource] ?? indicator.plannedSource}.</p>}
  </article>
}
