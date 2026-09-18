export const PLAYER_PROTOCOL_VERSION = 1 as const;

export const PLAYER_PROTOCOL_LIMITS = {
  romBytes: 64 * 1024 * 1024,
  coreLength: 64,
  displayNameLength: 256,
  relativePathLength: 1024,
  versionLength: 64,
  errorCodeLength: 64,
  errorMessageLength: 1024,
  coreCount: 64,
} as const;

export const PLAYER_EVENT_NAMES = [
  'ready',
  'game-started',
  'controls-ready',
  'controls-unavailable',
  'paused',
  'resumed',
  'save-updated',
  'exit',
  'error',
] as const;

export type PlayerEventName = (typeof PLAYER_EVENT_NAMES)[number];
export type EmulatorJsSource = 'cdn' | 'bundled';

export interface PlayerInitMessage {
  type: 'init';
  protocol: typeof PLAYER_PROTOCOL_VERSION;
}

export interface PlayerLoadMessage {
  type: 'load';
  core: string;
  displayName: string;
  contentSha256: string;
  relativePath: string;
  rom: Blob;
}

export interface PlayerShutdownMessage {
  type: 'shutdown';
}

export type PlayerControlAction = 'pause' | 'resume' | 'open-controls';
export type ParentToPlayerMessage = PlayerLoadMessage | PlayerShutdownMessage
  | { type: 'enable-controls' }
  | { type: 'control'; action: PlayerControlAction };

export interface PlayerHelloMessage {
  features?: string[];
  type: 'hello';
  protocol: typeof PLAYER_PROTOCOL_VERSION;
  emulatorJs: {
    version: string;
    source: EmulatorJsSource;
  };
  cores: string[];
}

export interface PlayerLifecycleEventMessage {
  type: 'event';
  event: Exclude<PlayerEventName, 'error'>;
}

export interface PlayerErrorEventMessage {
  type: 'event';
  event: 'error';
  code: string;
  message: string;
}

export type PlayerEventMessage = PlayerLifecycleEventMessage | PlayerErrorEventMessage;
export type PlayerToParentMessage = PlayerHelloMessage | PlayerEventMessage;

const eventNames = new Set<string>(PLAYER_EVENT_NAMES);

export function parsePlayerInitMessage(value: unknown): PlayerInitMessage | null {
  if (!isRecord(value) || value.type !== 'init' || value.protocol !== PLAYER_PROTOCOL_VERSION) {
    return null;
  }
  return { type: 'init', protocol: PLAYER_PROTOCOL_VERSION };
}

export function parseParentToPlayerMessage(value: unknown): ParentToPlayerMessage | null {
  if (!isRecord(value) || typeof value.type !== 'string') {
    return null;
  }

  if (value.type === 'enable-controls') return { type: 'enable-controls' };
  if (value.type === 'control') {
    return value.action === 'pause' || value.action === 'resume' || value.action === 'open-controls'
      ? { type: 'control', action: value.action } : null;
  }

  if (value.type === 'shutdown') {
    return { type: 'shutdown' };
  }

  if (
    value.type !== 'load' ||
    !isBoundedString(value.core, 1, PLAYER_PROTOCOL_LIMITS.coreLength) ||
    !isBoundedString(value.displayName, 1, PLAYER_PROTOCOL_LIMITS.displayNameLength) ||
    !isSha256(value.contentSha256) ||
    !isBoundedString(value.relativePath, 1, PLAYER_PROTOCOL_LIMITS.relativePathLength) ||
    !(value.rom instanceof Blob) ||
    value.rom.size > PLAYER_PROTOCOL_LIMITS.romBytes
  ) {
    return null;
  }

  return {
    type: 'load',
    core: value.core,
    displayName: value.displayName,
    contentSha256: value.contentSha256,
    relativePath: value.relativePath,
    rom: value.rom,
  };
}

export function parsePlayerToParentMessage(value: unknown): PlayerToParentMessage | null {
  if (!isRecord(value) || typeof value.type !== 'string') {
    return null;
  }

  if (value.type === 'hello') {
    if (
      value.protocol !== PLAYER_PROTOCOL_VERSION ||
      !isRecord(value.emulatorJs) ||
      !isBoundedString(value.emulatorJs.version, 1, PLAYER_PROTOCOL_LIMITS.versionLength) ||
      !isEmulatorJsSource(value.emulatorJs.source) ||
      !isCoreList(value.cores) ||
      (value.features !== undefined && (!Array.isArray(value.features) || value.features.length > 16 || !value.features.every(feature => isBoundedString(feature, 1, 64))))
    ) {
      return null;
    }

    return {
      type: 'hello',
      protocol: PLAYER_PROTOCOL_VERSION,
      emulatorJs: {
        version: value.emulatorJs.version,
        source: value.emulatorJs.source,
      },
      cores: [...value.cores],
      ...(value.features === undefined ? {} : { features: [...value.features as string[]] }),
    };
  }

  if (value.type !== 'event' || typeof value.event !== 'string' || !eventNames.has(value.event)) {
    return null;
  }

  if (value.event === 'error') {
    if (
      !isBoundedString(value.code, 1, PLAYER_PROTOCOL_LIMITS.errorCodeLength) ||
      !isBoundedString(value.message, 1, PLAYER_PROTOCOL_LIMITS.errorMessageLength)
    ) {
      return null;
    }
    return { type: 'event', event: 'error', code: value.code, message: value.message };
  }

  return { type: 'event', event: value.event as PlayerLifecycleEventMessage['event'] };
}

export function isPlayerProtocolVersion(value: unknown): value is typeof PLAYER_PROTOCOL_VERSION {
  return value === PLAYER_PROTOCOL_VERSION;
}

function isCoreList(value: unknown): value is string[] {
  return (
    Array.isArray(value) &&
    value.length > 0 &&
    value.length <= PLAYER_PROTOCOL_LIMITS.coreCount &&
    new Set(value).size === value.length &&
    value.every((core) => isBoundedString(core, 1, PLAYER_PROTOCOL_LIMITS.coreLength))
  );
}

function isEmulatorJsSource(value: unknown): value is EmulatorJsSource {
  return value === 'cdn' || value === 'bundled';
}

function isSha256(value: unknown): value is string {
  return typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
}

function isBoundedString(value: unknown, minimum: number, maximum: number): value is string {
  return typeof value === 'string' && value.length >= minimum && value.length <= maximum;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
