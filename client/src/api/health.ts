export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy'

export interface HealthCheck {
  name: string
  status: HealthStatus
  durationMs: number
}

export interface HealthResponse {
  status: HealthStatus
  totalDurationMs: number
  checks: HealthCheck[]
}

/**
 * Consulta GET /health. A API responde 503 quando algum check falha,
 * mas o corpo continua sendo um HealthResponse válido.
 */
export async function fetchHealth(signal?: AbortSignal): Promise<HealthResponse> {
  const response = await fetch('/health', { signal, headers: { Accept: 'application/json' } })

  const contentType = response.headers.get('content-type') ?? ''
  if (!contentType.includes('application/json')) {
    throw new Error(`Resposta inesperada da API (HTTP ${response.status}).`)
  }

  return (await response.json()) as HealthResponse
}
