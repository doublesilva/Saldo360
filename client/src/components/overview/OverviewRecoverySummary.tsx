import type { DecisionRecovery } from '../../api/overview'
import type { OverviewNavigation, OverviewSection } from './Overview'
import { recoveryNotApplicable, recoveryStateText } from './decisionText'
import { dateTime, isNegative, money } from './overviewText'

// T11.1 / T12.2: "Minha recuperação financeira". Resumo compacto da meta do mês: o servidor decide o estado (provisória, fixada, ausente) e calcula todos os valores.
// O registro da meta e a consulta ao histórico ficam na recuperação detalhada.

type Go = (section: OverviewSection, context?: OverviewNavigation) => void

const targetMessage: Record<DecisionRecovery['state'], string | null> = {
  Provisional: 'Calculada com os dados de hoje e ainda pode mudar até ser fixada.',
  Fixed: null,
  Pending: 'A meta ainda não pôde ser calculada com os dados disponíveis.',
  NotRegistered: 'A meta deste mês não foi registrada, e nenhuma meta é inventada.',
  Unavailable: 'A recuperação não pôde ser apurada com os dados disponíveis.',
  NotApplicable: null,
}

export function OverviewRecoverySummary({ recovery, onOpenDetail, onNavigate }: { recovery: DecisionRecovery; onOpenDetail: () => void; onNavigate: Go }) {
  const applicable = recovery.state !== 'NotApplicable'
  const hasTarget = recovery.state === 'Provisional' || recovery.state === 'Fixed'
  return <section className="ov-recoverysum" aria-labelledby="ov-recoverysum-title">
    <header className="ov-area__head"><h2 id="ov-recoverysum-title">Minha recuperação financeira</h2></header>
    <article className="ov-card" aria-label="Meta de recuperação">
      {!applicable
        ? <p className="ov-meta" role="status">{recoveryNotApplicable[recovery.reason ?? ''] ?? 'Sem meta de recuperação nesta competência.'}</p>
        : <>
          <p className="ov-tags"><span className={`ov-tag ov-tag--${recovery.state === 'Fixed' ? 'ok' : 'warning'}`}>{recoveryStateText[recovery.state]}</span></p>
          {targetMessage[recovery.state] && <p className="ov-meta" role="status">{targetMessage[recovery.state]}</p>}
          {recovery.state === 'Fixed' && recovery.targetFixedAt && <p className="ov-meta">{recovery.legacyTarget ? 'Meta histórica registrada em ' : 'Fixada em '}{dateTime(recovery.targetFixedAt)}. Não é recalculada.</p>}
          <dl className="ov-recoverysum__grid">
            <div><dt>Cheque especial utilizado</dt><dd data-unavailable={recovery.openingUsed === null || undefined}>{money(recovery.openingUsed)}<small>na abertura do mês</small></dd></div>
            <div><dt>Meta de recuperação</dt><dd data-unavailable={!hasTarget || undefined}>{hasTarget ? money(recovery.target) : 'Meta não configurada'}</dd></div>
            <div><dt>Recuperação realizada</dt><dd>{money(recovery.effectiveRecovery)}<small>até hoje</small></dd></div>
            <div><dt>Recuperação projetada</dt><dd data-unavailable={recovery.projectedRecovery === null || undefined}>{recovery.projectedRecovery === null ? 'Ainda não é possível calcular' : money(recovery.projectedRecovery)}</dd></div>
            <div><dt>Desvio em relação à meta</dt><dd className={isNegative(recovery.gapToTarget) ? 'ov-negative' : undefined} data-unavailable={recovery.gapToTarget === null || undefined}>
              {recovery.gapToTarget === null ? 'Ainda não é possível calcular' : money(recovery.gapToTarget)}
              {recovery.gapToTarget !== null && <small>{isNegative(recovery.gapToTarget) ? 'abaixo da meta' : 'dentro da meta'}</small>}</dd></div>
          </dl>
        </>}
      <div className="form__actions">
        <button type="button" onClick={() => onNavigate('planning', { planningTab: 'projection' })}>Ver projeção de 6, 13, 24 ou 36 meses</button>
        {applicable && <button type="button" onClick={onOpenDetail}>Registrar meta e ver histórico</button>}
      </div>
    </article>
  </section>
}
