import { getConsumerPlaybackConfig } from '@romd/consumer-api-client';
import {
  PLAYER_PROTOCOL_VERSION,
  type PlayerControlAction,
  type PlayerEventMessage,
  type PlayerHelloMessage,
  parsePlayerToParentMessage,
} from '@romd/player-protocol';
import type { EmulatorJsCore } from './browserPlayback';
import type { VerifiedDownload } from './downloadVerification';

const frameLoadTimeoutMs = 10_000;
const handshakeTimeoutMs = 5_000;
const emulatorLoadTimeoutMs = 30_000;
const shutdownTimeoutMs = 3_000;

export interface PlayerCapability {
  playerOrigin: string;
  emulatorJs: {
    version: string;
    source: 'cdn' | 'bundled';
    dataPath: string;
  };
  cores: string[];
}

export type PlayerCapabilityResult =
  | { status: 'available'; capability: PlayerCapability }
  | { status: 'unavailable'; reason: string };

export interface PlayerBridgeLoad {
  core: EmulatorJsCore;
  displayName: string;
  verifiedFile: Pick<VerifiedDownload, 'blob' | 'sha256' | 'relativePath'>;
}

export interface PlayerBridge {
  control: (action: PlayerControlAction) => void;
  ready: Promise<PlayerHelloMessage>;
  shutdown: () => Promise<void>;
  dispose: () => void;
}

interface PlayerRuntimeConfig {
  schemaVersion: 1;
  protocol: 1;
  emulatorJs: {
    version: string;
    source: 'cdn' | 'bundled';
    dataPath: string;
  };
  cores: string[];
  allowedParents: string[];
}

export class PlayerBridgeError extends Error {
  constructor(
    message: string,
    readonly code: string,
  ) {
    super(message);
    this.name = 'PlayerBridgeError';
  }
}

export async function fetchPlayerCapability(signal?: AbortSignal): Promise<PlayerCapabilityResult> {
  const originResponse = await getConsumerPlaybackConfig({
    baseUrl: window.location.origin,
    signal,
  });
  if (originResponse.error || !originResponse.data) {
    return unavailable('Browser playback configuration could not be loaded.');
  }

  const originConfig = parseOriginConfig(originResponse.data);
  if (!originConfig) {
    return unavailable('Browser playback is not configured for this portal origin.');
  }

  const playerUrl = new URL(originConfig.playerOrigin);
  if (playerUrl.origin === window.location.origin) {
    return unavailable(
      'Browser playback requires a dedicated origin and refuses this same-origin configuration.',
    );
  }
  if (!isLikelySameSite(window.location.hostname, playerUrl.hostname)) {
    console.warn(
      `ROMD player origin ${playerUrl.origin} does not appear to be same-site with ` +
        `${window.location.origin}; persistent saves may be partitioned.`,
    );
  }

  let configResponse: Response;
  try {
    configResponse = await fetch(new URL('/player-config.json', playerUrl.origin), {
      cache: 'no-store',
      credentials: 'omit',
      mode: 'cors',
      referrerPolicy: 'no-referrer',
      signal,
    });
  } catch {
    return unavailable('The dedicated browser player could not be reached.');
  }

  if (!configResponse.ok) {
    return unavailable('The dedicated browser player did not advertise its capabilities.');
  }

  let runtimeConfig: PlayerRuntimeConfig | null;
  try {
    runtimeConfig = parseRuntimeConfig(await configResponse.json());
  } catch {
    runtimeConfig = null;
  }
  if (!runtimeConfig) {
    return unavailable('The dedicated browser player returned an invalid capability document.');
  }
  if (!runtimeConfig.allowedParents.includes(window.location.origin)) {
    return unavailable('The dedicated browser player does not allow this portal origin.');
  }

  return {
    status: 'available',
    capability: {
      playerOrigin: playerUrl.origin,
      emulatorJs: runtimeConfig.emulatorJs,
      cores: runtimeConfig.cores,
    },
  };
}

export function createPlayerBridge(
  frame: HTMLIFrameElement,
  capability: PlayerCapability,
  load: PlayerBridgeLoad,
  onEvent: (event: PlayerEventMessage) => void,
): PlayerBridge {
  if (new URL(capability.playerOrigin).origin === window.location.origin) {
    throw new PlayerBridgeError(
      'Browser playback refuses to connect to a same-origin player.',
      'same-origin-player',
    );
  }

  let port: MessagePort | null = null;
  let hello: PlayerHelloMessage | null = null;
  let disposed = false;
  let exited = false;
  let readySettled = false;
  let controlsReady = false;
  let controlsNegotiated = false;
  let shutdownResolve: (() => void) | null = null;
  let frameTimer: ReturnType<typeof setTimeout> | null = null;
  let handshakeTimer: ReturnType<typeof setTimeout> | null = null;
  let emulatorTimer: ReturnType<typeof setTimeout> | null = null;
  let shutdownTimer: ReturnType<typeof setTimeout> | null = null;
  let resolveReady: (message: PlayerHelloMessage) => void = () => undefined;
  let rejectReady: (error: Error) => void = () => undefined;

  const ready = new Promise<PlayerHelloMessage>((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
  });

  const clearTimer = (timer: ReturnType<typeof setTimeout> | null) => {
    if (timer) {
      clearTimeout(timer);
    }
  };

  const settleFailure = (error: PlayerBridgeError) => {
    if (!readySettled) {
      readySettled = true;
      rejectReady(error);
    } else {
      onEvent({
        type: 'event',
        event: 'error',
        code: error.code,
        message: error.message,
      });
    }
    dispose();
  };

  const handlePortMessage = (messageEvent: MessageEvent<unknown>) => {
    const message = parsePlayerToParentMessage(messageEvent.data);
    if (!message) {
      settleFailure(
        new PlayerBridgeError(
          'The browser player sent an invalid protocol message.',
          'invalid-message',
        ),
      );
      return;
    }

    if (message.type === 'hello') {
      if (hello) {
        settleFailure(
          new PlayerBridgeError(
            'The browser player repeated its handshake.',
            'duplicate-hello',
          ),
        );
        return;
      }
      clearTimer(handshakeTimer);
      hello = message;
      if (
        message.emulatorJs.version !== capability.emulatorJs.version ||
        message.emulatorJs.source !== capability.emulatorJs.source
      ) {
        settleFailure(
          new PlayerBridgeError(
            'The browser player handshake did not match its capabilities.',
            'hello-mismatch',
          ),
        );
        return;
      }
      if (!message.cores.includes(load.core) || !capability.cores.includes(load.core)) {
        settleFailure(
          new PlayerBridgeError(
            'The selected emulator core is not available.',
            'unsupported-core',
          ),
        );
        return;
      }

      controlsNegotiated = message.features?.includes('session-controls') === true;
      if (controlsNegotiated) port?.postMessage({ type: 'enable-controls' });
      const { blob, sha256, relativePath } = load.verifiedFile;
      port?.postMessage({
        type: 'load',
        core: load.core,
        displayName: load.displayName,
        contentSha256: sha256,
        relativePath,
        rom: blob,
      });
      emulatorTimer = setTimeout(() => {
        settleFailure(
          new PlayerBridgeError(
            'The emulator assets did not finish loading. ' +
              'The configured CDN or asset mirror may be unavailable.',
            'asset-load-timeout',
          ),
        );
      }, emulatorLoadTimeoutMs);
      return;
    }

    if (message.event === 'controls-ready') controlsReady = controlsNegotiated;
    if (message.event === 'controls-unavailable' || message.event === 'exit') controlsReady = false;
    if (message.event === 'exit') {
      exited = true;
      shutdownResolve?.();
      shutdownResolve = null;
    }

    if (message.event === 'error') {
      const messageText = isAssetLoadCode(message.code)
        ? 'The emulator assets could not be loaded from the configured CDN or asset mirror.'
        : message.message;
      settleFailure(new PlayerBridgeError(messageText, message.code));
      return;
    }

    if (message.event === 'ready' || message.event === 'game-started') {
      clearTimer(emulatorTimer);
      if (!readySettled && hello) {
        readySettled = true;
        resolveReady(hello);
      }
    }
    onEvent(message);
  };

  const handleFrameLoad = () => {
    clearTimer(frameTimer);
    const target = frame.contentWindow;
    if (!target) {
      settleFailure(
        new PlayerBridgeError(
          'The browser player frame did not initialize.',
          'missing-frame-window',
        ),
      );
      return;
    }

    const channel = new MessageChannel();
    port = channel.port1;
    port.onmessage = handlePortMessage;
    port.start();
    target.postMessage(
      { type: 'init', protocol: PLAYER_PROTOCOL_VERSION },
      capability.playerOrigin,
      [channel.port2],
    );
    handshakeTimer = setTimeout(() => {
      settleFailure(
        new PlayerBridgeError(
          'The browser player did not complete its handshake.',
          'handshake-timeout',
        ),
      );
    }, handshakeTimeoutMs);
  };

  function dispose() {
    if (disposed) {
      return;
    }
    disposed = true;
    frame.removeEventListener('load', handleFrameLoad);
    clearTimer(frameTimer);
    clearTimer(handshakeTimer);
    clearTimer(emulatorTimer);
    clearTimer(shutdownTimer);
    port?.close();
    port = null;
  }

  const shutdown = async () => {
    if (disposed || exited || !port) {
      dispose();
      return;
    }

    port.postMessage({ type: 'shutdown' });
    await new Promise<void>((resolve) => {
      shutdownResolve = resolve;
      shutdownTimer = setTimeout(resolve, shutdownTimeoutMs);
    });
    shutdownResolve = null;
    dispose();
  };

  frame.addEventListener('load', handleFrameLoad, { once: true });
  frameTimer = setTimeout(() => {
    settleFailure(
      new PlayerBridgeError(
        'The dedicated browser player could not be loaded.',
        'frame-load-timeout',
      ),
    );
  }, frameLoadTimeoutMs);

  const control = (action: PlayerControlAction) => {
    if (!disposed && !exited && controlsReady) port?.postMessage({ type: 'control', action });
  };
  return { ready, shutdown, dispose, control };
}

function parseOriginConfig(value: unknown): { playerOrigin: string } | null {
  if (!isRecord(value) || typeof value.playerOrigin !== 'string') {
    return null;
  }

  try {
    const url = new URL(value.playerOrigin);
    if (
      (url.protocol !== 'http:' && url.protocol !== 'https:') ||
      url.origin !== value.playerOrigin
    ) {
      return null;
    }
    return { playerOrigin: url.origin };
  } catch {
    return null;
  }
}

function parseRuntimeConfig(value: unknown): PlayerRuntimeConfig | null {
  if (
    !isRecord(value) ||
    value.schemaVersion !== 1 ||
    value.protocol !== PLAYER_PROTOCOL_VERSION ||
    !isRecord(value.emulatorJs) ||
    typeof value.emulatorJs.version !== 'string' ||
    value.emulatorJs.source !== 'cdn' ||
    typeof value.emulatorJs.dataPath !== 'string' ||
    !isStringArray(value.cores) ||
    value.cores.length === 0 ||
    !isStringArray(value.allowedParents)
  ) {
    return null;
  }

  return {
    schemaVersion: 1,
    protocol: 1,
    emulatorJs: {
      version: value.emulatorJs.version,
      source: value.emulatorJs.source,
      dataPath: value.emulatorJs.dataPath,
    },
    cores: [...value.cores],
    allowedParents: [...value.allowedParents],
  };
}

function isLikelySameSite(portalHost: string, playerHost: string): boolean {
  return (
    portalHost === playerHost ||
    portalHost === 'localhost' ||
    playerHost === 'localhost' ||
    portalHost.endsWith(`.${playerHost}`) ||
    playerHost.endsWith(`.${portalHost}`) ||
    portalHost.split('.').slice(-2).join('.') === playerHost.split('.').slice(-2).join('.')
  );
}

function isAssetLoadCode(code: string): boolean {
  return code.includes('asset') || code.includes('loader') || code.includes('cdn');
}

function isStringArray(value: unknown): value is string[] {
  return (
    Array.isArray(value) &&
    value.every((entry) => typeof entry === 'string' && entry.length > 0)
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function unavailable(reason: string): PlayerCapabilityResult {
  return { status: 'unavailable', reason };
}
