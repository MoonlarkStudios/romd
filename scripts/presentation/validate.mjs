import { readFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const source = resolve(root, 'reference-data/assets/presentation');
const systems = JSON.parse(await readFile(resolve(root, 'reference-data/catalog/systems.json'), 'utf8'));
const canonicalRatings = JSON.parse(await readFile(resolve(root, 'reference-data/catalog/ratings.json'), 'utf8'));
const catalog = {
  schemaVersion: 1,
  platforms: Object.entries(systems).map(([key, row]) => ({ key, label: row.compactLabel, asset: row.iconPath, tintable: row.monochrome })),
  ratings: canonicalRatings.ratings.map(row => ({ board: row.board, code: row.code, label: row.name, asset: row.iconPath })),
};
const fail = message => { throw new Error(message); };
const unique = (items, key) => {
  const seen = new Set();
  for (const item of items) { const value = key(item); if (seen.has(value)) fail(`Duplicate key: ${value}`); seen.add(value); }
};
if (catalog.schemaVersion !== 1 || !Array.isArray(catalog.platforms) || !Array.isArray(catalog.ratings)) fail('Unsupported presentation catalog');
unique(catalog.platforms, item => item.key);
unique(catalog.ratings, item => `${item.board}:${item.code}`);
const asset = async path => {
  if (typeof path !== 'string' || !/^(platforms|ratings)\/[A-Za-z0-9_+./-]+\.(png|svg)$/.test(path) || path.split('/').includes('..')) fail(`Invalid asset path: ${path}`);
  return readFile(resolve(source, path));
};
for (const item of catalog.platforms) {
  if (!Object.hasOwn(systems, item.key) || typeof item.label !== 'string' || !item.label.trim() || typeof item.tintable !== 'boolean') fail(`Invalid platform: ${item.key}`);
  if (item.asset !== null) await asset(item.asset);
}
for (const item of catalog.ratings) {
  if (!canonicalRatings.boards.some(board => board.key === item.board) || typeof item.code !== 'string' || !item.code || typeof item.label !== 'string' || !item.label) fail(`Invalid rating: ${item.board}:${item.code}`);
  await asset(item.asset);
}
console.log(`Validated server artwork: ${catalog.platforms.length} systems, ${catalog.ratings.length} ratings.`);
