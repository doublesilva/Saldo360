import { afterEach, describe, expect, it } from 'vitest'
import { login } from './api/auth'
import {
  buildInviteLink,
  captureInviteToken,
  clearPendingInviteToken,
  getPendingInviteToken,
  isLocalOnlyHost,
} from './invite'

afterEach(() => {
  clearPendingInviteToken()
  window.history.replaceState(null, '', '/')
})

describe('captureInviteToken', () => {
  it('guarda o token e remove o fragmento da URL imediatamente', () => {
    window.history.replaceState(null, '', '/convite#token=abc_DEF-123')

    captureInviteToken()

    expect(window.location.pathname).toBe('/convite')
    expect(window.location.hash).toBe('')
    expect(window.location.href).not.toContain('abc_DEF-123')
    expect(window.sessionStorage.getItem('saldo360.pendingInvitation')).toBe('abc_DEF-123')
    expect(getPendingInviteToken()).toBe('abc_DEF-123')
  })

  it('ignora fragmentos fora da página de convite', () => {
    window.history.replaceState(null, '', '/#token=nao-e-convite')

    captureInviteToken()

    expect(getPendingInviteToken()).toBeNull()
    expect(window.location.hash).toBe('#token=nao-e-convite')
  })

  it('sem fragmento, mantém o token já guardado (volta do login)', () => {
    window.history.replaceState(null, '', '/convite#token=primeiro')
    captureInviteToken()

    window.history.replaceState(null, '', '/convite')
    captureInviteToken()

    expect(getPendingInviteToken()).toBe('primeiro')
  })

  it('limpa o token', () => {
    window.history.replaceState(null, '', '/convite#token=x')
    captureInviteToken()

    clearPendingInviteToken()

    expect(getPendingInviteToken()).toBeNull()
    expect(window.sessionStorage.getItem('saldo360.pendingInvitation')).toBeNull()
  })
})

describe('login a partir do convite', () => {
  it('pede ao provedor para voltar à página do convite, sem o token na URL', () => {
    window.history.replaceState(null, '', '/convite#token=segredo')
    captureInviteToken()
    const navigations: string[] = []

    login((url) => navigations.push(url))

    expect(navigations).toEqual(['/auth/login?returnUrl=%2Fconvite'])
    expect(navigations[0]).not.toContain('segredo')
    expect(getPendingInviteToken()).toBe('segredo')
  })
})

describe('buildInviteLink', () => {
  it('usa a origem do navegador e o token no fragmento', () => {
    expect(buildInviteLink('tok_en-1', 'https://saldo360.exemplo')).toBe('https://saldo360.exemplo/convite#token=tok_en-1')
  })
})

describe('isLocalOnlyHost', () => {
  it.each(['localhost', '127.0.0.1', 'app.localhost', '[::1]'])('%s só funciona na própria máquina', (host) => {
    expect(isLocalOnlyHost(host)).toBe(true)
  })

  it('domínio público não é local', () => {
    expect(isLocalOnlyHost('saldo360.exemplo.com.br')).toBe(false)
  })
})
