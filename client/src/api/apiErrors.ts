export type ApiErrorKind = 'validation' | 'business' | 'conflict' | 'authentication' | 'authorization' | 'notFound' | 'dependency' | 'network' | 'timeout' | 'cancelled' | 'unexpected'

export interface ApiProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  code?: string
  traceId?: string
  errors?: Record<string, string[]>
}

export class ApiRequestError extends Error {
  readonly kind: ApiErrorKind
  readonly problem?: ApiProblemDetails
  readonly resultUncertain: boolean
  constructor(message: string, kind: ApiErrorKind, problem?: ApiProblemDetails, resultUncertain = false) {
    super(message)
    this.name = 'ApiRequestError'
    this.kind = kind
    this.problem = problem
    this.resultUncertain = resultUncertain
  }
  get status() { return this.problem?.status }
  get code() { return this.problem?.code }
  get traceId() { return this.problem?.traceId }
  get fieldErrors() { return this.problem?.errors ?? {} }
}

function kindFor(problem: ApiProblemDetails): ApiErrorKind {
  if (problem.status === 400 && problem.errors) return 'validation'
  if (problem.status === 401) return 'authentication'
  if (problem.status === 403) return 'authorization'
  if (problem.status === 404) return 'notFound'
  if (problem.status === 409) return 'conflict'
  if (problem.status === 422) return 'business'
  if (problem.status === 503) return 'dependency'
  return problem.status && problem.status >= 400 && problem.status < 500 ? 'business' : 'unexpected'
}

export async function errorFromResponse(response: Response, fallback: string): Promise<ApiRequestError> {
  let problem: ApiProblemDetails = { status: response.status }
  try {
    const body: unknown = await response.json()
    if (typeof body === 'object' && body !== null) problem = { ...body, status: response.status }
  } catch {
    // Respostas legadas ou de infraestrutura podem não conter JSON.
  }
  const firstFieldError = Object.values(problem.errors ?? {}).flat()[0]
  return new ApiRequestError(firstFieldError ?? problem.detail ?? problem.title ?? fallback, kindFor(problem), problem)
}

export function normalizeRequestError(error: unknown, resultUncertain = false): ApiRequestError {
  if (error instanceof ApiRequestError) return error
  if (error instanceof DOMException && error.name === 'AbortError') return new ApiRequestError('A requisição foi cancelada.', 'cancelled')
  if (error instanceof TypeError) return new ApiRequestError(
    resultUncertain
      ? 'A conexão foi interrompida. O resultado da operação é incerto; atualize os dados antes de tentar novamente.'
      : 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.',
    'network', undefined, resultUncertain,
  )
  return new ApiRequestError('Não foi possível concluir a operação.', 'unexpected', undefined, resultUncertain)
}

export function toUserMessage(error: unknown): string {
  const normalized = normalizeRequestError(error)
  const message = normalized.resultUncertain
    ? 'Não foi possível confirmar se a operação foi concluída. Consulte os dados atualizados antes de tentar novamente; a operação pode já ter sido registrada.'
    : normalized.kind === 'authentication'
      ? 'Sua sessão expirou. Entre novamente para continuar.'
      : normalized.kind === 'authorization'
        ? 'Você não tem permissão para executar esta operação nesta família.'
        : normalized.kind === 'notFound'
          ? 'O recurso solicitado não foi encontrado ou não está mais disponível.'
          : normalized.kind === 'dependency'
            ? 'Um serviço necessário está indisponível no momento. Tente novamente em instantes.'
            : normalized.kind === 'network'
              ? 'Não foi possível conectar ao servidor. Verifique sua conexão e tente novamente.'
              : normalized.kind === 'conflict'
                ? normalized.message
                : normalized.message
  return `${message}${normalized.traceId ? ` Referência: ${normalized.traceId}.` : ''}${normalized.kind === 'conflict' ? ' Atualize os dados antes de tentar novamente.' : ''}`
}

export async function requestJson<T>(input: RequestInfo | URL, init: RequestInit, options: { timeoutMs?: number; resultUncertainOnTimeout?: boolean; fallback?: string } = {}): Promise<T> {
  const controller = new AbortController()
  let timedOut = false
  const timeout = globalThis.setTimeout(() => { timedOut = true; controller.abort() }, options.timeoutMs ?? 15_000)
  const externalSignal = init.signal
  const cancelFromExternal = () => controller.abort()
  if (externalSignal?.aborted) controller.abort()
  else externalSignal?.addEventListener('abort', cancelFromExternal, { once: true })
  try {
    const response = await fetch(input, { ...init, signal: controller.signal })
    if (!response.ok) throw await errorFromResponse(response, options.fallback ?? `Falha na operação (HTTP ${response.status}).`)
    return (response.status === 204 ? undefined : await response.json()) as T
  } catch (error) {
    if (timedOut) throw new ApiRequestError('O servidor não respondeu a tempo. O resultado da operação é incerto; atualize os dados antes de tentar novamente.', 'timeout', undefined, options.resultUncertainOnTimeout ?? false)
    if (externalSignal?.aborted) throw new ApiRequestError('A requisição foi cancelada.', 'cancelled')
    throw normalizeRequestError(error, options.resultUncertainOnTimeout ?? false)
  } finally {
    globalThis.clearTimeout(timeout)
    externalSignal?.removeEventListener('abort', cancelFromExternal)
  }
}
