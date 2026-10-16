import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiRequestError, errorFromResponse, normalizeRequestError, requestJson, toUserMessage } from './apiErrors'

describe('apiErrors', () => {
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks() })

  it('normaliza validação por campo, conflito e traceId', async () => {
    const validation = await errorFromResponse(new Response(JSON.stringify({
      type: 'https://saldo360.local/problems/validation-error', title: 'Dados inválidos.', status: 400,
      detail: 'Revise os dados.', instance: '/payments', code: 'VALIDATION_ERROR', traceId: 'trace-123',
      errors: { amount: ['Valor decimal inválido.'] },
    }), { status: 400 }), 'Falha')
    expect(validation.kind).toBe('validation')
    expect(validation.fieldErrors.amount).toEqual(['Valor decimal inválido.'])
    expect(toUserMessage(validation)).toContain('Referência: trace-123')

    const conflict = await errorFromResponse(new Response(JSON.stringify({ status: 409, code: 'INSUFFICIENT_FUNDS', detail: 'Saldo insuficiente.' }), { status: 409 }), 'Falha')
    expect(conflict).toMatchObject({ kind: 'conflict', code: 'INSUFFICIENT_FUNDS' })
  })

  it('distingue rede, cancelamento e erro desconhecido sem expor detalhes internos', () => {
    expect(normalizeRequestError(new TypeError('connection string secreta')).kind).toBe('network')
    expect(normalizeRequestError(new DOMException('troca de família', 'AbortError')).kind).toBe('cancelled')
    const unknown = normalizeRequestError(new Error('SELECT * FROM secrets'))
    expect(unknown.kind).toBe('unexpected')
    expect(unknown.message).not.toContain('SELECT')
  })

  it('classifica timeout de escrita como resultado incerto e não repete a requisição', async () => {
    vi.useFakeTimers()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((_input, init) => new Promise((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('timeout', 'AbortError')))
    }))
    const request = requestJson('/write', { method: 'POST' }, { timeoutMs: 10, resultUncertainOnTimeout: true }).catch(value => value as ApiRequestError)
    await vi.advanceTimersByTimeAsync(10)
    const error = await request as ApiRequestError
    expect(error).toMatchObject({ kind: 'timeout', resultUncertain: true })
    expect(error.message).toContain('resultado da operação é incerto')
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('fornece mensagens operacionais coerentes sem perder referência técnica', async () => {
    const expired = await errorFromResponse(new Response('{}', { status: 401 }), 'Falha')
    const forbidden = await errorFromResponse(new Response('{}', { status: 403 }), 'Falha')
    const missing = await errorFromResponse(new Response('{}', { status: 404 }), 'Falha')
    const dependency = await errorFromResponse(new Response(JSON.stringify({ traceId: 'trace-service' }), { status: 503 }), 'Falha')
    expect(toUserMessage(expired)).toContain('sessão expirou')
    expect(toUserMessage(forbidden)).toContain('não tem permissão')
    expect(toUserMessage(missing)).toContain('não foi encontrado')
    expect(toUserMessage(dependency)).toContain('serviço necessário')
    expect(toUserMessage(dependency)).toContain('trace-service')
  })

  it('orienta consulta antes do reenvio quando o resultado é incerto', () => {
    const error = new ApiRequestError('timeout interno', 'timeout', undefined, true)
    expect(toUserMessage(error)).toContain('Consulte os dados atualizados antes de tentar novamente')
    expect(toUserMessage(error)).toContain('pode já ter sido registrada')
  })
})
