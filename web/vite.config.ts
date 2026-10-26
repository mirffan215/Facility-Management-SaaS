/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  // Built files go to /static/, because /assets/<id> is a page of the app (and a QR label's landing address).
  build: { assetsDir: 'static' },
  server: { port: 5173, proxy: { '/api': 'http://localhost:5080', '/health': 'http://localhost:5080' } },
  preview: { port: 5173, proxy: { '/api': 'http://localhost:5080', '/health': 'http://localhost:5080' } },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
  },
});
