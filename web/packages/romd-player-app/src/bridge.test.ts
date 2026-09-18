import { PlayerBridge } from './bridge';
import type { PlayerRuntimeConfig } from './config';
import { SAVE_FLUSH_GRACE_MS } from './shutdown';

class StubMessagePort {
  readonly messages: unknown[] = [];
  onmessage: ((event: MessageEvent) => void) | null = null;
  readonly start = vi.fn();
  readonly close = vi.fn();

  postMessage(message: unknown): void {
    this.messages.push(message);
  }

  receive(message: unknown): void {
    this.onmessage?.({ data: message } as MessageEvent);
  }
}

class StubEmulator {
  paused = false;
  controlMenu = document.createElement('div');
  elements = { bottomBar: { gamepad: [document.createElement('button')] } };
  pause() { this.paused = true; }
  play() { this.paused = false; }
  readonly callbacks = new Map<string, Array<() => void>>();
  readonly callEvent = vi.fn((event: 'exit') => {
    const callbacks = this.callbacks.get(event) ?? [];
    for (const callback of callbacks) {
      callback();
    }
    return callbacks.length + 1;
  });

  on(event: string, callback: () => void): void {
    const callbacks = this.callbacks.get(event) ?? [];
    callbacks.push(callback);
    this.callbacks.set(event, callbacks);
  }

  emit(event: string): void {
    for (const callback of this.callbacks.get(event) ?? []) {
      callback();
    }
  }
}

const config: PlayerRuntimeConfig = {
  schemaVersion: 1,
  protocol: 1,
  emulatorJs: {
    version: '4.2.3',
    source: 'cdn',
    dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
  },
  cores: ['fceumm'],
  allowedParents: ['https://romd.example'],
};

const parentWindow = {} as WindowProxy;
const contentSha256 = 'a'.repeat(64);

describe('PlayerBridge', () => {
  const createObjectURL = vi.fn(() => 'blob:https://play.romd.example/player-rom');
  const revokeObjectURL = vi.fn();

  beforeEach(() => {
    vi.useFakeTimers();
    document.head.innerHTML = '';
    document.body.innerHTML = '<div id="game"></div><div id="status"></div>';
    createObjectURL.mockClear();
    revokeObjectURL.mockClear();
    window.EJS_emulator = undefined;
    window.EJS_ready = undefined;
    window.EJS_onGameStart = undefined;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('accepts exactly one init from window.parent at an allowed origin', () => {
    const bridge = createBridge();
    const rejectedSource = new StubMessagePort();
    dispatchInit(rejectedSource, 'https://romd.example', {} as WindowProxy);
    expect(rejectedSource.messages).toHaveLength(0);

    const rejectedOrigin = new StubMessagePort();
    dispatchInit(rejectedOrigin, 'https://evil.example', parentWindow);
    expect(rejectedOrigin.messages).toHaveLength(0);

    const accepted = new StubMessagePort();
    dispatchInit(accepted, 'https://romd.example', parentWindow);
    expect(accepted.messages).toEqual([
      {
        type: 'hello',
        features: ['session-controls'],
        protocol: 1,
        emulatorJs: { version: '4.2.3', source: 'cdn' },
        cores: ['fceumm'],
      },
    ]);
    expect(accepted.start).toHaveBeenCalledOnce();

    const duplicate = new StubMessagePort();
    dispatchInit(duplicate, 'https://romd.example', parentWindow);
    expect(duplicate.messages).toHaveLength(0);
    bridge.dispose();
  });

  it('rejects init messages without one transferred port', () => {
    const bridge = createBridge();
    window.dispatchEvent(
      new MessageEvent('message', {
        data: { type: 'init', protocol: 1 },
        origin: 'https://romd.example',
        source: parentWindow,
        ports: [],
      }),
    );

    const port = new StubMessagePort();
    dispatchInit(port, 'https://romd.example', parentWindow);
    expect(port.messages).toHaveLength(1);
    bridge.dispose();
  });

  it('rejects malformed load messages and unadvertised cores', () => {
    const { bridge, port } = initializedBridge();
    port.receive({ type: 'load', core: 'fceumm', rom: 'not-a-blob' });
    expect(lastMessage(port)).toMatchObject({ event: 'error', code: 'invalid-message' });

    port.receive(validLoad({ core: 'snes9x' }));
    expect(lastMessage(port)).toMatchObject({ event: 'error', code: 'unsupported-core' });
    expect(createObjectURL).not.toHaveBeenCalled();
    bridge.dispose();
  });

  it('uses the hash as storage identity and creates the ROM URL on the player origin', () => {
    const { bridge, port } = initializedBridge();
    port.receive(validLoad());

    expect(createObjectURL).toHaveBeenCalledWith(expect.any(Blob));
    expect(window.EJS_gameName).toBe(contentSha256);
    expect(window.EJS_gameUrl).toBe('blob:https://play.romd.example/player-rom');
    expect(window.EJS_core).toBe('fceumm');
    expect(window.EJS_startOnLoaded).toBe(false);
    expect(window.EJS_backgroundColor).toBe('transparent');
    expect(window.EJS_alignStartButton).toBe('center');
    expect(window.EJS_pathtodata).toBe('https://cdn.emulatorjs.org/4.2.3/data/');
    expect(document.title).toBe('Example Game — ROMD Player');
    expect(document.head.querySelector<HTMLScriptElement>('script')?.src).toBe(
      'https://cdn.emulatorjs.org/4.2.3/data/loader.js',
    );
    expect(window).not.toHaveProperty('EJS_onSaveState');
    expect(window).not.toHaveProperty('EJS_onLoadState');
    bridge.dispose();
  });

  it('keeps launch keyboard-accessible and clears ready and playing overlays', () => {
    const { bridge, port } = initializedBridge();
    port.receive(validLoad());
    const button = document.createElement('div');
    button.className = 'ejs_start_button';
    document.querySelector('#game')?.appendChild(button);
    const clicked = vi.fn();
    button.addEventListener('click', clicked);
    window.EJS_emulator = new StubEmulator();

    window.EJS_ready?.();
    expect(button.getAttribute('role')).toBe('button');
    expect(button.tabIndex).toBe(0);
    button.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    button.dispatchEvent(new KeyboardEvent('keydown', { key: ' ' }));
    button.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', repeat: true }));
    expect(clicked).toHaveBeenCalledTimes(2);
    expect(document.querySelector<HTMLElement>('#status')?.hidden).toBe(true);
    window.EJS_onGameStart?.();
    expect(document.querySelector<HTMLElement>('#status')?.hidden).toBe(true);
    expect(document.body.dataset.playing).toBe('true');
    bridge.dispose();
  });

  it('enables controls only after opt-in and runtime start, and observes native pauses', () => {
    const { bridge, port } = initializedBridge();
    port.receive({ type: 'enable-controls' });
    port.receive(validLoad());
    const runtime = new StubEmulator();
    window.EJS_emulator = runtime;
    window.EJS_ready?.();
    port.receive({ type: 'control', action: 'pause' });
    expect(runtime.paused).toBe(false);
    window.EJS_onGameStart?.();
    expect(port.messages).toContainEqual({ type: 'event', event: 'controls-ready' });
    runtime.pause();
    vi.advanceTimersByTime(200);
    expect(port.messages).toContainEqual({ type: 'event', event: 'paused' });
    port.receive({ type: 'control', action: 'resume' });
    expect(runtime.paused).toBe(false);
    expect(port.messages.at(-1)).toEqual({ type: 'event', event: 'resumed' });
    bridge.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('resolves a local dataPath against the player origin', () => {
    const localBridge = createBridge({
      ...config,
      emulatorJs: { ...config.emulatorJs, dataPath: '/mirror/ejs/4.2.3/data/' },
    });
    const port = new StubMessagePort();
    dispatchInit(port, 'https://romd.example', parentWindow);
    port.receive(validLoad());

    expect(document.head.querySelector<HTMLScriptElement>('script')?.src).toBe(
      new URL('/mirror/ejs/4.2.3/data/loader.js', window.location.href).href,
    );
    localBridge.dispose();
  });

  it('emits lifecycle events and performs idempotent pinned shutdown', () => {
    const { bridge, port } = initializedBridge();
    port.receive(validLoad());
    const emulator = new StubEmulator();
    window.EJS_emulator = emulator;

    window.EJS_ready?.();
    emulator.emit('saveSaveFiles');
    window.EJS_onGameStart?.();
    expect(port.messages).toContainEqual({ type: 'event', event: 'ready' });
    expect(port.messages).toContainEqual({ type: 'event', event: 'save-updated' });
    expect(port.messages).toContainEqual({ type: 'event', event: 'game-started' });
    expect(port.messages).not.toContainEqual({ type: 'event', event: 'controls-ready' });

    port.receive({ type: 'shutdown' });
    port.receive({ type: 'shutdown' });
    expect(emulator.callEvent).toHaveBeenCalledOnce();
    expect(emulator.callEvent).toHaveBeenCalledWith('exit');
    expect(port.messages).not.toContainEqual({ type: 'event', event: 'exit' });

    vi.advanceTimersByTime(SAVE_FLUSH_GRACE_MS);
    expect(port.messages).toContainEqual({ type: 'event', event: 'exit' });
    expect(revokeObjectURL).toHaveBeenCalledOnce();
    bridge.dispose();
  });

  it('treats a malformed emulator API as terminal and cleans up the ROM Blob', () => {
    const { bridge, port } = initializedBridge();
    port.receive(validLoad());
    window.EJS_emulator = { on: vi.fn() };

    window.EJS_ready?.();

    expect(lastMessage(port)).toMatchObject({
      type: 'event',
      event: 'error',
      code: 'asset-loading-failed',
    });
    expect(document.head.querySelector('script')).toBeNull();
    expect(revokeObjectURL).toHaveBeenCalledOnce();

    const messageCount = port.messages.length;
    window.EJS_ready?.();
    window.EJS_onGameStart?.();
    expect(port.messages).toHaveLength(messageCount);
    expect(port.messages).not.toContainEqual({ type: 'event', event: 'ready' });
    expect(port.messages).not.toContainEqual({ type: 'event', event: 'game-started' });
    expect(revokeObjectURL).toHaveBeenCalledOnce();
    bridge.dispose();
  });

  it('makes ready registration terminal when an event registration throws', () => {
    const { bridge, port } = initializedBridge();
    port.receive(validLoad());
    const callbacks: Array<() => void> = [];
    let registrationCount = 0;
    window.EJS_emulator = {
      callEvent: vi.fn(() => 1),
      on: vi.fn((_event: string, callback: () => void) => {
        registrationCount += 1;
        callbacks.push(callback);
        if (registrationCount === 2) {
          throw new Error('registration failed');
        }
      }),
    };

    window.EJS_ready?.();
    callbacks[0]?.();

    expect(lastMessage(port)).toMatchObject({
      type: 'event',
      event: 'error',
      code: 'asset-loading-failed',
      message: 'registration failed',
    });
    expect(port.messages).not.toContainEqual({ type: 'event', event: 'ready' });
    expect(revokeObjectURL).toHaveBeenCalledOnce();
    bridge.dispose();
  });

  function createBridge(runtimeConfig: PlayerRuntimeConfig = config): PlayerBridge {
    const bridge = new PlayerBridge(runtimeConfig, {
      window,
      document,
      parentWindow,
      createObjectURL,
      revokeObjectURL,
      setTimeout: window.setTimeout.bind(window),
      clearTimeout: window.clearTimeout.bind(window),
    });
    bridge.start();
    return bridge;
  }

  function initializedBridge(): { bridge: PlayerBridge; port: StubMessagePort } {
    const bridge = createBridge();
    const port = new StubMessagePort();
    dispatchInit(port, 'https://romd.example', parentWindow);
    return { bridge, port };
  }
});

function dispatchInit(port: StubMessagePort, origin: string, source: WindowProxy): void {
  window.dispatchEvent(
    new MessageEvent('message', {
      data: { type: 'init', protocol: 1 },
      origin,
      source,
      ports: [port as unknown as MessagePort],
    }),
  );
}

function validLoad(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    type: 'load',
    core: 'fceumm',
    displayName: 'Example Game',
    contentSha256,
    relativePath: 'example.nes',
    rom: new Blob([new Uint8Array([1, 2, 3])]),
    ...overrides,
  };
}

function lastMessage(port: StubMessagePort): Record<string, unknown> {
  return port.messages.at(-1) as Record<string, unknown>;
}
