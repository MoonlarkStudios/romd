import { readFileSync } from 'node:fs';
import type { Plugin } from 'vite';

/** Inline the same first-paint theme resources that the hosts embed for /connect. */
export function portalTheme(): Plugin {
  return {
    name: 'romd-portal-theme',
    transformIndexHtml: {
      order: 'pre',
      handler: () => [
        {
          tag: 'script',
          children: readFileSync(
            new URL('../packages/romd-foundation/src/portal-theme.js', import.meta.url),
            'utf8',
          ),
          injectTo: 'head-prepend',
        },
        {
          tag: 'style',
          children: readFileSync(
            new URL('../packages/romd-foundation/src/portal-theme.css', import.meta.url),
            'utf8',
          ),
          injectTo: 'head-prepend',
        },
      ],
    },
  };
}
