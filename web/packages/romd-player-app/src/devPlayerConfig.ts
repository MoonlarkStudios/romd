import type { Connect, Plugin } from 'vite';
import emulatorJsPinJson from '../../../tools/emulatorjs/pin.json';

interface PlayerDevelopmentConfig {
  schemaVersion: 1;
  protocol: 1;
  emulatorJs: {
    version: string;
    source: 'cdn';
    dataPath: string;
  };
  cores: string[];
  allowedParents: string[];
}

type PlayerDevelopmentEnv = Record<string, string | undefined>;

const pin = validatePin(emulatorJsPinJson);
const officialDataPath = `https://cdn.emulatorjs.org/${pin.version}/data/`;

export function createDevelopmentPlayerConfig(
  env: PlayerDevelopmentEnv,
): PlayerDevelopmentConfig {
  const source = env.VITE_ROMD_PLAYER_EMULATORJS_SOURCE?.trim() || 'cdn';
  if (source !== 'cdn') {
    throw new Error('VITE_ROMD_PLAYER_EMULATORJS_SOURCE must be cdn.');
  }

  const dataPath = validateDataPath(
    env.VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH?.trim() || officialDataPath,
  );
  const configuredCores = splitList(env.VITE_ROMD_PLAYER_CORES, pin.cores);
  const pinnedCores = new Set(pin.cores);
  if (configuredCores.some((core) => !pinnedCores.has(core))) {
    throw new Error('VITE_ROMD_PLAYER_CORES contains a core outside the EmulatorJS pin.');
  }

  const allowedParents = splitList(env.VITE_ROMD_PLAYER_ALLOWED_PARENTS, [
    'http://localhost:5174',
  ]).map(validateParentOrigin);

  return {
    schemaVersion: 1,
    protocol: 1,
    emulatorJs: { version: pin.version, source: 'cdn', dataPath },
    cores: configuredCores,
    allowedParents,
  };
}

export function createDevelopmentPlayerConfigMiddleware(
  config: PlayerDevelopmentConfig,
): Connect.NextHandleFunction {
  const body = JSON.stringify(config);
  return (request, response, next) => {
    if (request.originalUrl?.split('?', 1)[0] !== '/player-config.json') {
      next();
      return;
    }

    response.statusCode = 200;
    response.setHeader('Content-Type', 'application/json; charset=utf-8');
    response.setHeader('Cache-Control', 'no-store');
    response.setHeader('Access-Control-Allow-Origin', '*');
    response.end(body);
  };
}

export function developmentPlayerConfigPlugin(config: PlayerDevelopmentConfig): Plugin {
  return {
    name: 'romd-player-development-config',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use(createDevelopmentPlayerConfigMiddleware(config));
    },
  };
}

function validatePin(value: unknown): { version: string; cores: string[] } {
  if (
    typeof value !== 'object' ||
    value === null ||
    !('schemaVersion' in value) ||
    value.schemaVersion !== 2 ||
    !('version' in value) ||
    typeof value.version !== 'string' ||
    !/^\d+\.\d+\.\d+$/.test(value.version) ||
    !('cores' in value) ||
    !Array.isArray(value.cores) ||
    value.cores.length === 0 ||
    value.cores.some((core) => typeof core !== 'string')
  ) {
    throw new Error('The Vite player requires the schema-v2 numeric EmulatorJS pin.');
  }

  return { version: value.version, cores: [...value.cores] };
}

function validateDataPath(value: string): string {
  if (!value.endsWith('/') || value.includes('\\')) {
    throw new Error('VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH must end in / and contain no backslashes.');
  }

  if (value.startsWith('/')) {
    if (value.startsWith('//') || new URL(value, 'https://player.invalid').pathname !== value) {
      throw new Error('VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH contains an invalid local path.');
    }
    rejectFloatingSegments(value);
    return value;
  }

  const url = new URL(value);
  if (
    (url.protocol !== 'http:' && url.protocol !== 'https:') ||
    url.username !== '' ||
    url.password !== '' ||
    url.search !== '' ||
    url.hash !== '' ||
    url.href !== value
  ) {
    throw new Error('VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH must be a canonical HTTP(S) directory.');
  }
  rejectFloatingSegments(url.pathname);
  if (url.hostname === 'cdn.emulatorjs.org' && value !== officialDataPath) {
    throw new Error('The official EmulatorJS CDN data path must use the pinned version.');
  }
  return value;
}

function rejectFloatingSegments(path: string): void {
  for (const segment of path.split('/')) {
    let decoded = segment;
    for (let depth = 0; depth < 4; depth += 1) {
      let next: string;
      try {
        next = decodeURIComponent(decoded);
      } catch {
        throw new Error('The EmulatorJS data path contains invalid percent encoding.');
      }
      if (next.includes('/') || next.includes('\\')) {
        throw new Error('The EmulatorJS data path contains an encoded separator.');
      }
      decoded = next;
      if (!decoded.includes('%')) {
        break;
      }
    }

    const normalized = decoded.toLowerCase();
    if (
      normalized === 'latest' ||
      normalized === 'stable' ||
      normalized === '.' ||
      normalized === '..'
    ) {
      throw new Error('The EmulatorJS data path must be pinned and canonical.');
    }
  }
}

function splitList(value: string | undefined, defaults: string[]): string[] {
  if (value === undefined || value.trim() === '') {
    return [...defaults];
  }
  const entries = value.split(',').map((entry) => entry.trim());
  if (entries.some((entry) => entry === '') || new Set(entries).size !== entries.length) {
    throw new Error('Player development lists must contain unique, non-empty entries.');
  }
  return entries;
}

function validateParentOrigin(value: string): string {
  const url = new URL(value);
  if (
    (url.protocol !== 'http:' && url.protocol !== 'https:') ||
    url.username !== '' ||
    url.password !== '' ||
    url.origin !== value
  ) {
    throw new Error('VITE_ROMD_PLAYER_ALLOWED_PARENTS must contain canonical HTTP(S) origins.');
  }
  return value;
}
