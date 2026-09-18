import {
  deriveEmulatorJsDataPath,
  type EmulatorJsPin,
  fetchPlayerRuntimeConfig,
  PlayerConfigError,
  parseEmulatorJsPin,
  parsePlayerRuntimeConfig,
} from './config';

const pin: EmulatorJsPin = {
  schemaVersion: 2,
  version: '4.2.3',
  cores: ['fceumm', 'snes9x'],
};

const validConfig = {
  schemaVersion: 1,
  protocol: 1,
  emulatorJs: {
    version: '4.2.3',
    source: 'cdn',
    dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
  },
  cores: ['fceumm'],
  allowedParents: ['https://romd.example', 'http://romd.lan'],
};

describe('EmulatorJS config', () => {
  it('derives the version-pinned CDN path and rejects the Phase 2 bundled source', () => {
    expect(deriveEmulatorJsDataPath(pin, 'cdn')).toBe(
      'https://cdn.emulatorjs.org/4.2.3/data/',
    );
    expect(() => deriveEmulatorJsDataPath(pin, 'bundled')).toThrow('deferred to Phase 2');
    expect(() =>
      deriveEmulatorJsDataPath(pin, 'bundled', 'https://assets.romd.example/ejs/4.2.3/data/'),
    ).toThrow('deferred to Phase 2');
  });

  it('uses a validated explicit dataPath override first', () => {
    expect(deriveEmulatorJsDataPath(pin, 'cdn', 'https://assets.romd.example/ejs/4.2.3/data/')).toBe(
      'https://assets.romd.example/ejs/4.2.3/data/',
    );
  });

  it('accepts a canonical root-relative dataPath', () => {
    expect(deriveEmulatorJsDataPath(pin, 'cdn', '/mirror/emulatorjs/4.2.3/data/')).toBe(
      '/mirror/emulatorjs/4.2.3/data/',
    );
  });

  it.each([
    ['network path', '//assets.example/emulatorjs/4.2.3/data/'],
    ['backslash', '/vendor\\emulatorjs\\4.2.3\\data/'],
    ['encoded separator', '/vendor/%2f%2fassets.example/data/'],
    ['encoded stable alias', '/vendor/%73table/data/'],
    ['double-encoded stable alias', '/vendor/%2573table/data/'],
    ['normalized stable alias', '/vendor/4.2.3/../stable/data/'],
    ['normalized latest alias', '/vendor/4.2.3/../latest/data/'],
    ['absolute encoded latest alias', 'https://assets.example/emulatorjs/%6catest/data/'],
    ['encoded dot segment', '/vendor/%2e%2e/4.2.3/data/'],
  ])('rejects a %s dataPath', (_label, dataPath) => {
    expect(() => deriveEmulatorJsDataPath(pin, 'cdn', dataPath)).toThrow(PlayerConfigError);
  });

  it('rejects legacy and floating EmulatorJS pins', () => {
    expect(() => parseEmulatorJsPin({ schemaVersion: 1, version: '4.2.3', cores: ['fceumm'] })).toThrow(
      PlayerConfigError,
    );
    expect(() => parseEmulatorJsPin({ schemaVersion: 2, version: 'latest', cores: ['fceumm'] })).toThrow(
      'numeric and fully pinned',
    );
  });

  it('parses the full runtime config contract', () => {
    expect(parsePlayerRuntimeConfig(validConfig, pin)).toEqual(validConfig);
  });

  it.each([
    ['wrong protocol', { ...validConfig, protocol: 2 }],
    [
      'Phase 2 bundled source',
      { ...validConfig, emulatorJs: { ...validConfig.emulatorJs, source: 'bundled' } },
    ],
    [
      'floating asset path',
      {
        ...validConfig,
        emulatorJs: { ...validConfig.emulatorJs, dataPath: 'https://cdn.emulatorjs.org/latest/data/' },
      },
    ],
    [
      'port-qualified official CDN mismatch',
      {
        ...validConfig,
        emulatorJs: {
          ...validConfig.emulatorJs,
          dataPath: 'https://cdn.emulatorjs.org:8443/4.2.4/data/',
        },
      },
    ],
    [
      'insecure official CDN path',
      {
        ...validConfig,
        emulatorJs: {
          ...validConfig.emulatorJs,
          dataPath: 'http://cdn.emulatorjs.org/4.2.3/data/',
        },
      },
    ],
    ['unknown core', { ...validConfig, cores: ['mgba'] }],
    ['non-origin parent', { ...validConfig, allowedParents: ['https://romd.example/portal'] }],
  ])('rejects %s', (_label, config) => {
    expect(() => parsePlayerRuntimeConfig(config, pin)).toThrow(PlayerConfigError);
  });

  it('fetches the public runtime config without credentials or caching', async () => {
    const fetchImplementation = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify(validConfig), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    await expect(fetchPlayerRuntimeConfig(fetchImplementation)).resolves.toEqual(validConfig);
    expect(fetchImplementation).toHaveBeenCalledWith('/player-config.json', {
      cache: 'no-store',
      credentials: 'omit',
    });
  });
});
