import type { PlanActualDashboard } from '../../api/planActual'
import { formatDate, isNegativeDecimal, subtractMoneyDecimal } from '../../money'
import type { BlockLoad, Go } from './Overview'
import { isPositiveAmount, money } from './overviewText'

export function OverviewOverdraftAlert({ plan, onNavigate }: { plan: BlockLoad<PlanActualDashboard>; onNavigate: Go }) {
  const data = plan.data
  if (plan.error) return <section className="notice notice--warning" aria-label="Cheque especial">
    Posição do cheque especial indisponível. <button type="button" onClick={plan.retry}>Tentar novamente</button>
  </section>
  if (!data?.overdraft.eligible || data.periodKind === 'Past') return null

  const accounts = data.overdraft.accounts.filter(account => isPositiveAmount(account.contractedLimit))
  const stale = data.sync?.stale === true
  const alerts = accounts.flatMap(account => {
    const observed = account.observedAt && account.observedAsOf && account.state !== 'Unavailable'
    const used = observed && isPositiveAmount(account.usedLatest)
    const projected = isPositiveAmount(account.projectedUsedAtEnd)
    if (data.periodKind === 'Future' && !projected) return []
    if (data.periodKind === 'Current' && !used && !projected && observed && !stale) return []
    return [{ account, observed, used, projected }]
  })
  if (!alerts.length) return null

  return <section className="ov-card ov-overdraft-alert" aria-label="Situação do cheque especial">
    <h3>Cheque especial</h3>
    {alerts.map(({ account, observed, used, projected }) => {
      const available = observed && account.contractedLimit ? subtractMoneyDecimal(account.contractedLimit, account.usedLatest) : null
      const current = data.periodKind === 'Current'
      return <div key={account.accountId} className="ov-overdraft-alert__account">
        <strong>{account.name}</strong>
        <span className={current && used && !stale ? 'ov-tag ov-tag--danger' : 'ov-tag ov-tag--warning'}>
          {!current ? 'Risco projetado' : !observed ? 'Posição indisponível' : stale ? 'Última posição · sincronização desatualizada' : used ? 'Limite em uso' : 'Risco projetado'}
        </span>
        {current && observed && account.observedAsOf && <p className="ov-meta">
          Utilizado {money(account.usedLatest)} · Limite disponível {money(available && isNegativeDecimal(available) ? '0.00' : available)} · Posição bancária de {formatDate(account.observedAsOf)}
        </p>}
        {projected && <p className="ov-meta">Uso projetado no fechamento {money(account.projectedUsedAtEnd)}</p>}
        <button type="button" className="ov-link" onClick={() => onNavigate('planning', {
          planningTab: 'six', competence: data.competence, recoveryScope: { kind: 'account', accountId: account.accountId },
        })}>Ver recuperação de {account.name}</button>
      </div>
    })}
  </section>
}
