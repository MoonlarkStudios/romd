import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Plugin } from 'vite';

/** Inline the shared first-paint resources and any SPA-specific palette. */
export function portalTheme(palette?: 'admin') {
  const foundation = join(import.meta.dirname, '../packages/romd-foundation/src');

  return {
    name: 'romd-portal-theme',
    transformIndexHtml: {
      order: 'pre',
      handler: () => [
        {
          tag: 'script',
          children: readFileSync(join(foundation, 'portal-theme.js'), 'utf8'),
          injectTo: 'head-prepend',
        },
        {
          tag: 'style',
          children: [
            readFileSync(join(foundation, 'portal-theme.css'), 'utf8'),
            palette === 'admin' ? readFileSync(join(foundation, 'admin-theme.css'), 'utf8') : '',
          ].join('\n'),
          injectTo: 'head-prepend',
        },
      ],
    },
  } satisfies Plugin;
}
