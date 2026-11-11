import type { CSSProperties } from 'react'

/**
 * Esqueleto proporcional ao indicador que vai ocupar o lugar: reserva a altura e a largura do valor para a tela não saltar, sem inventar número nenhum. É decorativo (aria-hidden);
 * quem o usa declara o carregamento num texto neutro para leitores de tela.
 */
export function Skeleton({ width = '8rem', height = '1.5rem', className }: { width?: string; height?: string; className?: string }) {
  return <span className={`ov-skeleton${className ? ` ${className}` : ''}`} aria-hidden="true" style={{ '--sk-w': width, '--sk-h': height } as CSSProperties} />
}

/** Aviso de carregamento da seção para leitores de tela, em tom neutro (nunca vermelho). */
export const LoadingNote = ({ children }: { children: string }) => <span className="visually-hidden" role="status">{children}</span>
