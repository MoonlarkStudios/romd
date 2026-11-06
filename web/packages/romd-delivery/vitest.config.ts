import { defineProject } from 'vitest/config';

export default defineProject({
  test: {
    globals: true,
    environment: 'jsdom',
    include: [
      'src/**/*.test.ts',
    ],
    testTimeout: 10000,
  },
});
