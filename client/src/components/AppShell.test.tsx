import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { AppShell } from './AppShell'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

it('keeps primary navigation ordered and marks the current section, preserving user and module slots', () => {
  const navigate = vi.fn()
  render(<AppShell section="cards" onNavigate={navigate} workspaceName="Meu orçamento" userArea={<button>Sair</button>} controls={<label>Competência<input type="month" /></label>}><p>Conteúdo atual</p></AppShell>)
  const nav = screen.getByRole('navigation', { name: 'Navegação do orçamento pessoal' })
  expect(within(nav).getAllByRole('button').slice(0, 6).map(button => button.textContent)).toEqual(['Visão Geral', 'Agenda', 'Orçamentos', 'Cartões', 'Lançamentos', 'Planejamento'])
  expect(within(nav).getByRole('button', { name: /Cartões/ }).getAttribute('aria-current')).toBe('page')
  expect(screen.getByRole('heading', { name: 'Cartões' })).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Sair' })).toBeTruthy()
  expect(screen.getByLabelText('Competência')).toBeTruthy()
  expect(screen.getByText('Conteúdo atual')).toBeTruthy()
  fireEvent.click(within(nav).getByRole('button', { name: /Lançamentos/ }))
  expect(navigate).toHaveBeenCalledWith('transactions')
  expect((within(nav).getByRole('button', { name: /Relatórios/ }) as HTMLButtonElement).disabled).toBe(true)
})

it('opens a collapsible menu, confines focus, closes on Escape and restores scrolling and trigger focus', async () => {
  render(<AppShell section="overview" onNavigate={vi.fn()} workspaceName="Personal"><p>Conteúdo</p></AppShell>)
  const trigger = screen.getByRole('button', { name: 'Abrir menu lateral' })
  fireEvent.click(trigger)
  expect(trigger.getAttribute('aria-expanded')).toBe('true')
  expect(document.body.style.overflow).toBe('hidden')
  const nav = screen.getByRole('navigation')
  await waitFor(() => expect(document.activeElement).toBe(within(nav).getByRole('button', { name: /Visão Geral/ })))
  const close = screen.getByRole('button', { name: 'Fechar navegação' })
  close.focus(); fireEvent.keyDown(close, { key: 'Tab', shiftKey: true })
  expect(document.activeElement).toBe(within(nav).getByRole('button', { name: 'Previsões' }))
  fireEvent.keyDown(document.activeElement!, { key: 'Escape' })
  expect(trigger.getAttribute('aria-expanded')).toBe('false')
  await waitFor(() => expect(document.activeElement).toBe(trigger))
  expect(document.body.style.overflow).toBe('')
})

it('closes the drawer after navigation and when returning to a desktop viewport', async () => {
  let resize: (() => void) | undefined
  const desktop = { matches: false, addEventListener: (_: string, callback: () => void) => { resize = callback }, removeEventListener: vi.fn() }
  vi.stubGlobal('matchMedia', vi.fn(() => desktop))
  const navigate = vi.fn()
  render(<AppShell section="overview" onNavigate={navigate} workspaceName="Personal">Body</AppShell>)
  const trigger = screen.getByRole('button', { name: 'Abrir menu lateral' })
  fireEvent.click(trigger)
  fireEvent.click(screen.getByRole('button', { name: /Agenda/ }))
  expect(navigate).toHaveBeenCalledWith('calendar')
  expect(trigger.getAttribute('aria-expanded')).toBe('false')
  fireEvent.click(trigger)
  act(() => { desktop.matches = true; resize?.() })
  await waitFor(() => expect(trigger.getAttribute('aria-expanded')).toBe('false'))
  expect(document.body.style.overflow).toBe('')
})
