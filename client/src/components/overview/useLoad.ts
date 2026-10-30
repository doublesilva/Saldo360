import { useCallback, useEffect, useState } from 'react'

export type Load<T> = { state: 'loading' } | { state: 'error'; error: unknown } | { state: 'loaded'; data: T }

/**
 * Carrega um bloco da Visão Geral com cancelamento, recarga própria e sem resposta atrasada: uma resposta de outra chave (competência ou escopo antigos)
 * nunca é exibida. O último dado carregado é devolvido em `previous` para a tela não piscar vazia durante a troca de filtro.
 */
export function useLoad<T>(key: string, load: (signal: AbortSignal) => Promise<T>): { load: Load<T>; previous: T | null; retry: () => void } {
  const [attempt, setAttempt] = useState(0)
  const requestKey = `${key}|${attempt}`
  const [result, setResult] = useState<{ key: string; value: Load<T> }>({ key: requestKey, value: { state: 'loading' } })
  const [previous, setPrevious] = useState<T | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal)
      .then(data => { if (!controller.signal.aborted) { setResult({ key: requestKey, value: { state: 'loaded', data } }); setPrevious(data) } })
      .catch((error: unknown) => { if (!controller.signal.aborted) setResult({ key: requestKey, value: { state: 'error', error } }) })
    return () => controller.abort()
  }, [requestKey, load])
  const retry = useCallback(() => setAttempt(value => value + 1), [])
  return { load: result.key === requestKey ? result.value : { state: 'loading' }, previous, retry }
}
