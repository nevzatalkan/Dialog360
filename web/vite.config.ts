import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Geliştirmede /api istekleri .NET API'ye (5080) yönlenir; build çıktısı API'nin wwwroot'una yazılır.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { port: 5173, proxy: { '/api': 'http://localhost:5080' } },
  build: { outDir: '../api/wwwroot', emptyOutDir: true, chunkSizeWarningLimit: 1000 },
})
