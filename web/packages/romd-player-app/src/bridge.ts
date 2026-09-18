import {
  PLAYER_PROTOCOL_VERSION,
  type PlayerErrorEventMessage,
  type PlayerEventMessage,
  type PlayerHelloMessage,
  type PlayerLoadMessage,
  type PlayerToParentMessage,
  parseParentToPlayerMessage,
  parsePlayerInitMessage,
} from '@romd/player-protocol';
import type { PlayerRuntimeConfig } from './config';
import { createSessionControls } from './controls';
import { type EmulatorJsExitApi, EmulatorJsShutdownAdapter } from './shutdown';

export const ASSET_LOADING_TIMEOUT_MS = 20_000;
export const CORE_LOADING_TIMEOUT_MS = 30_000;

interface EmulatorJsApi extends EmulatorJsExitApi {
  on(event: string, callback: () => void): void;
}

declare global {
  interface Window {
    EJS_player?: string;
    EJS_core?: string;
    EJS_gameName?: string;
    EJS_gameUrl?: string;
    EJS_pathtodata?: string;
    EJS_startOnLoaded?: boolean;
    EJS_color?: string;
    EJS_backgroundColor?: string;
    EJS_alignStartButton?: string;
    EJS_startButtonName?: string;
    EJS_Buttons?: Record<string, boolean>;
    EJS_disableAutoLang?: boolean;
    EJS_cacheConfig?: { enabled: boolean };
    EJS_ready?: () => void;
    EJS_onGameStart?: () => void;
    EJS_emulator?: unknown;
  }
}

export interface PlayerBridgeDependencies {
  window: Window;
  document: Document;
  parentWindow: WindowProxy;
  createObjectURL: (blob: Blob) => string;
  revokeObjectURL: (url: string) => void;
  setTimeout: typeof window.setTimeout;
  clearTimeout: typeof window.clearTimeout;
}

const defaultDependencies = (): PlayerBridgeDependencies => ({
  window,
  document,
  parentWindow: window.parent,
  createObjectURL: URL.createObjectURL.bind(URL),
  revokeObjectURL: URL.revokeObjectURL.bind(URL),
  setTimeout: window.setTimeout.bind(window),
  clearTimeout: window.clearTimeout.bind(window),
});

export class PlayerBridge {
  private controlsEnabled = false;
  private controls: ReturnType<typeof createSessionControls> = null;
  private readonly allowedParents: Set<string>;
  private readonly shutdownAdapter: EmulatorJsShutdownAdapter;
  private port: MessagePort | undefined;
  private acceptedInit = false;
  private acceptedLoad = false;
  private ready = false;
  private registeringReady = false;
  private failed = false;
  private exited = false;
  private romUrl: string | undefined;
  private loader: HTMLScriptElement | undefined;
  private displayName: string | undefined;
  private assetTimer: number | undefined;
  private coreTimer: number | undefined;

  constructor(
    private readonly config: PlayerRuntimeConfig,
    private readonly dependencies: PlayerBridgeDependencies = defaultDependencies(),
  ) {
    this.allowedParents = new Set(config.allowedParents);
    this.shutdownAdapter = new EmulatorJsShutdownAdapter({
      emulatorJsVersion: config.emulatorJs.version,
      setTimeout: dependencies.setTimeout,
      clearTimeout: dependencies.clearTimeout,
      onComplete: () => this.finishExit(),
      onFailure: (message) => this.sendError('shutdown-failed', message),
    });
  }

  start(): void {
    this.dependencies.window.addEventListener('message', this.handleWindowMessage);
  }

  /** Delivers a window message captured before the bridge was listening. */
  replayWindowMessage(event: MessageEvent): void {
    this.handleWindowMessage(event);
  }

  dispose(): void {
    this.controls?.dispose();
    this.controls = null;
    this.dependencies.window.removeEventListener('message', this.handleWindowMessage);
    this.clearLoadingTimers();
    this.removeLoader();
    this.port?.close();
    this.revokeRomUrl();
  }

  private readonly handleWindowMessage = (event: MessageEvent): void => {
    if (
      this.acceptedInit ||
      event.source !== this.dependencies.parentWindow ||
      !this.allowedParents.has(event.origin) ||
      event.ports.length !== 1 ||
      parsePlayerInitMessage(event.data) === null
    ) {
      return;
    }

    this.acceptedInit = true;
    this.port = event.ports[0];
    this.port.onmessage = this.handlePortMessage;
    this.port.start();
    this.post({
      type: 'hello',
      features: ['session-controls'],
      protocol: PLAYER_PROTOCOL_VERSION,
      emulatorJs: {
        version: this.config.emulatorJs.version,
        source: this.config.emulatorJs.source,
      },
      cores: this.config.cores,
    } satisfies PlayerHelloMessage);
  };

  private readonly handlePortMessage = (event: MessageEvent): void => {
    if (this.failed || this.exited) {
      return;
    }

    const message = parseParentToPlayerMessage(event.data);
    if (!message) {
      this.sendError('invalid-message', 'The player rejected an invalid parent message.');
      return;
    }

    if (message.type === 'enable-controls') {
      this.controlsEnabled = true;
      return;
    }
    if (message.type === 'control') {
      this.controls?.control(message.action);
      return;
    }
    if (message.type === 'shutdown') {
      this.controls?.dispose();
      this.controls = null;
      const emulator = this.dependencies.window.EJS_emulator;
      this.shutdownAdapter.request(isEmulatorJsApi(emulator) ? emulator : undefined);
      return;
    }

    if (this.acceptedLoad) {
      this.sendError('already-loaded', 'The player accepts one ROM load per frame.');
      return;
    }
    if (!this.config.cores.includes(message.core)) {
      this.sendError('unsupported-core', `The configured player does not advertise ${message.core}.`);
      return;
    }

    this.acceptedLoad = true;
    this.load(message);
  };

  private load(message: PlayerLoadMessage): void {
    try {
      this.displayName = message.displayName;
      this.romUrl = this.dependencies.createObjectURL(message.rom);
      this.dependencies.document.title = `${message.displayName} — ROMD Player`;
      this.setStatus(`Loading ${message.displayName}…`);

      const playerWindow = this.dependencies.window;
      playerWindow.EJS_player = '#game';
      playerWindow.EJS_core = message.core;
      playerWindow.EJS_gameName = message.contentSha256.toLowerCase();
      playerWindow.EJS_gameUrl = this.romUrl;
      playerWindow.EJS_pathtodata = this.config.emulatorJs.dataPath;
      playerWindow.EJS_startOnLoaded = false;
      // Supported by the pinned 4.2.3 loader; leave the real start gesture inside
      // the iframe so audio and fullscreen activation keep their browser semantics.
      playerWindow.EJS_color = playerWindow.getComputedStyle(this.dependencies.document.documentElement).getPropertyValue('--romd-accent').trim();
      playerWindow.EJS_backgroundColor = 'transparent';
      playerWindow.EJS_alignStartButton = 'center';
      playerWindow.EJS_startButtonName = 'Start game';
      playerWindow.EJS_Buttons = { cacheManager: false };

      playerWindow.EJS_disableAutoLang = false;
      playerWindow.EJS_cacheConfig = { enabled: false };
      playerWindow.EJS_ready = this.handleEmulatorReady;
      playerWindow.EJS_onGameStart = this.handleGameStart;

      const loader = this.dependencies.document.createElement('script');
      this.loader = loader;
      const dataUrl = new URL(
        this.config.emulatorJs.dataPath,
        this.dependencies.window.location.href,
      );
      loader.src = new URL('loader.js', dataUrl).href;
      loader.async = true;
      loader.addEventListener('error', () => {
        this.failLoading('asset-loading-failed', 'The pinned EmulatorJS loader could not be loaded.');
      });
      this.assetTimer = this.dependencies.setTimeout(() => {
        this.failLoading(
          'asset-loading-timeout',
          'The pinned EmulatorJS assets did not become ready in time.',
        );
      }, ASSET_LOADING_TIMEOUT_MS);
      this.dependencies.document.head.appendChild(loader);
    } catch (error) {
      this.failLoading('asset-loading-failed', toErrorMessage(error));
    }
  }

  private readonly handleEmulatorReady = (): void => {
    if (this.ready || this.registeringReady || this.failed || this.exited) {
      return;
    }
    this.registeringReady = true;
    const emulator = this.dependencies.window.EJS_emulator;
    if (!isEmulatorJsApi(emulator)) {
      this.failLoading(
        'asset-loading-failed',
        'EmulatorJS reported ready with an invalid emulator API.',
      );
      return;
    }

    try {
      emulator.on('start-clicked', this.handleStartClicked);
      emulator.on('saveSaveFiles', this.handleSaveUpdated);
      emulator.on('exit', this.handleEmulatorExit);
    } catch (error) {
      this.failLoading('asset-loading-failed', toErrorMessage(error));
      return;
    }

    this.ready = true;
    this.registeringReady = false;
    this.clearAssetTimer();
    this.setStatus('');
    this.enhanceStartButton();
    this.postEvent('ready');
  };

  // EmulatorJS 4.2.3 renders a clickable div. Preserve its click handler and
  // user activation while giving keyboard and assistive-tech users a button.
  private enhanceStartButton(): void {
    const button = this.dependencies.document.querySelector<HTMLElement>('.ejs_start_button');
    if (!button) return;
    button.setAttribute('role', 'button');
    button.tabIndex = 0;
    button.addEventListener('keydown', (event) => {
      if ((event.key === 'Enter' || event.key === ' ') && !event.repeat) {
        event.preventDefault();
        button.click();
      }
    });
  }

  private readonly handleStartClicked = (): void => {
    if (!this.ready || this.coreTimer !== undefined || this.failed || this.exited) {
      return;
    }
    this.coreTimer = this.dependencies.setTimeout(() => {
      this.failLoading(
        'core-loading-timeout',
        'The configured EmulatorJS core did not start in time.',
      );
    }, CORE_LOADING_TIMEOUT_MS);
  };

  private readonly handleGameStart = (): void => {
    if (!this.ready || this.failed || this.exited) {
      return;
    }
    if (this.coreTimer !== undefined) {
      this.dependencies.clearTimeout(this.coreTimer);
      this.coreTimer = undefined;
    }
    this.setStatus('');
    this.dependencies.document.body.dataset.playing = 'true';
    this.postEvent('game-started');
    if (this.controlsEnabled && !this.controls) {
      this.controls = createSessionControls(
        this.dependencies.window.EJS_emulator,
        this.config.emulatorJs.version,
        this.dependencies,
        paused => this.postEvent(paused ? 'paused' : 'resumed'),
        () => this.postEvent('controls-unavailable'),
      );
      if (this.controls) this.postEvent('controls-ready');
    }
  };

  private readonly handleSaveUpdated = (): void => {
    if (!this.ready || this.failed || this.exited) {
      return;
    }
    this.postEvent('save-updated');
  };

  private readonly handleEmulatorExit = (): void => {
    this.controls?.dispose();
    this.controls = null;
    if (!this.ready || this.failed || this.exited) {
      return;
    }
    this.shutdownAdapter.observeExit();
  };

  private failLoading(code: string, message: string): void {
    this.controls?.dispose();
    this.controls = null;
    if (this.failed || this.exited) {
      return;
    }
    this.failed = true;
    this.clearLoadingTimers();
    this.removeLoader();
    this.revokeRomUrl();
    this.setStatus(message);
    this.sendError(code, message);
  }

  private finishExit(): void {
    if (this.exited) {
      return;
    }
    this.exited = true;
    this.clearLoadingTimers();
    this.removeLoader();
    this.revokeRomUrl();
    this.postEvent('exit');
  }

  private clearLoadingTimers(): void {
    this.clearAssetTimer();
    if (this.coreTimer !== undefined) {
      this.dependencies.clearTimeout(this.coreTimer);
      this.coreTimer = undefined;
    }
  }

  private clearAssetTimer(): void {
    if (this.assetTimer !== undefined) {
      this.dependencies.clearTimeout(this.assetTimer);
      this.assetTimer = undefined;
    }
  }

  private removeLoader(): void {
    this.loader?.remove();
    this.loader = undefined;
  }

  private revokeRomUrl(): void {
    if (this.romUrl) {
      this.dependencies.revokeObjectURL(this.romUrl);
      this.romUrl = undefined;
    }
  }

  private setStatus(message: string): void {
    const status = this.dependencies.document.querySelector<HTMLElement>('#status');
    if (status) {
      status.textContent = message;
      status.hidden = !message;
    }
  }

  private postEvent(event: Exclude<PlayerEventMessage['event'], 'error'>): void {
    this.post({ type: 'event', event });
  }

  private sendError(code: string, message: string): void {
    this.post({ type: 'event', event: 'error', code, message } satisfies PlayerErrorEventMessage);
  }

  private post(message: PlayerToParentMessage): void {
    this.port?.postMessage(message);
  }
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'The EmulatorJS runtime failed to load.';
}

function isEmulatorJsApi(value: unknown): value is EmulatorJsApi {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  try {
    const candidate = value as Record<string, unknown>;
    return typeof candidate.on === 'function' && typeof candidate.callEvent === 'function';
  } catch {
    return false;
  }
}
