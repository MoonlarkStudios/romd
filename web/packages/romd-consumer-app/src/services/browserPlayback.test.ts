import type {
  ConsumerReleaseDto,
  ConsumerReleaseManifestDto,
  ConsumerTitleDetailDto,
} from '@romd/consumer-api-client';
import {
  browserPlaybackMaxBytes,
  getBrowserPlaybackPreflight,
  getBrowserPlaybackSupport,
} from './browserPlayback';

const title = {
  id: 'title-1',
  system: { key: 'snes', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' },
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
} satisfies ConsumerTitleDetailDto;

const release = {
  id: 'release-1',
  name: 'Super Metroid',
  revision: 'Rev 1',
  regions: ['US'],
  languages: ['en'],
  sizeBytes: '3145728',
  isComplete: true,
} satisfies ConsumerReleaseDto;

describe('getBrowserPlaybackPreflight', () => {
  it('uses stable keys rather than editable labels for runtime selection', () => {
    expect(getBrowserPlaybackPreflight({ system: { key: 'snes', name: 'My console', compactLabel: 'CUSTOM' } }, release).status).toBe('playable');
    expect(getBrowserPlaybackPreflight({ system: { key: 'unknown', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' } }, release).status).toBe('download-only');
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
  it('maps a small SNES manifest item to the EmulatorJS SNES core', () => {
    const support = getBrowserPlaybackSupport(title, release, createManifest('SNES/Super Metroid.sfc', '3145728'));

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
        system: { key: 'nes', name: 'Nintendo Entertainment System', compactLabel: 'Nintendo Entertainment System' },
      },
      release,
      createManifest('NES/Metroid.nes', '262144'),
    );
    const gameBoySupport = getBrowserPlaybackSupport(
      {
        ...title,
        system: { key: 'gbc', name: 'Game Boy Color', compactLabel: 'Game Boy Color' },
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

  it('rejects unsupported cartridge mappings', () => {
    const support = getBrowserPlaybackSupport(
      {
        ...title,
        system: { key: 'psx', name: 'PlayStation', compactLabel: 'PlayStation' },
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
    const preflight = getBrowserPlaybackPreflight(title, release, ['mgba']);
    expect(preflight.status).toBe('download-only');
    if (preflight.status === 'download-only') {
      expect(preflight.reason).toMatch(/not mapped/i);
    }

    const support = getBrowserPlaybackSupport(
      title,
      release,
      createManifest('SNES/Super Metroid.sfc', '3145728'),
      ['mgba'],
    );
    expect(support.status).toBe('unsupported');
  });

  it('keeps advertised cores playable under a narrowed selection', () => {
    const preflight = getBrowserPlaybackPreflight(
      {
        ...title,
        system: { key: 'gba', name: 'Game Boy Advance', compactLabel: 'Game Boy Advance' },
      },
      release,
      ['mgba'],
    );
    expect(preflight.status).toBe('playable');
  });
});

function createManifest(relativePath: string, sizeBytes: string): ConsumerReleaseManifestDto {
  return {
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
