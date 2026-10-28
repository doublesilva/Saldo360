import { useEffect, useRef, useState, type ReactNode } from 'react'
import '../InternalPages.css'

const navigationGroups = [
  { label: 'Principal', items: [['overview', 'Visão Geral'], ['calendar', 'Agenda'], ['budgets', 'Orçamentos'], ['cards', 'Cartões'], ['transactions', 'Lançamentos'], ['planning', 'Planejamento']] },
  { label: 'Ferramentas', items: [['reports', 'Relatórios'], ['goals', 'Metas'], ['recurrences', 'Recorrências']] },
  { label: 'Mais', items: [['settings', 'Configurações'], ['accounts', 'Contas'], ['categories', 'Categorias'], ['documents', 'Documentos'], ['debts', 'Dívidas'], ['transfers', 'Transferências'], ['planned', 'Previsões']] },
] as const

export function AppShell({ section, onNavigate, workspaceName, subtitle, controls, actions, userArea, labItems, children }: {
  section: string; onNavigate: (section: string) => void; workspaceName: string; subtitle?: string
  controls?: ReactNode; actions?: ReactNode; userArea?: ReactNode; labItems?: readonly (readonly [string, string])[]; children: ReactNode
}) {
  const groups: readonly { label: string; items: readonly (readonly [string, string])[] }[] = labItems?.length ? [...navigationGroups, { label: 'Laboratório', items: labItems }] : navigationGroups
  const [open, setOpen] = useState(false)
  const toggle = useRef<HTMLButtonElement>(null)
  const sidebar = useRef<HTMLElement>(null)
  const heading = useRef<HTMLHeadingElement>(null)
  const title = groups.flatMap(group => [...group.items]).find(([id]) => id === section)?.[1] ?? 'Visão Geral'
  const close = () => { setOpen(false); requestAnimationFrame(() => toggle.current?.focus()) }
  useEffect(() => {
    if (!window.matchMedia) return
    const desktop = window.matchMedia('(min-width: 60.001rem)')
    const onResize = () => { if (desktop.matches) setOpen(false) }
    desktop.addEventListener('change', onResize)
    return () => desktop.removeEventListener('change', onResize)
  }, [])
  useEffect(() => {
    if (!open) return
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    sidebar.current?.querySelector<HTMLButtonElement>('[aria-current="page"]')?.focus()
    return () => { document.body.style.overflow = previous }
  }, [open])
  return <div className="app-shell">
    <a className="skip-link" href="#finance-content">Ir para o conteúdo</a>
    {open && <button type="button" className="app-shell__scrim" aria-label="Fechar menu lateral" onClick={close} />}
    <aside ref={sidebar} id="finance-sidebar" className={`app-sidebar${open ? ' app-sidebar--open' : ''}`} role={open ? 'dialog' : undefined} aria-modal={open || undefined} aria-label="Menu lateral" onKeyDown={event => {
      if (!open) return
      if (event.key === 'Escape') { event.preventDefault(); close() }
      if (event.key === 'Tab') {
        const buttons = sidebar.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')
        if (!buttons?.length) return
        const first = buttons[0], last = buttons[buttons.length - 1]
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
      }
    }}>
      <div className="app-sidebar__brand"><span className="brand-mark" aria-hidden="true">S</span><strong>Saldo360</strong><button className="app-sidebar__close" type="button" onClick={close} aria-label="Fechar navegação">×</button></div>
      <nav aria-label="Navegação do orçamento pessoal">{groups.map(group => <div className="app-sidebar__group" key={group.label}>
        <p>{group.label}</p>{group.items.map(([id, label]) => <button key={id} type="button" disabled={id === 'reports'} title={id === 'reports' ? 'Relatórios dedicados ainda não disponíveis' : undefined} aria-current={section === id ? 'page' : undefined} onClick={() => { onNavigate(id); setOpen(false); requestAnimationFrame(() => heading.current?.focus()) }}>
          <span className="nav-marker" aria-hidden="true" />{label}{id === 'reports' && <small>Em breve</small>}
        </button>)}
      </div>)}</nav>
      <div className="app-sidebar__workspace"><span className="meta">Orçamento pessoal</span><h2>{workspaceName}</h2><small>Privado · somente você</small></div>
    </aside>
    <div className="app-shell__main" inert={open}>
      <header className="app-topbar"><div className="app-topbar__title"><button ref={toggle} className="app-menu-toggle" type="button" aria-label="Abrir menu lateral" aria-controls="finance-sidebar" aria-expanded={open} onClick={() => setOpen(true)}>☰</button><div><h1 ref={heading} tabIndex={-1}>{title}</h1>{subtitle && <p>{subtitle}</p>}</div></div><div className="app-topbar__user">{actions && <div className="app-topbar__actions">{actions}</div>}{userArea}</div><div className="app-topbar__controls">{controls}</div></header>
      <div id="finance-content" tabIndex={-1} className={section === 'overview' ? 'app-content' : 'app-content internal-page'}>{children}</div>
    </div>
  </div>
}
