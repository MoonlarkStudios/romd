import { PLAYER_PROTOCOL_VERSION } from '@romd/player-protocol';
import { createPlayerBridge, fetchPlayerCapability } from './playerBridge';

describe('fetchPlayerCapability', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('loads and validates the anonymous origin and cross-origin capability documents', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(
        jsonResponse({
          schemaVersion: 1,
          protocol: 1,
          emulatorJs: {
            version: '4.2.3',
            source: 'cdn',
            dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
          },
          cores: ['snes9x'],
          allowedParents: ['http://localhost:3000'],
        }),
      );

    const result = await fetchPlayerCapability();

    expect(result).toEqual({
      status: 'available',
      capability: {
        playerOrigin: 'http://player.localhost:5175',
        emulatorJs: {
          version: '4.2.3',
          source: 'cdn',
          dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
        },
        cores: ['snes9x'],
      },
    });
    expect(fetch).toHaveBeenNthCalledWith(
      2,
      new URL('http://player.localhost:5175/player-config.json'),
      expect.objectContaining({
        credentials: 'omit',
        mode: 'cors',
        referrerPolicy: 'no-referrer',
      }),
    );
  });

  it('refuses a same-origin player before fetching its capability document', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse({ playerOrigin: window.location.origin }));

    const result = await fetchPlayerCapability();

    expect(result).toEqual({
      status: 'unavailable',
      reason: expect.stringMatching(/same-origin/i),
    });
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('rejects capability documents with the wrong schema or protocol version', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(
        jsonResponse({
          schemaVersion: 2,
          protocol: 1,
          emulatorJs: {
            version: '4.2.3',
            source: 'cdn',
            dataPath: 'https://example.test/data/',
          },
          cores: ['snes9x'],
          allowedParents: [],
        }),
      );

    const result = await fetchPlayerCapability();

    expect(result).toEqual({
      status: 'unavailable',
      reason: expect.stringMatching(/invalid capability/i),
    });
  });

  it('rejects a player that does not allow this portal origin', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(
        jsonResponse({
          schemaVersion: 1,
          protocol: 1,
          emulatorJs: {
            version: '4.2.3',
            source: 'cdn',
            dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
          },
          cores: ['snes9x'],
          allowedParents: ['https://another-portal.example'],
        }),
      );

    await expect(fetchPlayerCapability()).resolves.toEqual({
      status: 'unavailable',
      reason: expect.stringMatching(/does not allow/i),
    });
  });

  it('rejects the Phase 2 bundled source', async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(
        jsonResponse({
          schemaVersion: 1,
          protocol: 1,
          emulatorJs: {
            version: '4.2.3',
            source: 'bundled',
            dataPath: '/vendor/emulatorjs/4.2.3/data/',
          },
          cores: ['snes9x'],
          allowedParents: [window.location.origin],
        }),
      );

    await expect(fetchPlayerCapability()).resolves.toEqual({
      status: 'unavailable',
      reason: expect.stringMatching(/invalid capability/i),
    });
  });
});

describe('createPlayerBridge', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('refuses a direct same-origin bridge before creating a channel', () => {
    const frame = document.createElement('iframe');
    expect(() =>
      createPlayerBridge(
        frame,
        {
          playerOrigin: window.location.origin,
          emulatorJs: { version: '4.2.3', source: 'cdn', dataPath: 'https://example.test/data/' },
          cores: ['snes9x'],
        },
        {
          core: 'snes9x',
          displayName: 'Super Metroid',
          verifiedFile: { relativePath: 'game.sfc', sha256: 'a'.repeat(64), blob: new Blob(['x']) },
        },
        vi.fn(),
      ),
    ).toThrow(/same-origin/i);
  });

  it('transfers a port, validates hello, and sends only verified ROM data in load', async () => {
    const channel = installMessageChannel();
    const frame = document.createElement('iframe');
    document.body.appendChild(frame);
    const postMessage = vi.spyOn(frame.contentWindow as Window, 'postMessage');
    const onEvent = vi.fn();
    const verifiedFile = {
      relativePath: 'SNES/Super Metroid.sfc',
      sizeBytes: 5,
      sha256: 'a'.repeat(64),
      blob: new Blob(['hello']),
      objectUrl: 'blob:http://localhost/secret-object-url',
      contentType: 'application/octet-stream',
    };
    const bridge = createPlayerBridge(
      frame,
      {
        playerOrigin: 'http://player.localhost:5175',
        emulatorJs: {
          version: '4.2.3',
          source: 'cdn',
          dataPath: 'https://example.test/data/',
        },
        cores: ['snes9x'],
      },
      { core: 'snes9x', displayName: 'Super Metroid', verifiedFile },
      onEvent,
    );

    frame.dispatchEvent(new Event('load'));
    expect(postMessage).toHaveBeenCalledWith(
      { type: 'init', protocol: PLAYER_PROTOCOL_VERSION },
      'http://player.localhost:5175',
      [channel.port2],
    );

    channel.port1.receive({
      type: 'hello',
      protocol: 1,
      emulatorJs: { version: '4.2.3', source: 'cdn' },
      cores: ['snes9x'],
    });
    const loadMessage = channel.port1.postMessage.mock.calls[0]?.[0];
    expect(loadMessage).toEqual({
      type: 'load',
      core: 'snes9x',
      displayName: 'Super Metroid',
      contentSha256: 'a'.repeat(64),
      relativePath: 'SNES/Super Metroid.sfc',
      rom: verifiedFile.blob,
    });
    expect(JSON.stringify(loadMessage)).not.toContain('secret-object-url');
    expect(JSON.stringify(loadMessage)).not.toContain('downloadUrl');

    channel.port1.receive({ type: 'event', event: 'ready' });
    await expect(bridge.ready).resolves.toMatchObject({ type: 'hello' });
    expect(onEvent).toHaveBeenCalledWith({ type: 'event', event: 'ready' });
    bridge.dispose();
  });

  it.each([true, false])('negotiates controls safely (advertised: %s)', async supported => {
    const channel = installMessageChannel();
    const frame = document.createElement('iframe');
    document.body.appendChild(frame);
    const bridge = createPlayerBridge(frame, {
      playerOrigin: 'http://player.localhost:5175',
      emulatorJs: { version: '4.2.3', source: 'cdn', dataPath: 'https://example.test/data/' },
      cores: ['snes9x'],
    }, {
      core: 'snes9x', displayName: 'Game',
      verifiedFile: { relativePath: 'game.sfc', sha256: 'a'.repeat(64), blob: new Blob(['x']) },
    }, vi.fn());
    frame.dispatchEvent(new Event('load'));
    channel.port1.receive({ type: 'hello', protocol: 1, emulatorJs: { version: '4.2.3', source: 'cdn' }, cores: ['snes9x'], ...(supported ? { features: ['session-controls'] } : {}) });
    channel.port1.receive({ type: 'event', event: 'ready' });
    await bridge.ready;
    expect(channel.port1.postMessage.mock.calls.some(([message]) => (message as { type: string }).type === 'enable-controls')).toBe(supported);
    channel.port1.postMessage.mockClear();
    bridge.control('pause');
    expect(channel.port1.postMessage).not.toHaveBeenCalled();
    channel.port1.receive({ type: 'event', event: 'controls-ready' });
    bridge.control('pause');
    expect(channel.port1.postMessage).toHaveBeenCalledTimes(supported ? 1 : 0);
    channel.port1.postMessage.mockClear();
    bridge.dispose();
    bridge.control('resume');
    expect(channel.port1.postMessage).not.toHaveBeenCalled();
  });

  it('requests shutdown and waits for the validated exit event', async () => {
    const channel = installMessageChannel();
    const frame = document.createElement('iframe');
    document.body.appendChild(frame);
    const bridge = createPlayerBridge(
      frame,
      {
        playerOrigin: 'http://player.localhost:5175',
        emulatorJs: {
          version: '4.2.3',
          source: 'cdn',
          dataPath: 'https://example.test/data/',
        },
        cores: ['snes9x'],
      },
      {
        core: 'snes9x',
        displayName: 'Super Metroid',
        verifiedFile: {
          relativePath: 'game.sfc',
          sha256: 'b'.repeat(64),
          blob: new Blob(['x']),
        },
      },
      vi.fn(),
    );
    frame.dispatchEvent(new Event('load'));
    channel.port1.receive({
      type: 'hello',
      protocol: 1,
      emulatorJs: { version: '4.2.3', source: 'cdn' },
      cores: ['snes9x'],
    });
    channel.port1.receive({ type: 'event', event: 'ready' });
    await bridge.ready;

    const shutdown = bridge.shutdown();
    expect(channel.port1.postMessage).toHaveBeenLastCalledWith({ type: 'shutdown' });
    channel.port1.receive({ type: 'event', event: 'exit' });
    await shutdown;
    expect(channel.port1.close).toHaveBeenCalled();
  });

  it('reports and terminates on an invalid message after readiness', async () => {
    const channel = installMessageChannel();
    const frame = document.createElement('iframe');
    document.body.appendChild(frame);
    const onEvent = vi.fn();
    const bridge = createPlayerBridge(
      frame,
      {
        playerOrigin: 'http://player.localhost:5175',
        emulatorJs: { version: '4.2.3', source: 'cdn', dataPath: 'https://example.test/data/' },
        cores: ['snes9x'],
      },
      {
        core: 'snes9x',
        displayName: 'Super Metroid',
        verifiedFile: { relativePath: 'game.sfc', sha256: 'c'.repeat(64), blob: new Blob(['x']) },
      },
      onEvent,
    );
    frame.dispatchEvent(new Event('load'));
    channel.port1.receive({
      type: 'hello',
      protocol: 1,
      emulatorJs: { version: '4.2.3', source: 'cdn' },
      cores: ['snes9x'],
    });
    channel.port1.receive({ type: 'event', event: 'ready' });
    await bridge.ready;

    channel.port1.receive({ type: 'not-allowed' });

    expect(onEvent).toHaveBeenLastCalledWith(
      expect.objectContaining({ type: 'event', event: 'error', code: 'invalid-message' }),
    );
    expect(channel.port1.close).toHaveBeenCalled();
  });
});

class FakeMessagePort {
  onmessage: ((event: MessageEvent<unknown>) => void) | null = null;
  postMessage = vi.fn<(message: unknown) => void>();
  close = vi.fn();
  start = vi.fn();

  receive(data: unknown) {
    this.onmessage?.(new MessageEvent('message', { data }));
  }
}

function installMessageChannel(): { port1: FakeMessagePort; port2: FakeMessagePort } {
  const channel = { port1: new FakeMessagePort(), port2: new FakeMessagePort() };
  vi.stubGlobal(
    'MessageChannel',
    class {
      port1 = channel.port1;
      port2 = channel.port2;
    },
  );
  return channel;
}

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}
