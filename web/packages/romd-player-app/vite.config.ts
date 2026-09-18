import { loadEnv } from 'vite';
import { defineConfig } from 'vitest/config';
import {
  createDevelopmentPlayerConfig,
  developmentPlayerConfigPlugin,
} from './src/devPlayerConfig';

export default defineConfig(({ mode }) => {
  const envDirectory = new URL('../..', import.meta.url).pathname;
  const developmentConfig = createDevelopmentPlayerConfig(
    loadEnv(mode, envDirectory, 'VITE_ROMD_PLAYER_'),
  );

  return {
    plugins: [developmentPlayerConfigPlugin(developmentConfig)],
    resolve: {
      alias: {
        '@romd/player-protocol': new URL('../romd-player-protocol/src/index.ts', import.meta.url)
          .pathname,
      },
    },
    server: {
      port: 5175,
      strictPort: true,
    },
    build: {
      outDir: 'dist',
      emptyOutDir: true,
    },
    test: {
      globals: true,
      environment: 'jsdom',
    },
  };
});
