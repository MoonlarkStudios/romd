import { defineProject } from 'vitest/config';

export default defineProject({
  test: {
    globals: true,
    environment: 'jsdom',
    include: [
      'tests/**/*.test.ts',
    ],
    testTimeout: 10000,
  },
});
