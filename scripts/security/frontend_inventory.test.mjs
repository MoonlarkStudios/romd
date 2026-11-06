import assert from 'node:assert/strict';
import test from 'node:test';
import { inventory } from './frontend_inventory.mjs';

const tree = [{ name: 'romd-admin-app', dependencies: {
  '@romd/foundation': { version: 'link:../foundation', dependencies: {
    '@mantine/core': { version: '9.7.2', dependencies: { react: { version: '19.2.3' } } },
  } }, react: { version: '19.2.3' },
}, devDependencies: { vite: { version: '7.3.1' } } }];

test('installed workspace production closure includes transitive packages once and excludes build-only dependencies and local paths', () => {
  const sbom = inventory(tree, 'admin');
  assert.equal(sbom.components.length, 2);
  assert.equal(sbom.components.find((component) => component.name === '@mantine/core').purl, 'pkg:npm/%40mantine/core@9.7.2');
  assert.ok(!JSON.stringify(sbom).includes('vite'));
  assert.ok(!JSON.stringify(sbom).includes('../foundation'));
});

test('empty, mutable, wrong application and unresolved trees cannot produce a release inventory', () => {
  assert.throws(() => inventory([], 'admin'));
  assert.throws(() => inventory(tree, 'consumer'));
  assert.throws(() => inventory([{ name: 'romd-admin-app', dependencies: { react: { version: 'latest' } } }], 'admin'));
  assert.throws(() => inventory([{ name: 'romd-admin-app', dependencies: {} }], 'admin'));
});

test('player distinguishes pinned external EmulatorJS from image-carried frontend modules', () => {
  const playerTree = [{ ...tree[0], name: 'romd-player-app' }];
  assert.throws(() => inventory(playerTree, 'player'));
  const sbom = inventory(playerTree, 'player', { schemaVersion: 2, version: '4.2.3' });
  const runtime = sbom.components.find((component) => component.name === 'EmulatorJS');
  assert.equal(runtime.scope, 'optional');
  assert.equal(runtime.properties[0].value, 'pinned-external-browser-runtime');
});
