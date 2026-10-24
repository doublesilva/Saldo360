import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import { PdfPasswordHint } from './PdfPasswordHint'
afterEach(cleanup)
it.each([
  ['CpfNotRegistered', 'CPF não cadastrado.'], ['CpfInvalid', 'CPF do perfil inválido.'],
  ['CpfSessionInvalid', 'Sessão de perfil inválida ou expirada.'], ['CpfAccessDenied', 'Acesso ao perfil não autorizado.'],
  ['CpfUnavailable', 'Perfil temporariamente indisponível.'],
])('diferencia %s sem exibir dado sensível', (code, text) => {
  render(<PdfPasswordHint code={code} />)
  expect(screen.getByRole('status').textContent).toContain(text)
  expect(screen.getByRole('status').textContent).toContain('senha')
})
it('não ecoa códigos desconhecidos ou dados recebidos', () => {
  render(<PdfPasswordHint code="not-a-safe-status" />)
  expect(screen.queryByRole('status')).toBeNull()
})
