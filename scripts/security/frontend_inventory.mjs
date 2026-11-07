import { readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

export function inventory(tree, application, emulatorPin) {
  if (!['admin', 'consumer', 'player'].includes(application) || !Array.isArray(tree) || tree.length !== 1
      || tree[0].name !== `romd-${application}-app`) throw new Error('Expected one matching installed application dependency tree');
  const components = new Map();
  const visit = (dependencies) => {
    for (const [name, dependency] of Object.entries(dependencies ?? {})) {
      const version = dependency.version;
      if (typeof version !== 'string') throw new Error('Installed dependency version is missing');
      if (!version.startsWith('link:')) {
        if (!/^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.+-]+)?$/.test(version)) throw new Error('Installed dependency version is not immutable');
        const purl = `pkg:npm/${name.split('/').map(encodeURIComponent).join('/')}@${version}`;
        components.set(purl, { type: 'library', name, version, purl, 'bom-ref': purl,
          properties: [{ name: 'romd:inventory-source', value: 'installed-production-dependency-closure' }] });
      }
      visit(dependency.dependencies);
      visit(dependency.optionalDependencies);
    }
  };
  visit(tree[0].dependencies);
  visit(tree[0].optionalDependencies);
  if (!components.size) throw new Error('Installed production inventory is empty');
  if (application === 'player') {
    if (emulatorPin?.schemaVersion !== 2 || !/^[0-9]+\.[0-9]+\.[0-9]+$/.test(emulatorPin.version)) throw new Error('Player requires its pinned external runtime identity');
    const purl = `pkg:github/EmulatorJS/EmulatorJS@${emulatorPin.version}`;
    components.set(purl, { type: 'application', name: 'EmulatorJS', version: emulatorPin.version,
      purl, 'bom-ref': purl, scope: 'optional',
      properties: [{ name: 'romd:inventory-source', value: 'pinned-external-browser-runtime' }] });
  }
  return { bomFormat: 'CycloneDX', specVersion: '1.6', version: 1,
    metadata: { component: { type: 'application', name: `romd-${application}-frontend` } },
    components: [...components.values()].sort((left, right) => left.purl.localeCompare(right.purl)) };
}

async function main() {
  const values = Object.fromEntries(Array.from({ length: (process.argv.length - 2) / 2 }, (_, index) => process.argv.slice(2 + index * 2, 4 + index * 2)));
  if (!values['--input'] || !values['--output'] || !values['--application']) throw new Error('usage: node frontend_inventory.mjs --input <pnpm-list.json> --application admin|consumer|player --output <frontend.cdx.json> [--emulatorjs-pin <pin.json>]');
  const tree = JSON.parse(await readFile(values['--input'], 'utf8'));
  const pin = values['--emulatorjs-pin'] ? JSON.parse(await readFile(values['--emulatorjs-pin'], 'utf8')) : undefined;
  await writeFile(values['--output'], JSON.stringify(inventory(tree, values['--application'], pin), null, 2) + '\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
