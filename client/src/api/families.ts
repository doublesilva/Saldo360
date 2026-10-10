import { CSRF_HEADER, getCsrfToken } from './csrf'

export type FamilyRole = 'Owner' | 'Member'

export const FAMILY_NAME_MAX_LENGTH = 100

export interface FamilySummary {
  id: string
  name: string
  createdAt: string
  myRole: FamilyRole
  memberCount: number
}

export interface FamilyMember {
  /** Id do vínculo familiar (não é identidade de autenticação). */
  memberId: string
  displayName: string
  role: FamilyRole
  joinedAt: string
  isCurrentUser: boolean
}

export interface FamilyDetails {
  id: string
  name: string
  createdAt: string
  myRole: FamilyRole
  members: FamilyMember[]
}

export type CreateFamilyResult =
  | { ok: true; family: FamilyDetails }
  | { ok: false; errors: Record<string, string[]> }

const jsonHeaders = { Accept: 'application/json' }

export async function listFamilies(signal?: AbortSignal): Promise<FamilySummary[]> {
  const response = await fetch('/api/families', { signal, headers: jsonHeaders })
  if (!response.ok) throw new Error(`Falha ao carregar as famílias (HTTP ${response.status}).`)
  return (await response.json()) as FamilySummary[]
}

/** A família com membros, ou null se não existir ou o usuário não fizer parte dela (404). */
export async function getFamily(id: string, signal?: AbortSignal): Promise<FamilyDetails | null> {
  const response = await fetch(`/api/families/${encodeURIComponent(id)}`, { signal, headers: jsonHeaders })
  if (response.status === 404) return null
  if (!response.ok) throw new Error(`Falha ao carregar a família (HTTP ${response.status}).`)
  return (await response.json()) as FamilyDetails
}

/** Cria a família; o criador vem da sessão (só o nome é enviado). */
export async function createFamily(name: string): Promise<CreateFamilyResult> {
  const token = await getCsrfToken()
  const response = await fetch('/api/families', {
    method: 'POST',
    headers: { ...jsonHeaders, 'Content-Type': 'application/json', [CSRF_HEADER]: token },
    body: JSON.stringify({ name }),
  })

  if (response.status === 201) return { ok: true, family: (await response.json()) as FamilyDetails }
  if (response.status === 400) {
    const problem = (await response.json().catch(() => ({}))) as { errors?: Record<string, string[]> }
    if (problem.errors) return { ok: false, errors: problem.errors }
  }
  throw new Error(`Falha ao criar a família (HTTP ${response.status}).`)
}
