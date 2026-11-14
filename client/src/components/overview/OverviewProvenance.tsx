import { clockTime, dayTime, explain, resultReasonText, bridgeReasonText } from './overviewText'

// Origem e atualização dos valores de um bloco. O horário "Atualizado em" só aparece quando a MESMA resposta que trouxe os indicadores informa um instante verificável da origem
// (sincronização bancária mais antiga do conjunto). Sem ele, o texto diz que a atualização da origem está indisponível e, quando útil, mostra só a hora em que a consulta respondeu.

/** Motivos legíveis de uma ressalva: usa os textos do servidor já mapeados, na ordem recebida e sem repetir. */
export const reasonLines = (codes: string[]): string[] => {
  const seen = new Set<string>()
  return codes.map(code => resultReasonText[code] ?? bridgeReasonText[code] ?? explain([code])[0]).filter((text): text is string => Boolean(text) && !seen.has(text!) && Boolean(seen.add(text!)))
}

export function SourceLine({ updatedAt, queriedAt }: { updatedAt: string | null | undefined; queriedAt: number | null }) {
  const updated = updatedAt ? dayTime(updatedAt) : null
  return <p className="ov-meta ov-source">{updated ? `Atualizado em ${updated}` : 'Atualização da origem indisponível'}{queriedAt !== null && !updated && ` · Consultado às ${clockTime(queriedAt)}`}</p>
}

/** "Cobertura parcial · motivo" junto dos valores afetados; sem motivo identificado na resposta, só o estado. */
export function CoverageLine({ partial, reasons, extra = [] }: { partial: boolean; reasons: string[]; extra?: string[] }) {
  if (!partial) return null
  const lines = [...reasonLines(reasons), ...extra]
  return <p className="ov-meta ov-coverage" role="note">Cobertura parcial{lines.length > 0 && ` · ${lines.join(' ')}`}</p>
}
