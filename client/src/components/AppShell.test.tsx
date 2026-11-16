import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { AppShell } from './AppShell'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

const destinations = (nav: HTMLElement) => Array.from(nav.querySelectorAll<HTMLButtonElement>('.app-sidebar__items > button')).map(button => button.textContent)

function useMobileViewport() {
  let resize: (() => void) | undefined
  const viewport = { matches: true, addEventListener: (_: string, callback: () => void) => { resize = callback }, removeEventListener: vi.fn() }
  vi.stubGlobal('matchMedia', vi.fn(() => viewport))
  return { viewport, resize: () => resize?.() }
}

it('groups every active destination in four areas and marks the active area and screen', () => {
  const navigate = vi.fn()
  render(<AppShell section="cards" onNavigate={navigate} workspaceName="Meu orçamento" userArea={<button>Sair</button>} controls={<label>Competência<input type="month" /></label>}><p>Conteúdo atual</p></AppShell>)
  const nav = screen.getByRole('navigation', { name: 'Navegação do orçamento pessoal' })
  expect(Array.from(nav.querySelectorAll('.app-sidebar__group-toggle span:first-child')).map(group => group.textContent)).toEqual(['Visão Geral', 'Movimentações', 'Planejamento', 'Configurações'])
  expect(destinations(nav)).toEqual(['Visão Geral', 'Lançamentos', 'Contas', 'Cartões', 'Transferências', 'Orçamento', 'Agenda', 'Metas', 'Plano mensal', 'Previsões', 'Recorrências', 'Dívidas', 'Categorias', 'Configurações'])
  expect(within(nav).getByRole('button', { name: 'Área Movimentações' }).getAttribute('aria-current')).toBe('location')
  expect(within(nav).getByRole('button', { name: /Cartões/ }).getAttribute('aria-current')).toBe('page')
  expect(screen.getByRole('heading', { name: 'Cartões' })).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Sair' })).toBeTruthy()
  expect(screen.getByLabelText('Competência')).toBeTruthy()
  expect(screen.getByText('Conteúdo atual').closest('#finance-content')?.classList.contains('internal-page')).toBe(true)
  fireEvent.click(within(nav).getByRole('button', { name: /Lançamentos/ }))
  expect(navigate).toHaveBeenCalledWith('transactions')
  expect(within(nav).queryByRole('button', { name: /Relatórios/ })).toBeNull()
})

it('shows Automação under Configurações only when the integration is enabled', () => {
  const navigate = vi.fn()
  const view = render(<AppShell section="automation" onNavigate={navigate} workspaceName="Meu orçamento" automation><p>Conteúdo</p></AppShell>)
  const nav = screen.getByRole('navigation')
  expect(destinations(nav).slice(-3)).toEqual(['Categorias', 'Configurações', 'Automação'])
  expect(within(nav).getByRole('button', { name: 'Área Configurações' }).getAttribute('aria-current')).toBe('location')
  expect(within(nav).getByRole('button', { name: /Automação/ }).getAttribute('aria-current')).toBe('page')
  expect(within(nav).queryByText(/Laborat/)).toBeNull()
  fireEvent.click(within(nav).getByRole('button', { name: /Automação/ }))
  expect(navigate).toHaveBeenCalledWith('automation')
  view.rerender(<AppShell section="overview" onNavigate={navigate} workspaceName="Meu orçamento"><p>Conteúdo</p></AppShell>)
  expect(within(screen.getByRole('navigation')).queryByRole('button', { name: /Automação/ })).toBeNull()
})

it('opens the mobile menu, confines focus, closes on Escape and restores focus', async () => {
  useMobileViewport()
  render(<AppShell section="overview" onNavigate={vi.fn()} workspaceName="Personal"><p>Conteúdo</p></AppShell>)
  const trigger = screen.getByRole('button', { name: 'Abrir menu lateral' })
  fireEvent.click(trigger)
  expect(trigger.getAttribute('aria-expanded')).toBe('true')
  expect(document.body.style.overflow).toBe('hidden')
  const nav = screen.getByRole('navigation')
  await waitFor(() => expect(document.activeElement).toBe(within(nav).getByRole('button', { name: 'Visão Geral' })))
  const close = screen.getByRole('button', { name: 'Fechar navegação' })
  close.focus(); fireEvent.keyDown(close, { key: 'Tab', shiftKey: true })
  expect(document.activeElement).toBe(within(nav).getByRole('button', { name: 'Área Configurações' }))
  fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
  expect(trigger.getAttribute('aria-expanded')).toBe('false')
  await waitFor(() => expect(document.activeElement).toBe(trigger))
  expect(document.body.style.overflow).toBe('')
})

it('expands mobile groups without navigating and reopens the current group', async () => {
  useMobileViewport()
  const navigate = vi.fn()
  const view = render(<AppShell section="cards" onNavigate={navigate} workspaceName="Personal">Body</AppShell>)
  const trigger = screen.getByRole('button', { name: 'Abrir menu lateral' })
  fireEvent.click(trigger)
  const nav = screen.getByRole('navigation')
  expect(destinations(nav)).toEqual(['Lançamentos', 'Contas', 'Cartões', 'Transferências'])
  fireEvent.click(within(nav).getByRole('button', { name: 'Área Planejamento' }))
  expect(navigate).not.toHaveBeenCalled()
  expect(destinations(nav)).toEqual(['Orçamento', 'Agenda', 'Metas', 'Plano mensal', 'Previsões', 'Recorrências', 'Dívidas'])
  fireEvent.click(within(nav).getByRole('button', { name: /Agenda/ }))
  expect(navigate).toHaveBeenCalledWith('calendar')
  view.rerender(<AppShell section="calendar" onNavigate={navigate} workspaceName="Personal">Body</AppShell>)
  expect(trigger.getAttribute('aria-expanded')).toBe('false')
  await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('heading', { level: 1, name: 'Agenda' })))
  fireEvent.click(trigger)
  expect(destinations(screen.getByRole('navigation'))).toContain('Agenda')
  expect(within(screen.getByRole('navigation')).getByRole('button', { name: 'Área Planejamento' }).getAttribute('aria-expanded')).toBe('true')
})

it('closes the drawer when returning to a desktop viewport', async () => {
  const { viewport, resize } = useMobileViewport()
  render(<AppShell section="overview" onNavigate={vi.fn()} workspaceName="Personal">Body</AppShell>)
  const trigger = screen.getByRole('button', { name: 'Abrir menu lateral' })
  fireEvent.click(trigger)
  act(() => { viewport.matches = false; resize() })
  await waitFor(() => expect(trigger.getAttribute('aria-expanded')).toBe('false'))
  expect(document.body.style.overflow).toBe('')
})

it('does not offer retired flows', () => {
  render(<AppShell section="overview" onNavigate={vi.fn()} workspaceName="Personal">Body</AppShell>)
  const labels = destinations(screen.getByRole('navigation')).map(label => label ?? '')
  for (const retired of [/Documentos/i, /Inbox/i, /E-?mail/i, /Importa/i, /Concilia/i, /Confer/i]) expect(labels.filter(label => retired.test(label))).toEqual([])
})

it('focuses the title on direct navigation', () => {
  render(<AppShell section="calendar" onNavigate={vi.fn()} workspaceName="Meu orçamento"><p>Conteúdo</p></AppShell>)
  expect(document.activeElement).toBe(screen.getByRole('heading', { level: 1, name: 'Agenda' }))
})
