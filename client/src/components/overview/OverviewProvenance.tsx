import type { BankSource } from '../../api/overview'
import { clockTime, dayTime, explain, resultReasonText, bridgeReasonText } from './overviewText'

// Origem e atualização dos valores de um bloco. O horário "Atualizado em" só aparece quando a MESMA resposta que trouxe os indicadores informa um instante verificável da origem
// (sincronização bancária mais antiga do conjunto). Sem ele, o texto diz que a atualização da origem está indisponível e, quando útil, mostra só a hora em que a consulta respondeu.

/** Motivos legíveis de uma ressalva: usa os textos do servidor já mapeados, na ordem recebida e sem repetir. */
export const reasonLines = (codes: string[]): string[] => {
  const seen = new Set<string>()
  return codes.map(code => resultReasonText[code] ?? bridgeReasonText[code] ?? explain([code])[0]).filter((text): text is string => Boolean(text) && !seen.has(text!) && Boolean(seen.add(text!)))
}

/**
 * Linha de origem bancária, derivada do conjunto de contas da MESMA resposta que trouxe os valores. Só descreve a sincronização bancária (não edições manuais nem metas):
 * recorte misto diz "Contas integradas", várias contas sincronizadas dizem que o horário é o da mais antiga, só contas manuais não mostram a linha e tipo ou horário desconhecido
 * diz que a atualização da origem está indisponível. Sem `source` (servidor antigo) só se confia no horário, sem classificar o recorte.
 */
export const bankLine = (source: BankSource | null | undefined, fallbackAt: string | null | undefined): string | null => {
  const unavailable = 'Atualização da origem indisponível'
  if (!source) { const at = fallbackAt ? dayTime(fallbackAt) : null; return at ? `Sincronização bancária em ${at}` : unavailable }
  if (source.accounts === 0) return unavailable
  if (source.integrated === 0) return null // todas as contas do recorte são manuais: não há sincronização a informar
  const mixed = source.integrated < source.accounts
  const at = source.synced > 0 && source.oldestSyncAt ? dayTime(source.oldestSyncAt) : null
  if (!at) return `${unavailable} · ${mixed ? 'contas integradas' : 'integração'} sem sincronização bancária registrada`
  return `${mixed ? 'Contas integradas · ' : ''}Sincronização bancária ${source.synced > 1 ? 'mais antiga ' : ''}em ${at}${source.synced < source.integrated ? ` · ${source.synced} de ${source.integrated} contas integradas sincronizadas` : ''}`
}

/** Sincronização bancária e horário da consulta são informações diferentes: a primeira é da origem, a segunda é a chegada da resposta neste navegador. */
export function SourceLine({ source, updatedAt, queriedAt }: { source: BankSource | null | undefined; updatedAt: string | null | undefined; queriedAt: number | null }) {
  const bank = bankLine(source, updatedAt)
  if (bank === null && queriedAt === null) return null
  return <p className="ov-meta ov-source">{bank !== null && <span className="ov-source__bank">{bank}</span>}{bank !== null && queriedAt !== null && ' · '}{queriedAt !== null && <span className="ov-source__queried">{`Consultado às ${clockTime(queriedAt)} (chegada da resposta)`}</span>}</p>
}

/** "Cobertura parcial · motivo" junto dos valores afetados; sem motivo identificado na resposta, só o estado. */
export function CoverageLine({ partial, reasons, extra = [] }: { partial: boolean; reasons: string[]; extra?: string[] }) {
  if (!partial) return null
  const lines = [...reasonLines(reasons), ...extra]
  return <p className="ov-meta ov-coverage" role="note">Cobertura parcial{lines.length > 0 && ` · ${lines.join(' ')}`}</p>
}
