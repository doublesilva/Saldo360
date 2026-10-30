import { useState } from 'react'
import {
  aiReasonText, categorizationStateText, failureLabels, runCategorizationNow, runStateLabels, runTriggerLabels, syncStateText, updateAutomation,
  type AutomationOverview, type AutomationRun, type AutomationUpdate,
} from '../api/pluggyAutomation'
import { toUserMessage } from '../api/apiErrors'
import { aiStopLabels } from '../pluggyFormat'
import { countsText, durationText, whenSp } from '../automationFormat'

const AI_CONSENT = 'Categorizar com a IA, a cada sincronização bem-sucedida, os lançamentos elegíveis que continuarem sem categoria? '
  + 'Serão enviadas ao provedor de IA apenas descrições saneadas (sem CPF/CNPJ, números de conta e cartão, chaves Pix, e-mails, telefones e identificadores; '
  + 'transferências e descrições que parecem nomes de pessoas nunca são enviadas). Valores, datas e contas não são enviados. '
  + 'Você pode desligar quando quiser. O envio só acontece se o agente de IA estiver disponível.'
const AI_ONCE = 'Enviar ao provedor de IA, para sugerir categorias, a descrição saneada dos lançamentos que continuarem sem categoria? '
  + 'Antes do envio são removidos CPF/CNPJ, números de conta e cartão, chaves Pix, e-mails, telefones e identificadores; valores, datas e contas não são enviados. '
  + 'A IA só escolhe entre as suas categorias existentes: não cria categoria nem define o tipo de despesa.'

const stateBadge = (state: AutomationRun['state']) => state === 'Completed' ? 'badge--sync-completed' : state === 'Failed' ? 'badge--sync-failed' : 'badge--sync-running'

/** Resumo de uma execução: origem, início, duração, resultado e, em falha, o motivo em frase (nunca resposta do provedor ou payload). */
export function LastRun({ run, label }: { run: AutomationRun | null; label: string }) {
  if (!run) return <p className="meta">{label}: nenhuma execução registrada ainda.</p>
  const failure = run.failureCode ? (failureLabels[run.failureCode] ?? run.failureCode) : null
  const stopped = run.categorization?.aiStoppedBecause ? (aiStopLabels[run.categorization.aiStoppedBecause] ?? run.categorization.aiStoppedBecause) : null
  return <div className="automation-lastrun" aria-label={label}>
    <p>{label}: <span className={`badge ${stateBadge(run.state)}`}>{runStateLabels[run.state]}</span>{' '}
      <span className="meta">{runTriggerLabels[run.trigger]} · início {whenSp(run.startedAt)} · {run.finishedAt ? `duração ${durationText(run.durationSeconds)}` : 'sem término registrado'}{run.scope ? ` · ${run.scope}` : ''}</span></p>
    <p className="meta">{countsText(run)}</p>
    {failure && <p className="meta" role="status">{failure}</p>}
    {stopped && <p className="meta" role="status">{stopped}</p>}
  </div>
}

/**
 * Os dois cartões da central: sincronização bancária e categorização com IA. Tudo o que é "ligado/pausado", "próxima execução" e "elegíveis" vem da API;
 * o servidor decide se o agendador existe, o usuário só pausa e escolhe o intervalo, e a IA só roda com consentimento explícito.
 */
export function AutomationCards({ workspaceId, overview, onChanged, onOpenConnections, onOpenHistory }: {
  workspaceId: string; overview: AutomationOverview; onChanged: () => void; onOpenConnections: () => void; onOpenHistory: (kind: 'Sync' | 'Categorization') => void
}) {
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const { server, settings, sync, categorization } = overview

  const save = async (name: string, update: AutomationUpdate, done: string) => {
    setBusy(name); setError(null); setMessage(null)
    try { await updateAutomation(workspaceId, update); setMessage(done); onChanged() } catch (cause) { setError(toUserMessage(cause)) } finally { setBusy(null) }
  }
  const categorize = async (useAi: boolean) => {
    if (useAi && !window.confirm(AI_ONCE)) return
    setBusy(useAi ? 'ai' : 'local'); setError(null); setMessage(null)
    try { await runCategorizationNow(workspaceId, useAi); setMessage('Categorização concluída. Veja o resultado no histórico.'); onChanged() } catch (cause) { setError(toUserMessage(cause)); onChanged() } finally { setBusy(null) }
  }
  const toggleAfterSync = () => {
    if (settings.categorizeAfterSync) return void save('after', { categorizeAfterSync: false }, 'Categorização após a sincronização desligada. O consentimento foi removido.')
    if (!window.confirm(AI_CONSENT)) return
    void save('after', { categorizeAfterSync: true, confirmExternal: true }, 'Categorização após a sincronização ligada, com o seu consentimento registrado.')
  }

  const intervalText = settings.syncIntervalIsDefault ? `a cada ${settings.syncIntervalHours} h (padrão do servidor)` : `a cada ${settings.syncIntervalHours} h (escolhido por você)`
  const windowText = server.windowStartHour === 0 && server.windowEndHour === 24 ? 'a qualquer hora' : `entre ${server.windowStartHour}h e ${server.windowEndHour}h`
  const nextText = sync.nextRunAt ? whenSp(sync.nextRunAt)
    : sync.state === 'Paused' ? 'não prevista: as execuções agendadas estão pausadas'
    : sync.state === 'SchedulerOff' ? 'não prevista: o agendamento está desligado no servidor'
    : sync.state === 'NoConnections' ? 'não prevista: não há conexão com conta integrada'
    : sync.state === 'Unavailable' ? 'não prevista: integração indisponível' : 'não prevista'
  const ai = server.ai
  const aiReason = ai.reason ? (aiReasonText[ai.reason] ?? 'O agente de IA não está disponível.') : null
  const syncBlocked = sync.state === 'Unavailable'

  return <div className="automation-cards">
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {message && <p role="status" className="notice">{message}</p>}

    <section className="card automation-card" aria-labelledby="automation-sync-title">
      <h4 id="automation-sync-title">Sincronização bancária</h4>
      <p><span className={`badge ${sync.state === 'Scheduled' ? 'badge--sync-completed' : sync.state === 'Running' ? 'badge--sync-running' : ''}`}>{syncStateText[sync.state]}</span>
        {sync.connectionsNeedingAttention > 0 && <span className="meta"> · {sync.connectionsNeedingAttention} conexão(ões) precisam de atenção</span>}</p>
      <dl className="card-overview__facts">
        <div><dt>Conexões com conta integrada</dt><dd>{sync.connections}</dd></div>
        <div><dt>Frequência</dt><dd>{intervalText}</dd></div>
        <div><dt>Horário</dt><dd>{windowText} ({server.timeZone})</dd></div>
        <div><dt>Próxima execução</dt><dd>{nextText}</dd></div>
      </dl>
      <LastRun run={sync.lastRun} label="Última execução" />
      {sync.running && <p className="meta" role="status">Há uma sincronização em andamento. Uma nova só começa depois dela.</p>}
      {!server.schedulerEnabled && !syncBlocked && <p className="meta">O agendamento automático está desligado neste servidor: a sincronização acontece só quando você usa "Sincronizar agora". Suas preferências abaixo ficam salvas e passam a valer se ele for ligado.</p>}
      <div className="automation-card__controls">
        <label>Intervalo entre sincronizações
          <select value={settings.syncIntervalHours} disabled={busy !== null || syncBlocked} onChange={event => void save('interval', { syncIntervalHours: Number(event.target.value) }, 'Intervalo salvo. Vale a partir da próxima sincronização concluída.')}>
            {server.intervalOptions.map(hours => <option key={hours} value={hours}>{hours} h{hours === server.defaultIntervalHours ? ' (padrão)' : ''}</option>)}
          </select>
        </label>
        <button type="button" disabled={busy !== null || syncBlocked} aria-pressed={settings.syncPaused}
          onClick={() => void save('pause', { syncPaused: !settings.syncPaused }, settings.syncPaused ? 'Execuções agendadas retomadas.' : 'Execuções agendadas pausadas. Uma sincronização que já começou termina normalmente.')}>
          {settings.syncPaused ? 'Retomar execuções agendadas' : 'Pausar execuções agendadas'}
        </button>
        <button type="button" className="button--primary" disabled={syncBlocked || sync.connections === 0} onClick={onOpenConnections}>Sincronizar agora…</button>
        <button type="button" onClick={() => onOpenHistory('Sync')}>Ver histórico</button>
      </div>
      <p className="meta">Pausar impede novas execuções agendadas; não interrompe uma sincronização já iniciada e não impede "Sincronizar agora". A sincronização manual e a agendada usam o mesmo processo, nunca na mesma conexão ao mesmo tempo.</p>
    </section>

    <section className="card automation-card" aria-labelledby="automation-ai-title">
      <h4 id="automation-ai-title">Categorização com IA</h4>
      <p><span className={`badge ${ai.available ? 'badge--sync-completed' : 'badge--sync-failed'}`}>{ai.available ? `Agente disponível${ai.model ? ` (${ai.model})` : ''}` : 'Agente indisponível'}</span>{' '}
        <span className="meta">{categorizationStateText[categorization.state]}</span></p>
      {aiReason && <p className="meta" role="status">{aiReason} A categorização local (regras e categoria do banco) continua funcionando.</p>}
      <dl className="card-overview__facts">
        <div><dt>Lançamentos elegíveis (orçamento inteiro)</dt><dd>{categorization.eligible}</dd></div>
        <div><dt>Sem categoria segura</dt><dd>{categorization.needsReview}</dd></div>
        <div><dt>Ainda não avaliados</dt><dd>{categorization.notEvaluated}</dd></div>
        <div><dt>Após cada sincronização</dt><dd>{settings.categorizeAfterSync ? `Ligada${settings.categorizeConsentAt ? ` (consentimento em ${whenSp(settings.categorizeConsentAt)})` : ''}` : 'Desligada'}</dd></div>
      </dl>
      <LastRun run={categorization.lastRun} label="Última execução" />
      <div className="automation-card__controls">
        <button type="button" disabled={busy !== null || categorization.running} onClick={() => void categorize(false)}>Categorizar sem IA</button>
        <button type="button" disabled={busy !== null || categorization.running || !ai.available} onClick={() => void categorize(true)}>Categorizar com IA…</button>
        <label className="automation-card__check"><input type="checkbox" checked={settings.categorizeAfterSync} disabled={busy !== null || (!ai.available && !settings.categorizeAfterSync)} onChange={toggleAfterSync} />
          Categorizar com IA após cada sincronização bem-sucedida</label>
        <button type="button" onClick={() => onOpenHistory('Categorization')}>Ver histórico</button>
      </div>
      <ul className="meta automation-card__rules">
        <li>Sua escolha manual de categoria e de tipo de despesa nunca é substituída.</li>
        <li>A categoria do banco só é usada quando corresponde a uma das suas categorias; a IA escolhe apenas entre as suas categorias existentes e não cria categoria.</li>
        <li>A IA não define o tipo de despesa (fixa ou variável): isso continua com você.</li>
        <li>Se a IA não for conclusiva, o lançamento fica pendente para sua revisão. Cada execução tem limite de itens e custo: o que sobrar fica pendente e uma nova execução continua.</li>
        <li>Uma falha da IA não é falha da conexão e não desfaz a sincronização.</li>
      </ul>
    </section>
  </div>
}
