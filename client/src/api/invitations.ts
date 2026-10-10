import { CSRF_HEADER, getCsrfToken } from './csrf'
import type { FamilyDetails } from './families'

export interface CreatedInvitation {
  id: string
  /** Token em texto puro: só existe nesta resposta. Não guarde nem registre. */
  token: string
  expiresAt: string
}

export type AcceptInvitationResult =
  | { kind: 'accepted'; alreadyMember: boolean; family: FamilyDetails }
  /** Convite inválido, expirado, revogado ou já usado (resposta genérica da API). */
  | { kind: 'unavailable' }
  /** Sessão ausente/expirada: é preciso entrar de novo. */
  | { kind: 'unauthenticated' }

const jsonHeaders = { Accept: 'application/json' }

async function csrfHeaders(): Promise<Record<string, string>> {
  return { ...jsonHeaders, [CSRF_HEADER]: await getCsrfToken() }
}

export async function createInvitation(familyId: string): Promise<CreatedInvitation> {
  const response = await fetch(`/api/families/${encodeURIComponent(familyId)}/invitations`, {
    method: 'POST',
    headers: await csrfHeaders(),
  })
  if (response.status === 403) throw new Error('Somente o responsável pode convidar pessoas.')
  if (response.status !== 201) throw new Error(`Falha ao gerar o convite (HTTP ${response.status}).`)
  return (await response.json()) as CreatedInvitation
}

export async function revokeInvitation(familyId: string, invitationId: string): Promise<void> {
  const response = await fetch(
    `/api/families/${encodeURIComponent(familyId)}/invitations/${encodeURIComponent(invitationId)}`,
    { method: 'DELETE', headers: await csrfHeaders() },
  )
  if (response.status === 409) throw new Error('O convite já foi usado ou expirou.')
  if (response.status !== 204) throw new Error(`Falha ao revogar o convite (HTTP ${response.status}).`)
}

/** Aceita o convite; o token vai somente no corpo do POST. */
export async function acceptInvitation(token: string): Promise<AcceptInvitationResult> {
  const response = await fetch('/api/family-invitations/accept', {
    method: 'POST',
    headers: { ...(await csrfHeaders()), 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  })

  if (response.ok) {
    const body = (await response.json()) as { alreadyMember: boolean; family: FamilyDetails }
    return { kind: 'accepted', alreadyMember: body.alreadyMember, family: body.family }
  }
  if (response.status === 404) return { kind: 'unavailable' }
  if (response.status === 401) return { kind: 'unauthenticated' }
  throw new Error(`Falha ao aceitar o convite (HTTP ${response.status}).`)
}
