import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { captureInviteToken } from './invite'

// Antes de qualquer renderização: guarda o token do convite e tira o fragmento da URL.
captureInviteToken()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
