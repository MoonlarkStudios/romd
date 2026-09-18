import { readFile, writeFile } from 'node:fs/promises';

const [pinPath, outputPath] = process.argv.slice(2);

if (!pinPath || !outputPath) {
  throw new Error('usage: write-defaults.mjs <pin.json> <output.env>');
}

const pin = JSON.parse(await readFile(pinPath, 'utf8'));

if (pin.schemaVersion !== 2) {
  throw new Error(`unsupported EmulatorJS pin schema: ${pin.schemaVersion}`);
}

if (!/^\d+\.\d+\.\d+$/.test(pin.version) || /(?:latest|stable)/i.test(pin.version)) {
  throw new Error(`invalid pinned EmulatorJS version: ${pin.version}`);
}

if (
  !Array.isArray(pin.cores) ||
  pin.cores.length === 0 ||
  pin.cores.some((core) => typeof core !== 'string' || !/^[a-z0-9][a-z0-9_-]*$/.test(core)) ||
  new Set(pin.cores).size !== pin.cores.length
) {
  throw new Error('the EmulatorJS pin must contain a non-empty, unique core list');
}

await writeFile(
  outputPath,
  `ROMD_PLAYER_PIN_VERSION='${pin.version}'\nROMD_PLAYER_PIN_CORES='${pin.cores.join(',')}'\n`,
  'utf8',
);
