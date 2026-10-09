import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  // Sem prefixo VITE_: a variável fica só no servidor de desenvolvimento, nunca no bundle.
  const env = loadEnv(mode, process.cwd(), '')
  const apiTarget = env.API_PROXY_TARGET || 'http://localhost:5080'

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/health': { target: apiTarget, changeOrigin: true },
        '/api': { target: apiTarget, changeOrigin: true },
      },
    },
  }
})
