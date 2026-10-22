import { useState } from 'react'
import type { ResetResult } from '../api/financialReset'
import { Families } from './Families'
import { FinancialReset } from './FinancialReset'

export function WorkspaceSettings({ workspaceId, onReset }: { workspaceId: string; onReset: (result: ResetResult) => void }) {
  const [familiesOpen, setFamiliesOpen] = useState(false)
  return <section className="family-section" aria-labelledby="workspace-settings-title">
    <h3 id="workspace-settings-title">Configurações do orçamento</h3>
    <section className="card"><h4>Orçamento pessoal</h4><p>Seu orçamento é privado e funciona sem criar família, adicionar integrantes ou aceitar convites.</p><p className="meta">Participar de uma família não compartilha seus dados financeiros nem muda o orçamento ativo.</p></section>
    <section className="card" aria-labelledby="optional-family-title"><h4 id="optional-family-title">Família opcional</h4><p>Administre seus núcleos, integrantes e convites apenas quando precisar.</p><button type="button" aria-expanded={familiesOpen} aria-controls="optional-family-management" onClick={() => setFamiliesOpen(value => !value)}>Gerenciar famílias e integrantes</button>
      {familiesOpen && <div id="optional-family-management"><p className="notice">Vínculos familiares não compartilham seu orçamento pessoal.</p><Families /></div>}
    </section>
    <FinancialReset workspaceId={workspaceId} onReset={onReset} />
  </section>
}
