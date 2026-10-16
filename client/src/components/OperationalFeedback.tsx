import { useEffect, useRef } from 'react'
import { toUserMessage } from '../api/apiErrors'

export function OperationalError({ error, onRetry, retryLabel = 'Tentar novamente' }: { error: unknown; onRetry?: () => void; retryLabel?: string }) {
  const ref = useRef<HTMLParagraphElement>(null)
  useEffect(() => { ref.current?.focus() }, [error])
  return <div className="operational-feedback operational-feedback--error">
    <p ref={ref} role="alert" aria-live="assertive" tabIndex={-1}>{typeof error === 'string' ? error : toUserMessage(error)}</p>
    {onRetry && <button type="button" onClick={onRetry}>{retryLabel}</button>}
  </div>
}

export function OperationalLoading({ children }: { children: string }) {
  return <p className="operational-feedback operational-feedback--loading" role="status" aria-live="polite" aria-busy="true">{children}</p>
}

export function OperationalEmpty({ children, action }: { children: string; action?: { label: string; onClick: () => void } }) {
  return <div className="operational-feedback operational-feedback--empty"><p>{children}</p>{action && <button type="button" onClick={action.onClick}>{action.label}</button>}</div>
}

export function FieldError({ id, errors }: { id: string; errors?: string[] }) {
  if (!errors?.length) return null
  return <p id={id} className="field-error" role="alert">{errors[0]}</p>
}
