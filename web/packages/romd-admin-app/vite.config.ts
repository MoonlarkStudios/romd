import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { portalTheme } from '../../config/portalTheme';
import { vendorChunks } from '../../config/vendorChunks';

const adminApiOrigin = process.env.VITE_ROMD_ADMIN_API_ORIGIN ?? 'http://localhost:5000';

export default defineConfig({
  plugins: [
    portalTheme(),
    react(),
  ],
  server: {
    port: 5137,
    strictPort: true,
    // changeOrigin stays false everywhere so the backend always sees the SPA Host. OpenIddict
    // derives its issuer from the request Host, so tokens minted at /connect and validated at
    // /api and /hubs must agree on one Host, otherwise validation fails with ID2088.
    proxy: {
      '/api': {
        target: adminApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/artwork': {
        target: adminApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/media': {
        target: adminApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/health': {
        target: adminApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/hubs': {
        target: adminApiOrigin,
        changeOrigin: false,
        secure: false,
        ws: true,
      },
      '/connect': {
        target: adminApiOrigin,
        changeOrigin: false,
        secure: false,
      },
      '/.well-known': {
        target: adminApiOrigin,
        changeOrigin: false,
        secure: false,
      },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks: vendorChunks,
      },
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    include: [
      'src/**/*.test.{ts,tsx}',
    ],
    setupFiles: './src/test/setup.ts',
    testTimeout: 10000,
  },
});
