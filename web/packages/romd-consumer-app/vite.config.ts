import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const consumerApiOrigin = process.env.VITE_ROMD_CONSUMER_API_ORIGIN ?? 'http://localhost:5000';

export default defineConfig({
  plugins: [
    react(),
  ],
  server: {
    port: 5174,
    strictPort: true,
    // changeOrigin stays false everywhere so the backend always sees the SPA Host. OpenIddict
    // derives its issuer from the request Host, so tokens minted at /connect and validated at /api
    // must agree on one Host, otherwise validation fails with ID2088.
    proxy: {
      '/api': {
        target: consumerApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/artwork': {
        target: consumerApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/media': {
        target: consumerApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/delivery': {
        target: consumerApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/health': {
        target: consumerApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/connect': {
        target: consumerApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/.well-known': {
        target: consumerApiOrigin,
        changeOrigin: false,
        secure: false,
      },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: './src/test/setup.ts',
  },
});
