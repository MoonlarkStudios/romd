import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { cp, mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));

async function files(directory, relative = '') {
  const result = [];
  for (const entry of await readdir(path.join(directory, relative), {
    withFileTypes: true,
  })) {
    const name = path.join(relative, entry.name);
    if (entry.isDirectory()) result.push(...(await files(directory, name)));
    else result.push(name);
  }
  return result.sort();
}

test('the copied ROMD kit contains canonical files and a minimal source record', async () => {
  const kit = path.join(root, 'brand/romd');
  assert.deepEqual(
    (await readdir(kit)).sort(),
    [
      'NOTICES.md',
      'SOURCE.md',
      'assets',
      'fonts',
      'fonts.css',
      'tokens.css',
      'tokens.dart',
      'tokens.js',
      'tokens.json',
      'tokens.rs',
      'tokens.ts',
    ].sort(),
  );
  assert.match(
    await readFile(path.join(kit, 'SOURCE.md'), 'utf8'),
    /^Version: \d+\.\d+\.\d+\nCommit: [a-f0-9]{40}\n$/,
  );
  for (const name of await files(kit)) {
    if (!/\.(svg|json|css|js|ts|dart|rs|md|txt)$/.test(name)) continue;
    assert.doesNotMatch(
      await readFile(path.join(kit, name), 'utf8'),
      /moonlark-branding|\bsquish\b/i,
      name,
    );
  }
});

test('web placements reproduce from the copied ROMD kit without a branding checkout', async () => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'romd-web-brand-'));
  try {
    // Deliberately omit legacy root assets, the private source checkout, and node_modules.
    for (const name of [
      'brand/romd',
      'brand/tools/export_web_brand.sh',
    ]) {
      await cp(path.join(root, name), path.join(temporary, name), {
        recursive: true,
      });
    }
    const place = () =>
      execFileSync(
        'bash',
        [
          'brand/tools/export_web_brand.sh',
        ],
        {
          cwd: temporary,
        },
      );
    place();
    const generated = await files(path.join(temporary, 'web'));
    assert.ok(generated.includes('packages/romd-foundation/src/brand/tokens.css'));
    assert.deepEqual(
      await files(path.join(temporary, 'web/packages/romd-foundation/src/brand')),
      await files(path.join(root, 'web/packages/romd-foundation/src/brand')),
    );
    for (const app of [
      'admin',
      'consumer',
      'player',
    ]) {
      assert.deepEqual(
        await readFile(path.join(temporary, `web/packages/romd-${app}-app/public/favicon.svg`)),
        await readFile(path.join(root, 'brand/romd/assets/icons/standard/icon.svg')),
      );
    }
    for (const name of generated) {
      const expected = await readFile(path.join(root, 'web', name));
      assert.deepEqual(
        await readFile(path.join(temporary, 'web', name)),
        expected,
        `Stale placement: ${name}`,
      );
    }
    for (const family of [
      'archivo',
      'ibm-plex-mono',
    ]) {
      const source = path.join(root, 'brand/romd/fonts', family);
      for (const name of await files(source)) {
        assert.deepEqual(
          await readFile(path.join(temporary, 'web/packages/romd-foundation/src/fonts', name)),
          await readFile(path.join(source, name)),
        );
      }
    }
    place();
    for (const name of generated) {
      assert.deepEqual(
        await readFile(path.join(temporary, 'web', name)),
        await readFile(path.join(root, 'web', name)),
      );
    }
  } finally {
    await rm(temporary, {
      recursive: true,
      force: true,
    });
  }
});
