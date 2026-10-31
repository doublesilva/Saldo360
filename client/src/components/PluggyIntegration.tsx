import { useCallback, useEffect, useState } from 'react'
import {
  applyCardScheduleSuggestion, applyOpeningBalanceSuggestion, confirmTransfer, getCategorizationOverview, getLinkDetail, listLinks, listReview, previewRecovery, rejectTransfer,
  reprocessImported, resolveReview, runCategorization,
  type AccountLink, type AccountLinkDetail, type CategorizationOverview, type CategorizationResult, type RecoveryPreview, type ReviewPage, type Resolution,
} from '../api/pluggyIntegration'
import { formatBrlDecimal, formatDate, isNegativeDecimal } from '../money'
import { aiStopLabels, billCycleLabels, billPaymentLabels, billReconciliationLabels, billReconciliationReasonLabels, candidateBlockLabels, dateTime, recoveryEffectLabels, scheduleReasonLabels, linkStatusLabels, money, NOT_INFORMED, resolutionLabels, reviewReason, unavailableReasonLabels } from '../pluggyFormat'

const errorText = (error: unknown) => (error instanceof Error ? error.message : 'Falha inesperada.')

/**
 * Integração bancária de uma conta: origem, instituição, situação, última sincronização e os saldos SEPARADOS — o informado pelo banco, o calculado
 * pelo Saldo360 e a diferença. Todos os números vêm prontos da API; aqui só são formatados. Sem base consistente, mostra indisponibilidade.
 */
export function PluggyAccountPanel({ workspaceId, linkId, refreshKey, onChanged }: { workspaceId: string; linkId: string; refreshKey: string; onChanged?: () => void }) {
  const [detail, setDetail] = useState<AccountLinkDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    getLinkDetail(workspaceId, linkId, controller.signal).then(result => { setDetail(result); setError(null) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) setError(errorText(cause)) })
    return () => controller.abort()
  }, [workspaceId, linkId, refreshKey, version])

  if (error) return <p role="alert" className="notice notice--error">{error}</p>
  if (!detail) return <p className="meta">Carregando integração…</p>

  const { link, reconciliation: r } = detail
  const apply = async () => {
    // Conta que já tem saldo inicial só o troca com confirmação específica.
    const replace = r.calculatedUnavailableReason !== 'OpeningBalanceNotConfigured'
    const question = replace ? 'A conta já tem saldo inicial. Substituir pelo sugerido?' : 'Aplicar o saldo inicial sugerido? Ele passa a ser o saldo inicial da conta, na data indicada.'
    if (!window.confirm(question)) return
    setBusy(true); setError(null)
    try { setDetail(await applyOpeningBalanceSuggestion(workspaceId, linkId, replace)); onChanged?.() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }
  const applySchedule = async () => {
    if (!window.confirm('Alterar o fechamento e o vencimento habituais do cartão para os dias sugeridos pelo histórico de faturas do banco? As faturas já importadas continuam como o banco informou.')) return
    setBusy(true); setError(null)
    try { setDetail(await applyCardScheduleSuggestion(workspaceId, linkId)); onChanged?.() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }
  const reprocess = async () => {
    setBusy(true); setError(null)
    try { await reprocessImported(workspaceId); setVersion(value => value + 1); onChanged?.() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }

  return <section className="account__integration" aria-label={`Integração ${link.displayName}`}>
    <p><span className="badge badge--realization">Pluggy</span> {link.displayName}</p>
    <dl className="account__details">
      <div><dt>Situação da integração</dt><dd>{linkStatusLabels[link.status] ?? link.status}</dd></div>
      <div><dt>Última sincronização</dt><dd>{dateTime(link.lastSyncedAt)}</dd></div>
      <div><dt>Início da importação</dt><dd>{link.importFrom ? formatDate(link.importFrom) : 'Todo o histórico'}</dd></div>
    </dl>
    <p>Saldo informado pelo banco{r.observedAt ? ` (em ${dateTime(r.observedAt)})` : ''}: <strong>{money(r.observedBalance)}</strong></p>
    {link.observed.overdraftContractedLimit !== null && <p className="meta">Limite de cheque especial informado pelo banco: {money(link.observed.overdraftContractedLimit)} — não compõe o saldo.</p>}
    {link.kind === 'Bank' && <>
      <p>Saldo calculado pelo Saldo360{r.calculatedAsOf ? ` até ${formatDate(r.calculatedAsOf)}` : ''}: {r.calculatedBalance !== null
        ? <strong className={isNegativeDecimal(r.calculatedBalance) ? 'amount amount--negative' : 'amount'}>{formatBrlDecimal(r.calculatedBalance)}</strong>
        : <span className="meta">indisponível ({unavailableReasonLabels[r.calculatedUnavailableReason ?? ''] ?? NOT_INFORMED})</span>}</p>
      {r.difference !== null && <p>Diferença (banco − Saldo360): <strong>{money(r.difference)}</strong>{r.difference === '0.00' ? ' — confere.' : ' — movimentos em revisão, pendentes ou lançados à mão podem explicá-la.'}</p>}
      <p className="meta">Movimentos: {link.postings.applied} contabilizados · {link.postings.waiting} pendentes ou futuros · {link.postings.needsReview} em revisão.</p>
      {r.suggestedOpeningBalance !== null && r.suggestedOpeningBalanceDate !== null
        ? <p>Saldo inicial sugerido: <strong>{money(r.suggestedOpeningBalance)}</strong> em {formatDate(r.suggestedOpeningBalanceDate)} <button type="button" onClick={() => void apply()} disabled={busy}>Aplicar saldo inicial sugerido</button></p>
        : <p className="meta">Sugestão de saldo inicial indisponível: {unavailableReasonLabels[r.suggestionUnavailableReason ?? ''] ?? NOT_INFORMED}.</p>}
    </>}
    {link.kind === 'Card' && r.card && <>
      <p>Limite do cartão: {money(r.card.creditLimit)} · usado (Saldo360): {money(r.card.usedLimit)} · disponível (Saldo360): {money(r.card.availableLimit)}</p>
      <p className="meta">Informado pelo banco: saldo devedor {money(r.card.observedBalanceDue)} · limite disponível {money(r.card.observedAvailableLimit)}. Cartão não soma saldo de caixa.</p>
      <p className="meta" aria-label="Fechamento e vencimento habituais">
        Fechamento habitual cadastrado: {r.card.currentClosingDay ?? NOT_INFORMED} · vencimento habitual: {r.card.currentDueDay ?? NOT_INFORMED}.
        {r.card.suggestedClosingDay != null || r.card.suggestedDueDay != null
          ? <> O histórico de faturas do banco sugere {r.card.suggestedClosingDay != null ? `fechamento no dia ${r.card.suggestedClosingDay}` : ''}{r.card.suggestedClosingDay != null && r.card.suggestedDueDay != null ? ' e ' : ''}{r.card.suggestedDueDay != null ? `vencimento no dia ${r.card.suggestedDueDay}` : ''}. <button type="button" onClick={() => void applySchedule()} disabled={busy}>Aplicar fechamento e vencimento sugeridos</button></>
          : r.card.closingScheduleReason || r.card.dueScheduleReason
            ? <> Sem sugestão do histórico do banco ({scheduleReasonLabels[r.card.closingScheduleReason ?? r.card.dueScheduleReason ?? ''] ?? 'indisponível'}).</>
            : null}
      </p>
      {r.card.bills.length > 0 && <ul aria-label="Faturas do banco">
        {r.card.bills.map(bill => <li key={`${bill.closingDate}-${bill.dueDate}`}>
          Fatura com fechamento em {bill.closingDate ? formatDate(bill.closingDate) : NOT_INFORMED}{bill.dueDate ? ` e vencimento em ${formatDate(bill.dueDate)}` : ''}:
          {' '}{billCycleLabels[bill.cycle] ?? bill.cycle} · {billPaymentLabels[bill.payment] ?? bill.payment} · {billReconciliationLabels[bill.reconciliation] ?? bill.reconciliation}.
          {' '}Banco {money(bill.bankTotal)} · importado {money(bill.saldo360Total)}{bill.difference && bill.difference !== '0.00' ? ` · diferença ${money(bill.difference)}` : ''}
          {bill.paidAmount && bill.paidAmount !== '0.00' ? ` · pago (registrado) ${money(bill.paidAmount)}` : ''}
          {bill.reconciliationReason && bill.reconciliation !== 'Reconciled' ? ` (${billReconciliationReasonLabels[bill.reconciliationReason] ?? bill.reconciliationReason})` : ''}
        </li>)}
      </ul>}
    </>}
    {r.notes.map(note => <p key={note} className="meta">{note}</p>)}
    <div className="form__actions"><button type="button" onClick={() => void reprocess()} disabled={busy}>Reprocessar importados</button></div>
  </section>
}

/** Seção "Integração bancária": cartões integrados e a fila de revisão. Contas bancárias aparecem na própria lista de contas. */
export function PluggyIntegrationSection({ workspaceId, refreshKey, onChanged }: { workspaceId: string; refreshKey: string; onChanged?: () => void }) {
  const [links, setLinks] = useState<AccountLink[] | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    listLinks(workspaceId, controller.signal).then(setLinks).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId, refreshKey])
  if (!links || links.length === 0) return null
  const cards = links.filter(link => link.kind === 'Card')
  return <section className="card" aria-labelledby="pluggy-integration-title">
    <h3 id="pluggy-integration-title">Integração bancária (Pluggy)</h3>
    <p className="meta">A sincronização, a categorização com IA e o histórico das execuções ficam em <strong>Automação</strong>.</p>
    {cards.length > 0 && <>
      <h4>Cartões integrados</h4>
      <ul className="account-list">{cards.map(card => <li key={card.id} className="account"><PluggyAccountPanel workspaceId={workspaceId} linkId={card.id} refreshKey={refreshKey} onChanged={onChanged} /></li>)}</ul>
    </>}
    <PluggyReviewPanel workspaceId={workspaceId} refreshKey={refreshKey} onChanged={onChanged} />
    <PluggyRecoveryPanel workspaceId={workspaceId} refreshKey={refreshKey} onChanged={onChanged} />
  </section>
}

const AI_CONFIRMATION = 'Enviar à OpenAI, para sugerir categorias, a descrição saneada dos lançamentos que continuarem sem categoria? '
  + 'Antes do envio são removidos CPF/CNPJ, números de conta e cartão, chaves Pix, e-mails, telefones e identificadores; '
  + 'transferências e descrições que parecem nomes de pessoas nunca são enviadas. Valores, datas e contas não são enviados. '
  + 'A IA relaciona cada lançamento a uma das suas categorias existentes ou sugere uma nova categoria genérica, que só é criada depois de validada e nunca duplica uma equivalente.'

/**
 * Categorização automática dos lançamentos importados: a etapa local (regras do domínio e categoria da Pluggy) não envia nada; a IA é o último
 * recurso, só roda com a sua confirmação explícita e pode estar indisponível sem afetar o resto. A categoria que você escolhe nunca é substituída.
 */
export function PluggyCategorizationPanel({ workspaceId, refreshKey, onChanged }: { workspaceId: string; refreshKey: string; onChanged?: () => void }) {
  const [overview, setOverview] = useState<CategorizationOverview | null>(null)
  const [result, setResult] = useState<CategorizationResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [version, setVersion] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    getCategorizationOverview(workspaceId, controller.signal).then(setOverview).catch(() => undefined)
    return () => controller.abort()
  }, [workspaceId, refreshKey, version])

  const run = useCallback(async (useAi: boolean) => {
    if (useAi && !window.confirm(AI_CONFIRMATION)) return
    setBusy(true); setError(null)
    try {
      setResult(await runCategorization(workspaceId, { useAi, confirmExternal: useAi }))
      setVersion(value => value + 1); onChanged?.()
    } catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }, [workspaceId, onChanged])

  if (!overview) return null
  const { counts, ai } = overview
  return <section aria-labelledby="pluggy-categorization-title">
    <h4 id="pluggy-categorization-title">Categorização dos lançamentos importados</h4>
    <p className="meta">
      Ordem: sua escolha → regras do domínio (pagamento de fatura e transferências não são consumo) → categoria informada pelo banco → IA, somente para o que sobrar.
    </p>
    <ul className="meta" aria-label="Lançamentos por origem da categoria">
      <li>Pela Pluggy: {counts.pluggy}</li>
      <li>Por regras (não são consumo): {counts.rule}</li>
      <li>Pela IA: {counts.ai}</li>
      <li>Escolhidas por você: {counts.manual}</li>
      <li>Sem categoria segura (precisam de revisão): {counts.needsReview}</li>
      <li>Ainda não avaliados: {counts.notEvaluated}</li>
    </ul>
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    <div className="form__actions">
      <button type="button" disabled={busy} onClick={() => void run(false)}>Categorizar automaticamente</button>
      <button type="button" disabled={busy || !ai.available} onClick={() => void run(true)}>Categorizar também com IA…</button>
    </div>
    {!ai.available && <p className="meta">A classificação por IA não está disponível neste ambiente. A categorização local continua funcionando.</p>}
    {result && <div role="status" className="notice">
      <p>
        Categorizados agora: {result.pluggy} pela Pluggy, {result.rule} por regras, {result.ai} pela IA. Sem categoria segura: {result.unresolved}.
        {result.categoriesCreated > 0 ? ` Categorias criadas: ${result.categoriesCreated}.` : ''}
        {result.skippedChanged > 0 ? ` ${result.skippedChanged} alterado(s) por você durante a execução foram preservados.` : ''}
      </p>
      {result.aiCalls > 0 && <p className="meta">IA ({result.model}): {result.aiItemsSent} lançamento(s) enviados, {result.inputTokens} tokens de entrada e {result.outputTokens} de saída, custo estimado US$ {result.estimatedCostUsd.toFixed(4)}.</p>}
      {(result.aiStoppedBecause || result.aiFailure) && <p className="meta">{aiStopLabels[result.aiStoppedBecause ?? 'ProviderFailure'] ?? 'A execução da IA foi interrompida.'}</p>}
    </div>}
  </section>
}

/**
 * Revisão dos movimentos importados. Três coisas diferentes, nunca misturadas: a categoria pendente de um lançamento já contabilizado (não está aqui),
 * a evidência financeira ainda NÃO contabilizada (fora dos totais até a decisão) e o vínculo entre movimentos (transferência), que pode envolver pontas
 * já contabilizadas (continuam nos totais até a decisão). Cada item informa se já conta nos totais.
 */
export function PluggyReviewPanel({ workspaceId, refreshKey, onChanged }: { workspaceId: string; refreshKey: string; onChanged?: () => void }) {
  const [page, setPage] = useState<ReviewPage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [version, setVersion] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    listReview(workspaceId, 1, controller.signal).then(result => { setPage(result); setError(null) })
      .catch((cause: unknown) => { if (!controller.signal.aborted) setError(errorText(cause)) })
    return () => controller.abort()
  }, [workspaceId, refreshKey, version])

  const decide = useCallback(async (postingId: string, resolution: Resolution) => {
    if (resolution === 'Ignore' && !window.confirm('Não contabilizar este movimento? Ele continua guardado, mas não entra em receitas, despesas nem saldos.')) return
    setBusy(postingId); setError(null)
    try { await resolveReview(workspaceId, postingId, resolution); setVersion(value => value + 1); onChanged?.() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(null) }
  }, [workspaceId, onChanged])

  const confirm = useCallback(async (postingId: string, counterpartId: string) => {
    if (!window.confirm('Confirmar que estes dois movimentos são a mesma transferência entre as suas contas? Os lançamentos de receita e despesa dessas pontas, se existirem, são substituídos pela transferência (nada é apagado).')) return
    setBusy(postingId); setError(null)
    try { await confirmTransfer(workspaceId, postingId, counterpartId); setVersion(value => value + 1); onChanged?.() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(null) }
  }, [workspaceId, onChanged])

  const reject = useCallback(async (postingId: string, counterpartId: string) => {
    if (!window.confirm('Estes movimentos não são a mesma transferência? Cada um segue como receita ou despesa comum e a sugestão não volta nas próximas sincronizações.')) return
    setBusy(postingId); setError(null)
    try { await rejectTransfer(workspaceId, postingId, counterpartId); setVersion(value => value + 1); onChanged?.() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(null) }
  }, [workspaceId, onChanged])

  return <section aria-labelledby="pluggy-review-title">
    <h4 id="pluggy-review-title">Movimentos para revisar{page ? ` (${page.total})` : ''}</h4>
    <p className="meta">Movimentos que o banco informou, mas sem classificação ou vínculo seguro. Os <strong>ainda não contabilizados</strong> ficam fora de receitas, despesas e saldos até você decidir; os <strong>já contabilizados</strong> continuam nos totais. Transferências e pagamentos de fatura nunca são identificados só pela descrição.</p>
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {page && page.items.length === 0 && <p className="meta">Nada para revisar.</p>}
    {page && page.items.length > 0 && <ul className="transaction-list">
      {page.items.map(item => <li key={item.postingId} className="transaction">
        <div className="transaction__main"><span className="transaction__description">{item.description}</span>
          <strong className={item.direction === 'Income' ? 'amount amount--income' : 'amount amount--negative'}>{item.direction === 'Income' ? '+' : '−'}{money(item.amount)}</strong></div>
        <div className="transaction__meta meta"><span>{item.date ? formatDate(item.date) : NOT_INFORMED}</span><span>{item.linkDisplayName}</span><span>{reviewReason(item.reason)}</span></div>
        {item.accounted !== undefined && <p className="meta" data-testid="review-impact">
          {item.accounted ? <strong>Já contabilizado — continua nos totais até a decisão.</strong> : <strong>Ainda não contabilizado — fora dos totais até a decisão.</strong>}
          {' '}{item.reviewKind === 'Link' ? 'Revisão do vínculo entre movimentos.' : 'Revisão financeira da evidência.'}
        </p>}
        {item.candidates && item.candidates.length > 0 && <ul aria-label="Possíveis contrapartes" className="meta">
          {item.candidates.map(candidate => <li key={candidate.postingId}>
            {candidate.linkDisplayName}: {candidate.description} — {candidate.direction === 'Income' ? '+' : '−'}{money(candidate.amount)} em {formatDate(candidate.date)}
            {candidate.accounted ? ' (já contabilizado)' : ' (ainda não contabilizado)'}
            {' '}{candidate.canConfirm
              ? <button type="button" disabled={busy === item.postingId} onClick={() => void confirm(item.postingId, candidate.postingId)}>Confirmar transferência</button>
              : <span>— não pode ser confirmada: {candidateBlockLabels[candidate.blockedReason ?? ''] ?? candidate.blockedReason}.</span>}
            {' '}<button type="button" disabled={busy === item.postingId} onClick={() => void reject(item.postingId, candidate.postingId)}>Não é transferência</button>
          </li>)}
        </ul>}
        {item.reviewKind === 'Link' && (!item.candidates || item.candidates.length === 0) && <p className="meta">Nenhuma contraparte elegível encontrada nas contas integradas.</p>}
        {item.allowedResolutions.length > 0 && <div className="form__actions">
          {item.allowedResolutions.map(resolution => <button key={resolution} type="button" disabled={busy === item.postingId} onClick={() => void decide(item.postingId, resolution)}>{resolutionLabels[resolution]}</button>)}
        </div>}
      </li>)}
    </ul>}
  </section>
}

/**
 * Recuperação dos dados existentes: a prévia roda o motor atual numa transação que é sempre desfeita (nada é gravado) e mostra o que mudaria em receitas,
 * despesas, transferências e saldo por conta, mais os casos que exigem decisão sua. Aplicar usa o reprocessamento idempotente, sem nova consulta ao banco.
 */
export function PluggyRecoveryPanel({ workspaceId, onChanged }: { workspaceId: string; refreshKey?: string; onChanged?: () => void }) {
  const [preview, setPreview] = useState<RecoveryPreview | null>(null)
  const [amount, setAmount] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [applied, setApplied] = useState(false)
  const [busy, setBusy] = useState(false)

  const generate = async () => {
    setBusy(true); setError(null); setApplied(false)
    try { setPreview(await previewRecovery(workspaceId, { amount: amount.trim() || undefined })) }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }
  const apply = async () => {
    if (!window.confirm('Aplicar o reprocessamento? Ele reaplica os dados já importados (sem consultar o banco), preserva suas decisões manuais e não apaga nenhuma evidência.')) return
    setBusy(true); setError(null)
    try { await reprocessImported(workspaceId); setApplied(true); setPreview(null); onChanged?.() }
    catch (cause) { setError(errorText(cause)) }
    finally { setBusy(false) }
  }
  const figures = (label: string, f: RecoveryPreview['totalBefore']) =>
    <li>{label}: receitas {money(f.income)} · despesas {money(f.expense)} · transferências recebidas {money(f.transfersIn)} · enviadas {money(f.transfersOut)} · efeito no saldo {money(f.net)}</li>

  return <section aria-labelledby="pluggy-recovery-title">
    <h4 id="pluggy-recovery-title">Recuperação dos movimentos importados</h4>
    <p className="meta">A prévia não altera nada. Informe um valor para rastrear um movimento (do importado até o efeito nos totais).</p>
    <div className="form__actions">
      <label>Rastrear valor (opcional)<input inputMode="decimal" value={amount} onChange={event => setAmount(event.target.value)} placeholder="55,99" /></label>
      <button type="button" disabled={busy} onClick={() => void generate()}>Gerar prévia</button>
      <button type="button" disabled={busy || !preview} onClick={() => void apply()}>Aplicar reprocessamento</button>
    </div>
    {error && <p role="alert" className="notice notice--error">{error}</p>}
    {applied && <p role="status" className="notice">Reprocessamento aplicado. Gere uma nova prévia para conferir o que ainda exige decisão.</p>}
    {preview && <div aria-label="Prévia do reprocessamento">
      <ul className="meta">
        {figures('Hoje', preview.totalBefore)}
        {figures('Depois', preview.totalAfter)}
      </ul>
      <p className="meta">{preview.willChange} movimento(s) mudariam · {preview.needsDecision} exigem decisão sua. Os efeitos já aplicados nunca são removidos em silêncio.</p>
      {preview.items.length === 0 ? <p className="meta">Nada a mudar nem a decidir.</p> : <ul className="transaction-list">
        {preview.items.map(item => <li key={item.importedTransactionId} className="transaction">
          <div className="transaction__main"><span className="transaction__description">{item.description}</span>
            <strong>{item.direction === 'Income' ? '+' : item.direction === 'Expense' ? '−' : ''}{money(item.amount)}</strong></div>
          <div className="transaction__meta meta"><span>{item.date ? formatDate(item.date) : NOT_INFORMED}</span><span>{item.linkDisplayName}</span>
            <span>Hoje: {recoveryEffectLabels[item.current.effect ?? 'None'] ?? item.current.effect}</span>
            <span>Proposta: {recoveryEffectLabels[item.proposed.effect ?? 'None'] ?? item.proposed.effect}{item.proposed.state === 'NeedsReview' ? ' (em revisão)' : ''}</span>
            <span>{item.outcome === 'NeedsDecision' ? `Exige decisão${item.note ? ` — ${reviewReason(item.note.replace('Rule:', ''))}` : ''}` : item.outcome === 'WillChange' ? 'Mudaria' : 'Sem mudança'}</span></div>
        </li>)}
      </ul>}
    </div>}
  </section>
}
