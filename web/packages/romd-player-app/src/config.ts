import {
  type EmulatorJsSource,
  PLAYER_PROTOCOL_LIMITS,
  PLAYER_PROTOCOL_VERSION,
} from '@romd/player-protocol';
import emulatorJsPinJson from '../../../tools/emulatorjs/pin.json';

export const PLAYER_CONFIG_SCHEMA_VERSION = 1 as const;
const numericVersionPattern = /^\d+\.\d+\.\d+$/;
const unpinnedPathSegmentPattern = /\/(?:latest|stable)(?:\/|$)/i;

export interface EmulatorJsPin {
  schemaVersion: 2;
  version: string;
  cores: string[];
}

export interface PlayerRuntimeConfig {
  schemaVersion: typeof PLAYER_CONFIG_SCHEMA_VERSION;
  protocol: typeof PLAYER_PROTOCOL_VERSION;
  emulatorJs: {
    version: string;
    source: EmulatorJsSource;
    dataPath: string;
  };
  cores: string[];
  allowedParents: string[];
}

export class PlayerConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'PlayerConfigError';
  }
}

export const emulatorJsPin = parseEmulatorJsPin(emulatorJsPinJson);

export function parseEmulatorJsPin(value: unknown): EmulatorJsPin {
  if (!isRecord(value) || value.schemaVersion !== 2) {
    throw new PlayerConfigError('The EmulatorJS pin must use schema version 2.');
  }

  if (!isNumericVersion(value.version)) {
    throw new PlayerConfigError('The EmulatorJS pin version must be numeric and fully pinned.');
  }

  const cores = parseCoreList(value.cores, 'EmulatorJS pin');
  return { schemaVersion: 2, version: value.version, cores };
}

export function deriveEmulatorJsDataPath(
  pin: EmulatorJsPin,
  source: EmulatorJsSource,
  override?: string,
): string {
  if (source !== 'cdn') {
    throw new PlayerConfigError('Bundled EmulatorJS assets are deferred to Phase 2.');
  }

  if (override !== undefined && override.trim() !== '') {
    return parseDataPath(override.trim());
  }

  return `https://cdn.emulatorjs.org/${pin.version}/data/`;
}

export function parsePlayerRuntimeConfig(
  value: unknown,
  pin: EmulatorJsPin = emulatorJsPin,
): PlayerRuntimeConfig {
  if (!isRecord(value) || value.schemaVersion !== PLAYER_CONFIG_SCHEMA_VERSION) {
    throw new PlayerConfigError('Player config must use schema version 1.');
  }

  if (value.protocol !== PLAYER_PROTOCOL_VERSION) {
    throw new PlayerConfigError('Player config uses an unsupported bridge protocol.');
  }

  if (!isRecord(value.emulatorJs)) {
    throw new PlayerConfigError('Player config must include EmulatorJS settings.');
  }

  const { version, source, dataPath } = value.emulatorJs;
  if (!isNumericVersion(version) || version !== pin.version) {
    throw new PlayerConfigError('Player config must use the numeric version from the EmulatorJS pin.');
  }
  if (source !== 'cdn') {
    throw new PlayerConfigError('Player config must use the CDN source in Phase 1.');
  }

  const parsedDataPath = parseDataPath(dataPath);
  if (isOfficialEmulatorJsCdnPath(parsedDataPath) && parsedDataPath !== deriveEmulatorJsDataPath(pin, 'cdn')) {
    throw new PlayerConfigError('The official EmulatorJS CDN path must use the pinned version.');
  }

  const cores = parseCoreList(value.cores, 'Player config');
  const pinnedCores = new Set(pin.cores);
  if (cores.some((core) => !pinnedCores.has(core))) {
    throw new PlayerConfigError('Player config advertises a core that is not in the pin.');
  }

  if (!Array.isArray(value.allowedParents) || value.allowedParents.length === 0) {
    throw new PlayerConfigError('Player config must allow at least one parent origin.');
  }
  const allowedParents = value.allowedParents.map(parseParentOrigin);
  if (new Set(allowedParents).size !== allowedParents.length) {
    throw new PlayerConfigError('Player config parent origins must be unique.');
  }

  return {
    schemaVersion: PLAYER_CONFIG_SCHEMA_VERSION,
    protocol: PLAYER_PROTOCOL_VERSION,
    emulatorJs: { version, source, dataPath: parsedDataPath },
    cores,
    allowedParents,
  };
}

export async function fetchPlayerRuntimeConfig(
  fetchImplementation: typeof fetch = fetch,
): Promise<PlayerRuntimeConfig> {
  const response = await fetchImplementation('/player-config.json', {
    cache: 'no-store',
    credentials: 'omit',
  });
  if (!response.ok) {
    throw new PlayerConfigError(`Player config request failed with status ${response.status}.`);
  }

  return parsePlayerRuntimeConfig(await response.json());
}

function parseCoreList(value: unknown, label: string): string[] {
  if (
    !Array.isArray(value) ||
    value.length === 0 ||
    value.length > PLAYER_PROTOCOL_LIMITS.coreCount ||
    value.some(
      (core) =>
        typeof core !== 'string' ||
        core.length === 0 ||
        core.length > PLAYER_PROTOCOL_LIMITS.coreLength ||
        !/^[a-z0-9_-]+$/.test(core),
    ) ||
    new Set(value).size !== value.length
  ) {
    throw new PlayerConfigError(`${label} contains an invalid core list.`);
  }
  return [...value];
}

function parseDataPath(value: unknown): string {
  if (typeof value !== 'string' || value.length === 0 || !value.endsWith('/')) {
    throw new PlayerConfigError('EmulatorJS dataPath must be a non-empty path ending in a slash.');
  }
  if (value.includes('\\')) {
    throw new PlayerConfigError('EmulatorJS dataPath may not contain backslashes.');
  }

  if (value.startsWith('/')) {
    if (value.startsWith('//') || value.includes('?') || value.includes('#')) {
      throw new PlayerConfigError('The local EmulatorJS dataPath is invalid.');
    }
    const canonical = new URL(value, 'https://player.invalid');
    if (canonical.pathname !== value) {
      throw new PlayerConfigError('The local EmulatorJS dataPath must already be canonical.');
    }
    validatePinnedPathSegments(canonical.pathname);
    return canonical.pathname;
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new PlayerConfigError('EmulatorJS dataPath must be root-relative or an HTTP(S) URL.');
  }
  if (
    (url.protocol !== 'http:' && url.protocol !== 'https:') ||
    url.username !== '' ||
    url.password !== '' ||
    url.search !== '' ||
    url.hash !== '' ||
    url.href !== value
  ) {
    throw new PlayerConfigError('The absolute EmulatorJS dataPath is invalid.');
  }
  validatePinnedPathSegments(url.pathname);
  return url.href;
}

function validatePinnedPathSegments(pathname: string): void {
  if (unpinnedPathSegmentPattern.test(pathname)) {
    throw new PlayerConfigError('EmulatorJS dataPath may not use latest or stable aliases.');
  }

  for (const segment of pathname.split('/')) {
    let decoded = segment;
    for (let depth = 0; depth < 4; depth += 1) {
      let next: string;
      try {
        next = decodeURIComponent(decoded);
      } catch {
        throw new PlayerConfigError('EmulatorJS dataPath contains invalid percent encoding.');
      }
      if (next.includes('/') || next.includes('\\')) {
        throw new PlayerConfigError('EmulatorJS dataPath contains an encoded path separator.');
      }
      decoded = next;
      if (!decoded.includes('%')) {
        break;
      }
    }

    const normalized = decoded.toLowerCase();
    if (normalized === 'latest' || normalized === 'stable') {
      throw new PlayerConfigError('EmulatorJS dataPath may not use encoded floating aliases.');
    }
    if (normalized === '.' || normalized === '..') {
      throw new PlayerConfigError('EmulatorJS dataPath may not contain encoded dot segments.');
    }
  }
}

function isOfficialEmulatorJsCdnPath(dataPath: string): boolean {
  return !dataPath.startsWith('/') && new URL(dataPath).hostname === 'cdn.emulatorjs.org';
}

function parseParentOrigin(value: unknown): string {
  if (typeof value !== 'string') {
    throw new PlayerConfigError('Player config contains an invalid parent origin.');
  }
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new PlayerConfigError('Player config contains an invalid parent origin.');
  }
  if (
    (url.protocol !== 'http:' && url.protocol !== 'https:') ||
    url.username !== '' ||
    url.password !== '' ||
    url.origin !== value
  ) {
    throw new PlayerConfigError('Allowed parents must be canonical HTTP(S) origins.');
  }
  return value;
}

function isNumericVersion(value: unknown): value is string {
  return typeof value === 'string' && numericVersionPattern.test(value);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
