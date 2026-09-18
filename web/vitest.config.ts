import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  resolve: { alias: [{ find: /^@romd\/consumer-ui$/, replacement: fileURLToPath(new URL('./packages/romd-consumer-ui/src/index.ts', import.meta.url)) }] },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./packages/romd-admin-app/src/test/setup.ts'],
    include: [
      'packages/**/*.{test,spec}.{ts,tsx}',
    ],
    exclude: [
      '**/node_modules/**',
      '**/dist/**',
    ],
    coverage: {
      provider: 'v8',
      reporter: [
        'text',
        'html',
        'lcov',
      ],
      exclude: [
        'node_modules/',
        '**/*.config.*',
        '**/*.d.ts',
        '**/types/**',
        '**/__mocks__/**',
      ],
    },
    testTimeout: 10000,
  },
});
