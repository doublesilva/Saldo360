import type { ReactNode } from 'react'

/**
 * Seção expansível da Visão Geral. O conteúdo só é montado com a seção aberta: o que depende de outra consulta (evolução, projeção) só é carregado quando o usuário pede.
 * O estado aberto/fechado fica com quem usa (o clique no título só o inverte, em vez de deixar o navegador alternar sozinho), para que um alerta consiga abrir o detalhe correspondente.
 */
export function Disclosure({ id, title, hint, open, onOpenChange, children }: { id: string; title: string; hint?: string; open: boolean; onOpenChange: (open: boolean) => void; children: ReactNode }) {
  return <details id={`ov-d-${id}`} className="ov-disclosure" open={open}>
    <summary onClick={event => { event.preventDefault(); onOpenChange(!open) }}><span className="ov-disclosure__title">{title}</span>{hint && <small className="ov-disclosure__hint">{hint}</small>}</summary>
    {open && <div className="ov-disclosure__body">{children}</div>}
  </details>
}

export type DetailKey = 'coverage' | 'pressures' | 'cashflow' | 'competence' | 'evolution' | 'recovery' | 'budget' | 'commitments' | 'recent' | 'goals'
