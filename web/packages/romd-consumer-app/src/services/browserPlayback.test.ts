import type { ConsumerReleaseDto, ConsumerReleaseManifestDto } from '@romd/consumer-api-client';
import emulatorJsPin from '../../../../tools/emulatorjs/pin.json';
import type { ConsumerTitleDetail } from '../types/consumerTitle';
import {
  browserPlaybackMaxBytes,
  browserPlaybackPlatforms,
  getBrowserPlaybackPreflight,
  getBrowserPlaybackSupport,
} from './browserPlayback';

const title = {
  id: 'title-1',
  system: {
    key: 'snes',
    name: 'Super Nintendo Entertainment System',
    compactLabel: 'SNES',
  },
  name: 'Super Metroid',
  description: null,
  publisher: null,
  developer: null,
  genre: null,
  releaseDate: null,
  players: null,
  rating: null,
  media: [],
  releases: [],
  defaultReleaseId: null,
} satisfies ConsumerTitleDetail;

const release = {
  id: 'release-1',
  name: 'Super Metroid',
  revision: 'Rev 1',
  regions: [
    'US',
  ],
  languages: [
    'en',
  ],
  sizeBytes: '3145728',
  isComplete: true,
} satisfies ConsumerReleaseDto;

describe('getBrowserPlaybackPreflight', () => {
  it('uses stable keys rather than editable labels for runtime selection', () => {
    expect(
      getBrowserPlaybackPreflight(
        {
          system: {
            key: 'snes',
            name: 'My console',
            compactLabel: 'CUSTOM',
          },
        },
        release,
      ).status,
    ).toBe('playable');
    expect(
      getBrowserPlaybackPreflight(
        {
          system: {
            key: 'unknown',
            name: 'Super Nintendo Entertainment System',
            compactLabel: 'SNES',
          },
        },
        release,
      ).status,
    ).toBe('download-only');
  });

  it('allows complete small Releases on mapped cartridge platforms', () => {
    const preflight = getBrowserPlaybackPreflight(title, release);

    expect(preflight.status).toBe('playable');
    if (preflight.status === 'playable') {
      expect(preflight.systemName).toBe('SNES');
      expect(preflight.maxBytes).toBe(browserPlaybackMaxBytes);
    }
  });

  it('keeps large Releases Download-only before requesting a manifest', () => {
    const preflight = getBrowserPlaybackPreflight(title, {
      ...release,
      sizeBytes: String(browserPlaybackMaxBytes + 1),
    });

    expect(preflight.status).toBe('download-only');
    if (preflight.status === 'download-only') {
      expect(preflight.reason).toMatch(/large releases/i);
    }
  });

  it('keeps valid Int64 sizes above JavaScript precision Download-only without throwing', () => {
    const preflight = getBrowserPlaybackPreflight(title, {
      ...release,
      sizeBytes: '9007199254740992',
    });

    expect(preflight.status).toBe('download-only');
  });
});

describe('getBrowserPlaybackSupport', () => {
  it('keeps the platform registry aligned with the deployed player core pin', () => {
    expect(
      [
        ...new Set(browserPlaybackPlatforms.map((platform) => platform.core)),
      ].sort(),
    ).toEqual(
      [
        ...emulatorJsPin.cores,
      ].sort(),
    );
  });

  it.each([
    [
      'n64',
      'z64',
      'mupen64plus_next',
    ],
    [
      'n64',
      'n64',
      'mupen64plus_next',
    ],
    [
      'n64',
      'v64',
      'mupen64plus_next',
    ],
    [
      'genesis',
      'md',
      'genesis_plus_gx',
    ],
    [
      'genesis',
      'gen',
      'genesis_plus_gx',
    ],
    [
      'genesis',
      'bin',
      'genesis_plus_gx',
    ],
    [
      'genesis',
      'smd',
      'genesis_plus_gx',
    ],
    [
      'sms',
      'sms',
      'smsplus',
    ],
    [
      'gg',
      'gg',
      'genesis_plus_gx',
    ],
  ])('launches %s .%s only when %s is advertised', (key, extension, core) => {
    const platformTitle = {
      ...title,
      system: {
        key,
        name: 'Custom label',
        compactLabel: 'Custom',
      },
    };
    const manifest = {
      ...createManifest(`Games/Game.${extension.toUpperCase()}`, '1024'),
      systemKey: key,
    };
    expect(
      getBrowserPlaybackPreflight(platformTitle, release, [
        core,
      ]).status,
    ).toBe('playable');
    expect(
      getBrowserPlaybackSupport(platformTitle, release, manifest, [
        core,
      ]),
    ).toMatchObject({
      status: 'supported',
      candidate: {
        core,
        sizeBytes: 1024,
      },
    });
    expect(getBrowserPlaybackPreflight(platformTitle, release, []).status).toBe('download-only');
    expect(getBrowserPlaybackSupport(platformTitle, release, manifest, []).status).toBe(
      'unsupported',
    );
  });

  it.each([
    [
      'n64',
      'zip',
    ],
    [
      'n64',
      'ndd',
    ],
    [
      'n64',
      'bin',
    ],
    [
      'nes',
      'z64',
    ],
    [
      'genesis',
      'gg',
    ],
    [
      'gg',
      'md',
    ],
    [
      'sms',
      'gg',
    ],
    [
      'segacd',
      'bin',
    ],
    [
      '32x',
      'bin',
    ],
    [
      'psx',
      'bin',
    ],
    [
      'genesis',
      'cue',
    ],
    [
      'genesis',
      'chd',
    ],
    [
      'genesis',
      'zip',
    ],
    [
      'gg',
      '7z',
    ],
  ])('does not infer support for %s from a .%s file', (key, extension) => {
    const platformTitle = {
      ...title,
      system: {
        key,
        name: 'Genesis',
        compactLabel: 'Genesis',
      },
    };
    const manifest = {
      ...createManifest(`game.${extension}`, '1024'),
      systemKey: key,
    };
    expect(
      getBrowserPlaybackSupport(platformTitle, release, manifest, emulatorJsPin.cores).status,
    ).toBe('unsupported');
  });

  it.each([
    [
      browserPlaybackMaxBytes,
      'playable',
      'supported',
    ],
    [
      browserPlaybackMaxBytes + 1,
      'download-only',
      'unsupported',
    ],
  ])('enforces the N64 full-object limit at %s bytes', (size, preflightStatus, supportStatus) => {
    const n64Title = {
      ...title,
      system: {
        key: 'n64',
        name: 'Nintendo 64',
        compactLabel: 'N64',
      },
    };
    const n64Release = {
      ...release,
      sizeBytes: String(size),
    };
    const manifest = {
      ...createManifest('Game.z64', String(size)),
      systemKey: 'n64',
    };
    expect(getBrowserPlaybackPreflight(n64Title, n64Release, emulatorJsPin.cores).status).toBe(
      preflightStatus,
    );
    expect(
      getBrowserPlaybackSupport(n64Title, n64Release, manifest, emulatorJsPin.cores).status,
    ).toBe(supportStatus);
  });

  it('maps a small SNES manifest item to the EmulatorJS SNES core', () => {
    const support = getBrowserPlaybackSupport(
      title,
      release,
      createManifest('SNES/Super Metroid.sfc', '3145728'),
    );

    expect(support.status).toBe('supported');
    if (support.status === 'supported') {
      expect(support.candidate.core).toBe('snes9x');
      expect(support.candidate.systemName).toBe('SNES');
      expect(support.candidate.sizeBytes).toBe(3145728);
    }
  });

  it('maps NES and Game Boy manifest items to concrete EmulatorJS cores', () => {
    const nesSupport = getBrowserPlaybackSupport(
      {
        ...title,
        system: {
          key: 'nes',
          name: 'Nintendo Entertainment System',
          compactLabel: 'Nintendo Entertainment System',
        },
      },
      release,
      createManifest('NES/Metroid.nes', '262144'),
    );
    const gameBoySupport = getBrowserPlaybackSupport(
      {
        ...title,
        system: {
          key: 'gbc',
          name: 'Game Boy Color',
          compactLabel: 'Game Boy Color',
        },
      },
      release,
      createManifest('GBC/Link.gb', '1048576'),
    );

    expect(nesSupport.status).toBe('supported');
    if (nesSupport.status === 'supported') {
      expect(nesSupport.candidate.core).toBe('fceumm');
    }

    expect(gameBoySupport.status).toBe('supported');
    if (gameBoySupport.status === 'supported') {
      expect(gameBoySupport.candidate.core).toBe('gambatte');
    }
  });

  it('rejects full-object candidates above the spike size ceiling', () => {
    const support = getBrowserPlaybackSupport(
      title,
      release,
      createManifest('SNES/Large.sfc', String(browserPlaybackMaxBytes + 1)),
    );

    expect(support.status).toBe('unsupported');
    if (support.status === 'unsupported') {
      expect(support.reason).toMatch(/too large/i);
    }
  });

  it('rejects valid Int64 manifest sizes above JavaScript precision without throwing', () => {
    const support = getBrowserPlaybackSupport(
      title,
      release,
      createManifest('SNES/Very Large.sfc', '9007199254740992'),
    );

    expect(support.status).toBe('unsupported');
  });

  it('rejects manifests with more than one item as multi-file', () => {
    const manifest = createManifest('SNES/Super Metroid.sfc', '3145728');
    manifest.runtime.contentType = 'unknown';
    manifest.items = [
      ...manifest.items,
      {
        ...manifest.items[0],
        relativePath: 'SNES/Super Metroid (Disc 2).sfc',
      },
    ];

    const support = getBrowserPlaybackSupport(title, release, manifest);

    expect(support.status).toBe('unsupported');
    if (support.status === 'unsupported') {
      expect(support.reason).toMatch(/multi-file/i);
    }
  });

  it('keeps a small PS2 CHD with PCSX2 runtime metadata unsupported in the browser', () => {
    const ps2Title = {
      ...title,
      system: {
        key: 'ps2',
        name: 'PlayStation 2',
        compactLabel: 'PS2',
      },
    };
    const ps2Release = {
      ...release,
      sizeBytes: '8192',
    };
    const manifest = {
      ...createManifest('PS2/Game.iso', '8192'),
      systemKey: 'ps2',
    };
    manifest.items[0].sha256 = 'a'.repeat(64);
    manifest.items[0].formats = [
      {
        format: 'chd',
        label: 'CHD',
        fileExtension: '.chd',
        state: 'available',
        relativePath: 'PS2/Game.chd',
        filename: 'Game.chd',
        sizeBytes: '4096',
        sha256: 'b'.repeat(64),
        profileVersion: 'chd-observed/v1',
        runtimeApplicability: [
          'pcsx2',
        ],
        runtime: {
          contentType: 'single_rom',
          launch: {
            type: 'file',
            relativePath: 'PS2/Game.chd',
          },
          packaging: 'direct_files',
          minimumInstallBytes: '4096',
        },
        contentGrant: {
          downloadUrl: '/delivery/content/chd',
          expiresAt: '2026-10-01T12:00:00Z',
        },
      },
    ];
    const availableCores = [
      ...emulatorJsPin.cores,
      'pcsx2',
    ];
    const preflight = getBrowserPlaybackPreflight(ps2Title, ps2Release, availableCores);
    expect(preflight.status).toBe('download-only');
    if (preflight.status === 'download-only') expect(preflight.reason).toMatch(/not mapped/i);
    const support = getBrowserPlaybackSupport(ps2Title, ps2Release, manifest, availableCores);
    expect(support.status).toBe('unsupported');
    if (support.status === 'unsupported')
      expect(support.reason).toMatch(/cartridge emulator core/i);
  });

  it('rejects unsupported cartridge mappings', () => {
    const support = getBrowserPlaybackSupport(
      {
        ...title,
        system: {
          key: 'psx',
          name: 'PlayStation',
          compactLabel: 'PlayStation',
        },
      },
      release,
      createManifest('PSX/Game.bin', '3145728'),
    );

    expect(support.status).toBe('unsupported');
    if (support.status === 'unsupported') {
      expect(support.reason).toMatch(/cartridge emulator core/i);
    }
  });

  it('gates mapped platforms against cores advertised by the player origin', () => {
    const preflight = getBrowserPlaybackPreflight(title, release, [
      'mgba',
    ]);
    expect(preflight.status).toBe('download-only');
    if (preflight.status === 'download-only') {
      expect(preflight.reason).toMatch(/not mapped/i);
    }

    const support = getBrowserPlaybackSupport(
      title,
      release,
      createManifest('SNES/Super Metroid.sfc', '3145728'),
      [
        'mgba',
      ],
    );
    expect(support.status).toBe('unsupported');
  });

  it('keeps advertised cores playable under a narrowed selection', () => {
    const preflight = getBrowserPlaybackPreflight(
      {
        ...title,
        system: {
          key: 'gba',
          name: 'Game Boy Advance',
          compactLabel: 'Game Boy Advance',
        },
      },
      release,
      [
        'mgba',
      ],
    );
    expect(preflight.status).toBe('playable');
  });
});

function createManifest(relativePath: string, sizeBytes: string): ConsumerReleaseManifestDto {
  return {
    isDeliveryComplete: true,
    serverInstanceId: '11111111-1111-4111-8111-111111111111',

    releaseId: 'release-1',
    titleId: 'title-1',

    systemKey: 'snes',
    name: 'Super Metroid',
    revision: 'Rev 1',
    isComplete: true,
    // Runtime values mirror the real backend contract (ConsumerReleaseManifestRepository):
    // contentType is the runtime kind ("single_rom" | "unknown"), packaging is always
    // "direct_files". Single-file-ness is defined by the item count, not a vocabulary.
    runtime: {
      contentType: 'single_rom',
      launch: {
        type: 'file',
        relativePath,
      },
      packaging: 'direct_files',
      minimumInstallBytes: sizeBytes,
    },
    items: [
      {
        relativePath,
        role: 'rom',
        sizeBytes,
        sha256: 'abc123',
        isAvailable: true,
        contentGrant: {
          downloadUrl: '/delivery/content/signed-token',
          expiresAt: '2026-06-05T12:00:00Z',
        },
      },
    ],
  };
}
