import { useState } from 'react'
import type { PlanActualDashboard } from '../../api/planActual'
import type { OverviewClassification } from '../../api/overview'
import { syncConnection } from '../../api/pluggyPoc'
import { toUserMessage } from '../../api/apiErrors'
import { formatDate } from '../../money'
import type { OverviewNavigation } from './Overview'
import { dateTime, explain, isNegative, money, monthTitle, periodLabels } from './overviewText'

type Target = 'accounts' | 'transactions' | 'automation'

/**
 * Os quatro indicadores principais, e só eles (R01). Fonte única: `plan-actual`, no escopo e na competência escolhidos.
 * Saldo atual e fechamento têm a sua própria referência de tempo; receitas e despesas acompanham a competência.
 */
export function OverviewIndicators({ data }: { data: PlanActualDashboard }) {
  const situation = data.situation
  const partialBalance = situation.realizedBalance === null && situation.partialRealizedBalance !== null && situation.consideredAccounts > 0
  const balance = situation.realizedBalance ?? (partialBalance ? situation.partialRealizedBalance : null)
  const bankUpdate = dateTime(situation.latestBankUpdate)
  const consumption = data.expenses.consumption
  const past = data.periodKind === 'Past'
  const closingReason = explain(situation.reasons)[0] ?? 'Faltam saldo inicial ou histórico para calcular.'

  return <section className="ov-indicators" aria-label="Indicadores do mês">
    <Indicator title="Saldo atual" value={balance} negative={isNegative(balance)} tag={partialBalance ? 'Parcial' : null}
      note={balance === null ? 'Saldo indisponível: configure o saldo inicial das contas.' : partialBalance ? `Soma de ${situation.consideredAccounts} conta(s); ${situation.unavailableAccounts} sem saldo calculável.` : null}
      reference={`Posição em ${formatDate(data.asOf)}${bankUpdate ? ` · banco atualizado em ${bankUpdate}` : ''}`} />
    <Indicator title="Receitas do mês" value={data.income.realized} tone="income"
      note={data.income.pending !== '0.00' ? `Previsto a receber: ${money(data.income.pending)}` : null} reference={`Realizadas em ${monthTitle(data.competence)}`} />
    <Indicator title="Despesas do mês" value={consumption.realized} tone="expense"
      note={consumption.pending !== '0.00' ? `Previsto a pagar: ${money(consumption.pending)}` : null} reference={`Incorridas em ${monthTitle(data.competence)} · cartão pelo mês da fatura`} />
    <Indicator title={past ? 'Saldo no fechamento' : 'Fechamento projetado'} value={situation.projectedClosing} negative={isNegative(situation.projectedClosing)}
      note={situation.projectedClosing === null ? closingReason : null}
      reference={`${periodLabels[data.periodKind]}${past ? ' · valor realizado no último dia' : data.periodKind === 'Current' ? ' · estimativa para o último dia' : ' · considera só compromissos conhecidos'}`} />
  </section>
}

function Indicator({ title, value, negative = false, tone, note, reference, tag }: { title: string; value: string | null; negative?: boolean; tone?: 'income' | 'expense'; note?: string | null; reference: string; tag?: string | null }) {
  return <article className={`ov-card ov-indicator${tone ? ` ov-indicator--${tone}` : ''}${negative ? ' ov-indicator--negative' : ''}`} aria-label={title}>
    <h3>{title}</h3>
    <p className="ov-indicator__value" data-unavailable={value === null || undefined}>{money(value)}</p>
    {(negative || tag) && <p className="ov-tags">{negative && <span className="ov-tag ov-tag--danger">Saldo negativo</span>}{tag && <span className="ov-tag">{tag}</span>}</p>}
    {note && <p className="ov-indicator__note">{note}</p>}
    <p className="ov-indicator__reference">{reference}</p>
  </article>
}

/**
 * Avisos que mudam a leitura dos valores, em linguagem direta; a explicação completa fica no detalhe expansível. A fila de classificação é um aviso
 * próprio e nunca é apresentada como falha de sincronização.
 */
export function OverviewNotices({ workspaceId, data, classification, onNavigate, onSynced }: {
  workspaceId: string; data: PlanActualDashboard; classification: OverviewClassification | null; onNavigate: (target: Target, context?: OverviewNavigation) => void; onSynced: () => void
}) {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const sync = data.sync
  const reasons = Array.from(new Set([...data.situation.reasons, ...data.coverage.reasons, ...data.income.reasons, ...data.expenses.consumption.reasons]))
  const details = explain(reasons)
  const pending = classification && classification.state === 'Complete' ? classification.needsReview + classification.notEvaluated : 0
  const syncProblem = sync !== null && (sync.stale || ['Failed', 'ConsentExpired', 'CompletedWithErrors'].includes(sync.state))
  const run = async (connectionId: string) => {
    setBusy(connectionId); setError(null)
    try { await syncConnection(workspaceId, connectionId) } catch (failure: unknown) { setError(toUserMessage(failure)) } finally { setBusy(null); onSynced() }
  }
  const notices: { key: string; tone: 'warning' | 'info'; content: React.ReactNode }[] = []
  if (data.scope.primaryMissing) notices.push({ key: 'primary', tone: 'info', content: <>Nenhuma conta principal definida: mostrando todas as contas. <button type="button" onClick={() => onNavigate('accounts')}>Definir em Contas</button></> })
  if (!data.snapshotConsistent) notices.push({ key: 'snapshot', tone: 'warning', content: 'Os dados mudaram durante a leitura. Atualize para ver um retrato consistente.' })
  if (syncProblem) notices.push({ key: 'sync', tone: 'warning', content: <>{sync?.state === 'ConsentExpired' ? 'A autorização do banco expirou.' : sync?.state === 'Failed' ? 'A última sincronização com o banco falhou.' : 'Os dados do banco podem estar desatualizados.'} <button type="button" onClick={() => onNavigate('automation', { automationTab: 'connections' })}>Ver conexões</button></> })
  if (data.coverage.reviewAffectsConfidence) notices.push({ key: 'review', tone: 'warning', content: <>{data.coverage.reviewCount} movimentação(ões) do banco aguardam revisão e ainda não entram nos totais. <button type="button" onClick={() => onNavigate('accounts')}>Revisar em Contas</button></> })
  if (pending > 0) notices.push({ key: 'classification', tone: 'info', content: <>Há {pending} lançamento(s) pendentes de classificação (todos os importados do banco, não só deste mês). <button type="button" onClick={() => onNavigate('automation', { automationTab: 'automations', review: { kind: 'category' } })}>Revisar classificação</button></> })
  if (!data.coverage.complete || data.situation.state !== 'Complete') notices.push({ key: 'coverage', tone: 'warning', content: 'Projeção incompleta: parte dos dados ainda não pode ser calculada.' })

  return <section className="ov-notices" aria-label="Avisos">
    {sync && <p className="ov-bank">{sync.latestSuccessAt ? `Última sincronização com o banco: ${dateTime(sync.latestSuccessAt)}.` : 'Ainda sem sincronização bem-sucedida com o banco.'}{' '}
      {sync.connections.map(connection => <button key={connection.connectionId} type="button" disabled={busy !== null || connection.status === 'Running'} onClick={() => void run(connection.connectionId)}>
        {busy === connection.connectionId ? 'Sincronizando…' : sync.connections.length > 1 ? `Sincronizar · ${connection.label ?? 'conexão'}` : 'Sincronizar agora'}</button>)}</p>}
    {error && <p className="field-error" role="alert">{error}</p>}
    {notices.map(notice => <p key={notice.key} className={`ov-notice ov-notice--${notice.tone}`} role="status">{notice.content}</p>)}
    {details.length > 0 && <details className="ov-details"><summary>Por que alguns valores são parciais</summary><ul>{details.map(text => <li key={text}>{text}</li>)}</ul></details>}
  </section>
}
