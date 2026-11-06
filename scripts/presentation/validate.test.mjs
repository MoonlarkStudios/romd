import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, cpSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
function fixture(t) {
  const dir = mkdtempSync(resolve(tmpdir(), 'romd-presentation-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  for (const path of ['reference-data/assets/presentation', 'scripts/presentation', 'reference-data/catalog']) {
    mkdirSync(dirname(resolve(dir, path)), { recursive: true });
    cpSync(resolve(root, path), resolve(dir, path), { recursive: true });
  }
  const manifest = resolve(dir, 'reference-data/catalog/ratings.json');
  const systemsPath = resolve(dir, 'reference-data/catalog/systems.json');
  const catalog = JSON.parse(readFileSync(manifest, 'utf8'));
  catalog.systems = JSON.parse(readFileSync(systemsPath, 'utf8'));
  return { dir, catalog, save: () => { const { systems, ...ratings } = catalog; writeFileSync(manifest, JSON.stringify(ratings)); writeFileSync(systemsPath, JSON.stringify(systems)); }, run: (...args) => spawnSync(process.execPath, [resolve(dir, 'scripts/presentation/validate.mjs'), ...args], { encoding: 'utf8' }) };
}
test('validates server assets without generating client copies', t => {
  const f = fixture(t);
  assert.equal(f.run().status, 0);
  assert.equal(existsSync(resolve(f.dir, 'web')), false);
  assert.equal(existsSync(resolve(f.dir, 'clients')), false);
});
for (const [name, mutate] of [
  ['missing platform label', c => { Object.values(c.systems)[0].compactLabel = ''; }],
  ['unknown rating board', c => { c.ratings[0].board = 'invented'; }],
  ['duplicate rating', c => c.ratings.push(c.ratings[0])],
  ['missing asset', c => { c.ratings[0].iconPath = 'ratings/missing.svg'; }],
  ['asset outside catalog', c => { c.ratings[0].iconPath = 'ratings/../../secret.svg'; }],
  ['unsupported artwork type', c => { c.ratings[0].iconPath = 'ratings/icon.html'; }],
]) test(`rejects ${name}`, t => {
  const f = fixture(t); mutate(f.catalog); f.save(); assert.notEqual(f.run().status, 0);
});
