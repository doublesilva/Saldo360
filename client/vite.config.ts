import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  // Sem prefixo VITE_: a variável fica só no servidor de desenvolvimento, nunca no bundle.
  const env = loadEnv(mode, process.cwd(), '')
  const apiTarget = env.API_PROXY_TARGET || 'http://localhost:5080'

  // changeOrigin: false mantém o Host do navegador (localhost:5173). Assim a API monta o
  // redirect_uri do OIDC (http://localhost:5173/signin-oidc) e os cookies no mesmo site do frontend.
  const toApi = { target: apiTarget, changeOrigin: false }

  return {
    plugins: [react()],
    server: {
      // IPv4 explícito: com "localhost" o Node escuta só em ::1, e clientes que resolvem
      // localhost para 127.0.0.1 (ex.: curl do Git Bash) não conectam.
      host: '127.0.0.1',
      port: 5173,
      strictPort: true,
      proxy: {
        '/health': toApi,
        '/api': toApi,
        '/auth': toApi,
        '/signin-oidc': toApi,
      },
    },
  }
})
