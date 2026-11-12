import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { PersonalFinance } from './PersonalFinance'

const ACCOUNT = '3f2b8c1e-5a47-4d0e-9b61-0c7d2a9e4f10'
const account = { id: ACCOUNT, name: 'Conta Teste', isActive: true, isPrimary: true, financialScope: 'PF', type: 'Checking' }

function mockApi(accounts: unknown[] = [account]) {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input); calls.push(url)
    if (url === '/auth/csrf') return Response.json({ token: 'csrf' })
    if (url === '/api/me/financial-workspace') return Response.json({ id: 'personal-a', ownerUserId: 'a', name: 'Personal', type: 'Personal', isActive: true })
    if (url.includes('/accounts?includeInactive=true')) return Response.json(accounts)
    return new Response('{}', { status: 503 })
  }))
  return calls
}
const path = () => window.location.pathname + window.location.search
const open = (url: string) => window.history.replaceState(null, '', url)

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

describe('rotas — a tela e os filtros ficam na URL', () => {
  it('abrir a Agenda pelo menu muda a URL, marca o menu e grava o recorte', async () => {
    mockApi(); render(<PersonalFinance />)
    fireEvent.click(await screen.findByRole('button', { name: 'Agenda' }))
    expect(await screen.findByRole('heading', { name: 'Agenda' })).toBeTruthy()
    await waitFor(() => expect(path()).toMatch(/^\/agenda\?competencia=\d{4}-\d{2}/))
    expect(screen.getByRole('button', { name: 'Agenda' }).getAttribute('aria-current')).toBe('page')
  })

  it('link direto para a Agenda restaura competência, conta e tipo de evento', async () => {
    mockApi(); open(`/agenda?competencia=2026-09&conta=${ACCOUNT}&tipo=Expense`)
    render(<PersonalFinance />)
    expect(await screen.findByText('Setembro de 2026')).toBeTruthy()
    await waitFor(() => expect((screen.getByLabelText('Conta') as HTMLSelectElement).value).toBe(ACCOUNT))
    expect((screen.getByLabelText('Tipo') as HTMLSelectElement).value).toBe('Expense')
    expect(path()).toBe(`/agenda?competencia=2026-09&conta=${ACCOUNT}&tipo=Expense`)
  })

  it('consolidado ("todas") é restaurado e não vira a conta principal', async () => {
    mockApi(); open('/agenda?competencia=2026-09&conta=todas')
    render(<PersonalFinance />)
    await screen.findByText('Setembro de 2026')
    await waitFor(() => expect((screen.getByLabelText('Conta') as HTMLSelectElement).value).toBe(''))
    expect(path()).toBe('/agenda?competencia=2026-09&conta=todas')
  })

  it('parâmetros inválidos são substituídos (sem nova entrada) e o ajuste é avisado no bloco de filtros', async () => {
    mockApi(); open('/agenda?competencia=2026-13&tipo=Transfer')
    const before = window.history.length
    render(<PersonalFinance />)
    expect(await screen.findByText(/Parte do link não era válida/)).toBeTruthy()
    expect((screen.getByLabelText('Tipo') as HTMLSelectElement).value).toBe('')
    expect(path()).not.toContain('tipo=')
    expect(path()).not.toContain('2026-13')
    expect(window.history.length).toBe(before)
  })

  it('conta indisponível volta ao padrão e informa, sem consultar dados com a conta inválida', async () => {
    const calls = mockApi([]); open(`/planejamento?aba=seis-meses&competencia=2026-11&conta=${ACCOUNT}&recuperacao=1`)
    render(<PersonalFinance />)
    expect(await screen.findByText(/A conta do link não está disponível/)).toBeTruthy()
    expect(path()).not.toContain('conta=')
    expect(path()).not.toContain('recuperacao=')
    expect(calls.some(url => url.includes(ACCOUNT) && url.includes('monthly-plan'))).toBe(false)
  })

  it('recuperação: aba, competência e conta do link são restauradas e continuam na URL', async () => {
    mockApi(); open(`/planejamento?aba=seis-meses&competencia=2026-11&conta=${ACCOUNT}&recuperacao=1`)
    render(<PersonalFinance />)
    await waitFor(() => expect(screen.getByRole('tab', { name: /Seis meses/ }).getAttribute('aria-selected')).toBe('true'))
    expect((screen.getByLabelText('Competência') as HTMLInputElement).value).toBe('2026-11')
    expect(path()).toBe(`/planejamento?aba=seis-meses&competencia=2026-11&conta=${ACCOUNT}&recuperacao=1`)
  })

  it('voltar e avançar restauram a seção e o recorte de cada entrada', async () => {
    mockApi(); render(<PersonalFinance />)
    fireEvent.change(await screen.findByLabelText('Competência'), { target: { value: '2026-08' } })
    await waitFor(() => expect(path()).toBe('/?competencia=2026-08'))
    fireEvent.click(screen.getByRole('button', { name: 'Agenda' }))
    await screen.findByRole('heading', { name: 'Agenda' })
    window.history.back()
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Visão Geral' })).toBeTruthy())
    expect((await screen.findByLabelText('Competência') as HTMLInputElement).value).toBe('2026-08')
    expect(screen.getByRole('button', { name: 'Visão Geral' }).getAttribute('aria-current')).toBe('page')
    window.history.forward()
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Agenda' })).toBeTruthy())
  })
})
