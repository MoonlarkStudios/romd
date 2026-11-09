import type {
  ConsumerRelease,
  ConsumerReleaseManifest,
  ConsumerReleaseManifestItem,
  ConsumerTitleDetail,
} from '@romd/consumer-api-client';
import { systemKeys } from '@romd/foundation/catalog';
import { PLAYER_PROTOCOL_LIMITS } from '@romd/player-protocol';
import { getDownloadFileName, type VerifiedDownload } from './downloadVerification';

// Cores ROMD knows how to map to a platform. The dedicated player may advertise
// fewer cores at runtime; mappings outside that capability set are inert.
export type EmulatorJsCore =
  | 'fceumm'
  | 'snes9x'
  | 'gambatte'
  | 'mgba'
  | 'genesis_plus_gx'
  | 'smsplus'
  | 'mupen64plus_next';

export const browserPlaybackMaxBytes = PLAYER_PROTOCOL_LIMITS.romBytes;

export type BrowserPlaybackPreflight =
  | {
      status: 'playable';
      systemName: string;
      maxBytes: number;
    }
  | {
      status: 'download-only';
      reason: string;
    };

export interface BrowserPlaybackCandidate {
  item: ConsumerReleaseManifestItem;
  core: EmulatorJsCore;
  systemName: string;
  sizeBytes: number;
}

export type BrowserPlaybackSupport =
  | {
      status: 'supported';
      candidate: BrowserPlaybackCandidate;
    }
  | {
      status: 'unsupported';
      reason: string;
    };

interface CoreMapping {
  core: EmulatorJsCore;
  systemName: string;
  systemKeys: (typeof systemKeys)[keyof typeof systemKeys][];
  extensions: string[];
}

export const browserPlaybackPlatforms: readonly CoreMapping[] = [
  {
    core: 'mupen64plus_next',
    systemName: 'Nintendo 64',
    systemKeys: [
      systemKeys.N64,
    ],
    extensions: [
      'z64',
      'n64',
      'v64',
    ],
  },
  {
    core: 'snes9x',
    systemName: 'SNES',
    systemKeys: [
      systemKeys.Snes,
    ],
    extensions: [
      'sfc',
      'smc',
      'fig',
      'swc',
    ],
  },
  {
    core: 'fceumm',
    systemName: 'NES',
    systemKeys: [
      systemKeys.Nes,
    ],
    extensions: [
      'nes',
      'fds',
    ],
  },
  {
    core: 'mgba',
    systemName: 'Game Boy Advance',
    systemKeys: [
      systemKeys.GameBoyAdvance,
    ],
    extensions: [
      'gba',
    ],
  },
  {
    core: 'gambatte',
    systemName: 'Game Boy',
    systemKeys: [
      systemKeys.GameBoy,
      systemKeys.GameBoyColor,
    ],
    extensions: [
      'gb',
      'gbc',
    ],
  },
  {
    core: 'genesis_plus_gx',
    systemName: 'Genesis / Mega Drive',
    systemKeys: [
      systemKeys.Genesis,
    ],
    extensions: [
      'md',
      'gen',
      'bin',
      'smd',
    ],
  },
  {
    core: 'smsplus',
    systemName: 'Master System',
    systemKeys: [
      systemKeys.MasterSystem,
    ],
    extensions: [
      'sms',
    ],
  },
  {
    core: 'genesis_plus_gx',
    systemName: 'Game Gear',
    systemKeys: [
      systemKeys.GameGear,
    ],
    extensions: [
      'gg',
    ],
  },
];

export function getBrowserPlaybackPreflight(
  title: Pick<ConsumerTitleDetail, 'system'>,
  release: ConsumerRelease,
  availableCores?: readonly string[],
): BrowserPlaybackPreflight {
  if (!release.isComplete) {
    return downloadOnly('Incomplete Releases are Download-only until all files are available.');
  }

  if (toBrowserPlaybackSize(release.sizeBytes) === null) {
    return downloadOnly(
      'Large Releases are Download-only until range-capable launch cache support exists.',
    );
  }

  const mapping = findPlatformMapping(title.system.key, availableCores);
  if (!mapping) {
    return downloadOnly('This platform is not mapped to a browser emulator core yet.');
  }

  return {
    status: 'playable',
    systemName: mapping.systemName,
    maxBytes: browserPlaybackMaxBytes,
  };
}

export function getBrowserPlaybackSupport(
  title: Pick<ConsumerTitleDetail, 'system'>,
  release: ConsumerRelease,
  manifest: ConsumerReleaseManifest,
  availableCores?: readonly string[],
): BrowserPlaybackSupport {
  if (!release.isComplete || !manifest.isComplete) {
    return unsupported('Only complete cartridge Releases are eligible for browser playback.');
  }

  // Single-file-ness follows the backend's own definition (item count), not the
  // runtime string vocabulary: the API emits packaging "direct_files" for every
  // manifest and carries the kind in contentType ("single_rom" | "unknown").
  if (manifest.items.length !== 1) {
    return unsupported('Multi-file Releases are Download-only for browser playback.');
  }

  const candidates = manifest.items
    .filter((item) => item.isAvailable && item.sha256 && item.contentGrant)
    .map((item) => ({
      item,
      mapping: findCoreMapping(title.system.key, item.relativePath, availableCores),
      sizeBytes: toBrowserPlaybackSize(item.sizeBytes),
    }));

  const playable = candidates.find(({ mapping, sizeBytes }) => mapping && sizeBytes !== null);
  if (playable?.mapping && playable.sizeBytes !== null) {
    return {
      status: 'supported',
      candidate: {
        item: playable.item,
        core: playable.mapping.core,
        systemName: playable.mapping.systemName,
        sizeBytes: playable.sizeBytes,
      },
    };
  }

  if (candidates.some(({ sizeBytes }) => sizeBytes === null)) {
    return unsupported(
      'The available manifest item is too large for full-object browser playback.',
    );
  }

  return unsupported('No available manifest item maps to a cartridge emulator core.');
}

function toBrowserPlaybackSize(value: string): number | null {
  try {
    const size = BigInt(value);
    if (size < 0n || size > BigInt(browserPlaybackMaxBytes)) {
      return null;
    }

    return Number(size);
  } catch {
    return null;
  }
}

export function hasBrowserPlaybackPlatform(
  systemKey: string,
  availableCores: readonly string[],
): boolean {
  return findPlatformMapping(systemKey, availableCores) !== undefined;
}

export function getBrowserPlaybackGameName(
  title: Pick<ConsumerTitleDetail, 'name'>,
  release: ConsumerRelease,
): string {
  return release.revision ? `${title.name} ${release.revision}` : title.name;
}

export function getBrowserPlaybackFileName(file: VerifiedDownload): string {
  return getDownloadFileName(file.relativePath);
}

function findCoreMapping(
  systemKey: string,
  relativePath: string,
  availableCores?: readonly string[],
): CoreMapping | undefined {
  const extension = getFileExtension(relativePath);

  return getActiveMappings(availableCores).find(
    (mapping) => matchesPlatform(mapping, systemKey) && mapping.extensions.includes(extension),
  );
}

function findPlatformMapping(
  systemKey: string,
  availableCores?: readonly string[],
): CoreMapping | undefined {
  return getActiveMappings(availableCores).find((mapping) => matchesPlatform(mapping, systemKey));
}

function getActiveMappings(availableCores?: readonly string[]): readonly CoreMapping[] {
  if (!availableCores) {
    return browserPlaybackPlatforms;
  }
  const cores = new Set(availableCores);
  return browserPlaybackPlatforms.filter((mapping) => cores.has(mapping.core));
}

function getFileExtension(relativePath: string): string {
  const fileName = getDownloadFileName(relativePath).toLowerCase();
  const extensionStart = fileName.lastIndexOf('.');

  return extensionStart >= 0 ? fileName.slice(extensionStart + 1) : '';
}

function unsupported(reason: string): BrowserPlaybackSupport {
  return {
    status: 'unsupported',
    reason,
  };
}

function downloadOnly(reason: string): BrowserPlaybackPreflight {
  return {
    status: 'download-only',
    reason,
  };
}

function matchesPlatform(mapping: CoreMapping, systemKey: string): boolean {
  const normalizedPlatform = systemKey.toLowerCase();

  return mapping.systemKeys.some((candidate) => normalizedPlatform === candidate);
}
