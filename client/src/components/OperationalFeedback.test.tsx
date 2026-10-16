import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ApiRequestError } from '../api/apiErrors'
import { FieldError, OperationalEmpty, OperationalError, OperationalLoading } from './OperationalFeedback'

describe('OperationalFeedback', () => {
  it('anuncia, focaliza e permite repetir um erro de leitura', () => {
    const retry = vi.fn()
    render(<OperationalError error={new ApiRequestError('Falha', 'network')} onRetry={retry} />)
    const alert = screen.getByRole('alert')
    expect(document.activeElement).toBe(alert)
    expect(alert.textContent).toContain('Verifique sua conexão')
    fireEvent.click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(retry).toHaveBeenCalledTimes(1)
  })

  it('representa carregamento, vazio acionável e erro de campo acessível', () => {
    const action = vi.fn()
    render(<><OperationalLoading>Carregando dados…</OperationalLoading><OperationalEmpty action={{ label: 'Cadastrar', onClick: action }}>Nenhum item.</OperationalEmpty><FieldError id="amount-error" errors={['Valor inválido.']} /></>)
    expect(screen.getByRole('status').getAttribute('aria-busy')).toBe('true')
    fireEvent.click(screen.getByRole('button', { name: 'Cadastrar' }))
    expect(action).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Valor inválido.').getAttribute('id')).toBe('amount-error')
  })
})
