import { resolve } from 'node:path';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const server = 'http://localhost:5080';

// Two entries: the admin SPA (admin/index.html) and the embed player script (served by the
// server-rendered /embed page, which finds the hashed file through the build manifest).
export default defineConfig({
  plugins: [react()],
  base: '/',
  build: {
    outDir: resolve(import.meta.dirname, '../src/Foxfire.Server/wwwroot'),
    emptyOutDir: true,
    manifest: true,
    assetsDir: 'assets',
    sourcemap: false,
    rollupOptions: {
      input: {
        admin: resolve(import.meta.dirname, 'admin/index.html'),
        embed: resolve(import.meta.dirname, 'src/embed/main.ts'),
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': server,
      '/uploads': server,
      '/stream': server,
      '/embed': server,
      '/poster': server,
    },
  },
});
