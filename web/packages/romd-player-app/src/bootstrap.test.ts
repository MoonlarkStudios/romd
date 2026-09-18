import { bootstrapPlayer } from './bootstrap';
import type { PlayerRuntimeConfig } from './config';

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

it('does not complete bootstrap until the config-backed listener is installed', async () => {
  let resolveConfig: ((value: PlayerRuntimeConfig) => void) | undefined;
  const loadConfig = vi.fn(
    () =>
      new Promise<PlayerRuntimeConfig>((resolve) => {
        resolveConfig = resolve;
      }),
  );
  const bridge = { start: vi.fn(), replayWindowMessage: vi.fn() };
  const createBridge = vi.fn(() => bridge);

  const bootstrap = bootstrapPlayer({ window, loadConfig, createBridge });
  await Promise.resolve();
  expect(createBridge).not.toHaveBeenCalled();
  expect(bridge.start).not.toHaveBeenCalled();

  resolveConfig?.(config);
  await expect(bootstrap).resolves.toBe(bridge);
  expect(createBridge).toHaveBeenCalledWith(config);
  expect(bridge.start).toHaveBeenCalledOnce();
});

it('replays window messages that arrive before the config finishes loading', async () => {
  let resolveConfig: ((value: PlayerRuntimeConfig) => void) | undefined;
  const loadConfig = vi.fn(
    () =>
      new Promise<PlayerRuntimeConfig>((resolve) => {
        resolveConfig = resolve;
      }),
  );
  const bridge = { start: vi.fn(), replayWindowMessage: vi.fn() };
  const createBridge = vi.fn(() => bridge);

  const bootstrap = bootstrapPlayer({ window, loadConfig, createBridge });
  await Promise.resolve();
  const earlyInit = new MessageEvent('message', { data: { type: 'init', protocol: 1 } });
  window.dispatchEvent(earlyInit);

  resolveConfig?.(config);
  await bootstrap;

  expect(bridge.replayWindowMessage).toHaveBeenCalledTimes(1);
  expect(bridge.replayWindowMessage).toHaveBeenCalledWith(earlyInit);
  // The live listener is active before replay, so no delivery gap remains.
  expect(bridge.start.mock.invocationCallOrder[0]).toBeLessThan(
    bridge.replayWindowMessage.mock.invocationCallOrder[0],
  );
});

it('stops queueing and drops captured messages when the config load fails', async () => {
  const loadConfig = vi.fn(() => Promise.reject(new Error('config failed')));
  const createBridge = vi.fn();

  await expect(bootstrapPlayer({ window, loadConfig, createBridge })).rejects.toThrow(
    'config failed',
  );
  expect(createBridge).not.toHaveBeenCalled();
});
