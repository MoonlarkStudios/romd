import { parsePlayerRuntimeConfig } from './config';
import {
  createDevelopmentPlayerConfig,
  createDevelopmentPlayerConfigMiddleware,
} from './devPlayerConfig';

describe('player Vite development config', () => {
  it('serves a parser-valid CDN-only config from pin-aligned defaults', () => {
    const config = createDevelopmentPlayerConfig({});

    expect(parsePlayerRuntimeConfig(config)).toEqual(config);
    expect(config.emulatorJs).toEqual({
      version: '4.2.3',
      source: 'cdn',
      dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
    });
    expect(config.allowedParents).toEqual(['http://localhost:5174']);
  });

  it('allows an explicit mirror, pinned core subset, and parent origins', () => {
    const config = createDevelopmentPlayerConfig({
      VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH: 'https://assets.example/ejs/data/',
      VITE_ROMD_PLAYER_CORES: 'fceumm,mgba',
      VITE_ROMD_PLAYER_ALLOWED_PARENTS: 'http://localhost:5174,https://console.example.com',
    });

    expect(parsePlayerRuntimeConfig(config)).toEqual(config);
    expect(config.cores).toEqual(['fceumm', 'mgba']);
  });

  it.each([
    [{ VITE_ROMD_PLAYER_EMULATORJS_SOURCE: 'bundled' }, 'must be cdn'],
    [
      { VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH: 'https://cdn.emulatorjs.org/4.2.2/data/' },
      'must use the pinned version',
    ],
    [
      { VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH: 'https://assets.example/%2573table/data/' },
      'must be pinned',
    ],
    [{ VITE_ROMD_PLAYER_CORES: 'unknown' }, 'outside the EmulatorJS pin'],
    [{ VITE_ROMD_PLAYER_ALLOWED_PARENTS: 'https://console.example.com/path' }, 'canonical'],
  ])('rejects invalid environment: %o', (env, message) => {
    expect(() => createDevelopmentPlayerConfig(env)).toThrow(message);
  });

  it('serves player-config.json with public no-store headers and passes other routes through', () => {
    const middleware = createDevelopmentPlayerConfigMiddleware(createDevelopmentPlayerConfig({}));
    const setHeader = vi.fn();
    const end = vi.fn();
    const next = vi.fn();

    middleware(
      { originalUrl: '/player-config.json?cache-bust=1' } as never,
      { setHeader, end } as never,
      next,
    );

    expect(next).not.toHaveBeenCalled();
    expect(setHeader).toHaveBeenCalledWith('Cache-Control', 'no-store');
    expect(setHeader).toHaveBeenCalledWith('Access-Control-Allow-Origin', '*');
    expect(JSON.parse(end.mock.calls[0][0])).toMatchObject({ schemaVersion: 1, protocol: 1 });

    middleware({ originalUrl: '/assets/index.js' } as never, {} as never, next);
    expect(next).toHaveBeenCalledOnce();
  });
});
