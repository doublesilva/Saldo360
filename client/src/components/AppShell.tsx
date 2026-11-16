import { useEffect, useRef, useState, type ReactNode } from 'react'
import '../InternalPages.css'

const navigationGroups = [
  { id: 'overview', label: 'Visão Geral', items: [['overview', 'Visão Geral']] },
  { id: 'movement', label: 'Movimentações', items: [['transactions', 'Lançamentos'], ['accounts', 'Contas'], ['cards', 'Cartões'], ['transfers', 'Transferências']] },
  { id: 'planning', label: 'Planejamento', items: [['budgets', 'Orçamento'], ['calendar', 'Agenda'], ['goals', 'Metas'], ['planning', 'Plano mensal'], ['planned', 'Previsões'], ['recurrences', 'Recorrências'], ['debts', 'Dívidas']] },
  { id: 'settings', label: 'Configurações', items: [['categories', 'Categorias'], ['settings', 'Configurações']] },
] as const

type NavigationGroup = { id: string; label: string; items: readonly (readonly [string, string])[] }

export function AppShell({ section, onNavigate, workspaceName, subtitle, controls, actions, userArea, automation, children }: {
  section: string; onNavigate: (section: string) => void; workspaceName: string; subtitle?: string
  controls?: ReactNode; actions?: ReactNode; userArea?: ReactNode; automation?: boolean; children: ReactNode
}) {
  // "Automação" (central Pluggy + agente de IA) só aparece quando a integração está habilitada para este usuário.
  const groups: readonly NavigationGroup[] = navigationGroups.map(group => group.id === 'settings' && automation ? { ...group, items: [...group.items, ['automation', 'Automação'] as const] } : group)
  const activeGroup = groups.find(group => group.items.some(([id]) => id === section)) ?? groups[0]
  const [open, setOpen] = useState(false)
  const [mobile, setMobile] = useState(() => window.matchMedia?.('(max-width: 60rem)').matches ?? false)
  const [expandedGroup, setExpandedGroup] = useState(activeGroup.id)
  const toggle = useRef<HTMLButtonElement>(null)
  const sidebar = useRef<HTMLElement>(null)
  const heading = useRef<HTMLHeadingElement>(null)
  // Abertura da tela (link direto, recarga ou volta do login): o foco vai ao título, a menos que algo já tenha recebido foco.
  useEffect(() => { if (!document.activeElement || document.activeElement === document.body) heading.current?.focus() }, [])
  const title = groups.flatMap(group => [...group.items]).find(([id]) => id === section)?.[1] ?? 'Visão Geral'
  const close = () => { setOpen(false); requestAnimationFrame(() => toggle.current?.focus()) }
  useEffect(() => { setExpandedGroup(activeGroup.id) }, [activeGroup.id])
  useEffect(() => {
    if (!window.matchMedia) return
    const viewport = window.matchMedia('(max-width: 60rem)')
    const onResize = () => { setMobile(viewport.matches); if (!viewport.matches) setOpen(false) }
    viewport.addEventListener('change', onResize)
    return () => viewport.removeEventListener('change', onResize)
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
      <nav aria-label="Navegação do orçamento pessoal">{groups.map(group => {
        const active = group.id === activeGroup.id
        const expanded = !mobile || expandedGroup === group.id
        const itemsId = `finance-nav-${group.id}`
        return <section className="app-sidebar__group" data-active={active || undefined} key={group.id}>
          <button className="app-sidebar__group-toggle" type="button" aria-label={`Área ${group.label}`} aria-expanded={expanded} aria-controls={itemsId} aria-current={active ? 'location' : undefined}
            onClick={() => { if (mobile) setExpandedGroup(current => current === group.id ? '' : group.id) }}>
            <span>{group.label}</span><span aria-hidden="true" className="app-sidebar__chevron">⌄</span>
          </button>
          {expanded && <div id={itemsId} className="app-sidebar__items">{group.items.map(([id, label]) => <button key={id} type="button" aria-current={section === id ? 'page' : undefined} onClick={() => { onNavigate(id); setOpen(false); requestAnimationFrame(() => heading.current?.focus()) }}>
            <span className="nav-marker" aria-hidden="true" />{label}
          </button>)}</div>}
        </section>
      })}</nav>
      <div className="app-sidebar__workspace"><span className="meta">Orçamento pessoal</span><h2>{workspaceName}</h2><small>Privado · somente você</small></div>
    </aside>
    <div className="app-shell__main" inert={open}>
      <header className="app-topbar"><div className="app-topbar__title"><button ref={toggle} className="app-menu-toggle" type="button" aria-label="Abrir menu lateral" aria-controls="finance-sidebar" aria-expanded={open} onClick={() => { setExpandedGroup(activeGroup.id); setOpen(true) }}>☰</button><div><h1 ref={heading} tabIndex={-1}>{title}</h1>{subtitle && <p>{subtitle}</p>}</div></div><div className="app-topbar__user">{actions && <div className="app-topbar__actions">{actions}</div>}{userArea}</div><div className="app-topbar__controls">{controls}</div></header>
      <div id="finance-content" tabIndex={-1} className={section === 'overview' ? 'app-content' : 'app-content internal-page'}>{children}</div>
    </div>
  </div>
}
