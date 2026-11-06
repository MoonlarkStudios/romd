import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // Each package owns its environment, setup, and resolution, so a package behaves the same
    // here as under its own `vitest run`.
    projects: [
      'packages/*/{vite,vitest}.config.ts',
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
  },
});
