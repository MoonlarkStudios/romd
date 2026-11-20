import { createReadStream, readdirSync, readFileSync, statSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, extname, join, sep } from 'node:path';
import type { Plugin } from 'vite';

/** The URL prefix the manual viewer passes to pdf.js for its decoders, character maps and standard fonts. */
export const pdfjsAssetBase = '/pdfjs/';

/**
 * pdf.js resolves its image decoders, CMaps and standard fonts by name under directory URLs, so they cannot be
 * content-hashed bundle imports. Serve them in development and emit them at stable paths in the build. Only the
 * decoders are published: the scripting engine (QuickJS) is left out because manuals render without scripting.
 */
export function pdfjsAssets(): Plugin {
  // Resolved from the app that depends on pdf.js, since pnpm does not hoist it to the workspace.
  let root = '';
  const directories = [
    'cmaps',
    'standard_fonts',
    'wasm',
  ];
  const published = (path: string) => !/quickjs/i.test(path);
  const files = () =>
    directories.flatMap((directory) =>
      readdirSync(join(root, directory), {
        recursive: true,
      })
        .map((name) => join(directory, String(name)))
        .filter((path) => statSync(join(root, path)).isFile() && published(path)),
    );
  const types: Record<string, string> = {
    '.wasm': 'application/wasm',
    '.js': 'text/javascript',
    '.bcmap': 'application/octet-stream',
    '.pfb': 'application/octet-stream',
    '.ttf': 'font/ttf',
  };
  return {
    name: 'romd-pdfjs-assets',
    configResolved(config) {
      root = dirname(
        createRequire(join(config.root, 'package.json')).resolve('pdfjs-dist/package.json'),
      );
    },
    configureServer(server) {
      const available = new Set(files().map((path) => path.split(sep).join('/')));
      server.middlewares.use(pdfjsAssetBase, (request, response, next) => {
        const path = decodeURIComponent((request.url ?? '').split('?')[0]).replace(/^\//, '');
        if (!available.has(path)) return next();
        response.setHeader('Content-Type', types[extname(path)] ?? 'application/octet-stream');
        createReadStream(join(root, path)).pipe(response);
      });
    },
    generateBundle() {
      for (const path of files())
        this.emitFile({
          type: 'asset',
          fileName: `${pdfjsAssetBase.slice(1)}${path.split(sep).join('/')}`,
          source: readFileSync(join(root, path)),
        });
    },
  };
}
