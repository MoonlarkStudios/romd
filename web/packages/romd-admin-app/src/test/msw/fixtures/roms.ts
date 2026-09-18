import type { LibraryStats, PageOfRom, Rom } from '@romd/admin-api-client';

// Extended list with more items for pagination testing
const catalogedRoms: Rom[] = [
  {
    id: 'rom-1',
    originalFilename: 'Super Mario World (USA).sfc',
    size: '524288',
    sha1: 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2',
    md5: 'abc123def456abc123def456abc123de',
    crc32: 'DEADBEEF',
    importedAt: '2024-01-15T10:30:00Z',
    status: 'Cataloged',
    matches: [
      {
        datId: 'dat-1',
        datName: 'No-Intro - Super Nintendo Entertainment System',
        gameName: 'Super Mario World (USA)',
        romName: 'Super Mario World (USA).sfc',
        titleId: 'title-1',
        titleName: 'Super Mario World',
      },
    ],
  },
  {
    id: 'rom-2',
    originalFilename: 'Sonic the Hedgehog (USA).md',
    size: '1048576',
    sha1: 'b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3',
    md5: 'def456abc123def456abc123def456ab',
    crc32: 'CAFEBABE',
    importedAt: '2024-01-16T14:20:00Z',
    status: 'Cataloged',
    matches: [
      {
        datId: 'dat-2',
        datName: 'No-Intro - Sega Genesis',
        gameName: 'Sonic the Hedgehog (USA, Europe)',
        romName: 'Sonic the Hedgehog (USA).md',
        titleId: 'title-2',
        titleName: 'Sonic the Hedgehog',
      },
    ],
  },
  {
    id: 'rom-4',
    originalFilename: 'Zelda - A Link to the Past (USA).sfc',
    size: '1048576',
    sha1: 'd4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5',
    md5: 'aaa111bbb222ccc333ddd444eee555ff',
    crc32: 'AABBCCDD',
    importedAt: '2024-01-18T08:00:00Z',
    status: 'Cataloged',
    matches: [
      {
        datId: 'dat-1',
        datName: 'No-Intro - Super Nintendo Entertainment System',
        gameName: 'Legend of Zelda, The - A Link to the Past (USA)',
        romName: 'Zelda - A Link to the Past (USA).sfc',
        titleId: 'title-3',
        titleName: 'The Legend of Zelda: A Link to the Past',
      },
    ],
  },
  {
    id: 'rom-5',
    originalFilename: 'Metroid (USA).nes',
    size: '131072',
    sha1: 'e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6',
    md5: 'bbb222ccc333ddd444eee555fff666aa',
    crc32: 'EEFF0011',
    importedAt: '2024-01-19T11:30:00Z',
    status: 'Cataloged',
    matches: [
      {
        datId: 'dat-3',
        datName: 'No-Intro - Nintendo Entertainment System',
        gameName: 'Metroid (USA)',
        romName: 'Metroid (USA).nes',
        titleId: 'title-4',
        titleName: 'Metroid',
      },
    ],
  },
];

const unidentifiedRoms: Rom[] = [
  {
    id: 'rom-3',
    originalFilename: 'unknown-game.bin',
    size: '262144',
    sha1: 'c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4',
    importedAt: '2024-01-17T09:00:00Z',
    status: 'Unidentified',
    matches: null,
  },
  {
    id: 'rom-6',
    originalFilename: 'mystery-rom.bin',
    size: '524288',
    sha1: 'f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1',
    importedAt: '2024-01-20T15:45:00Z',
    status: 'Unidentified',
    matches: null,
  },
];

const unroutedRoms: Rom[] = [
  {
    id: 'rom-7',
    originalFilename: 'Street Fighter II (USA).sfc',
    size: '2097152',
    sha1: 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2',
    md5: 'ccc333ddd444eee555fff666aaa777bb',
    crc32: '11223344',
    importedAt: '2024-01-21T12:00:00Z',
    status: 'Unrouted',
    matches: null,
  },
];

// Combined list of all ROMs
const allRoms: Rom[] = [...catalogedRoms, ...unidentifiedRoms, ...unroutedRoms];

// Helper to paginate an array
function paginateArray<T extends { id: string }>(
  items: T[],
  cursor: string | null | undefined,
  limit: number
): { items: T[]; nextCursor: string | null; hasNextPage: boolean } {
  let startIndex = 0;

  if (cursor) {
    const cursorIndex = items.findIndex((item) => item.id === cursor);
    if (cursorIndex !== -1) {
      startIndex = cursorIndex + 1;
    }
  }

  const pageItems = items.slice(startIndex, startIndex + limit);
  const hasNextPage = startIndex + limit < items.length;
  const nextCursor = hasNextPage ? pageItems[pageItems.length - 1]?.id ?? null : null;

  return { items: pageItems, nextCursor, hasNextPage };
}

export const romFixtures = {
  // All ROMs (for useRoms hook)
  list: allRoms,

  // Separate lists by status
  cataloged: catalogedRoms,
  unidentified: unidentifiedRoms,
  unrouted: unroutedRoms,

  // Stats
  stats: {
    totalRomFiles: '7',
    catalogedCount: '4',
    unroutedCount: '1',
    unidentifiedCount: '2',
    totalSizeBytes: '5636096',
    platformBreakdown: [
      { systemKey: 'plat-1', platformName: 'Super Nintendo', ownedCount: '3', totalCount: '1500' },
      { systemKey: 'plat-2', platformName: 'Sega Genesis', ownedCount: '1', totalCount: '900' },
      { systemKey: 'plat-3', platformName: 'Nintendo Entertainment System', ownedCount: '1', totalCount: '800' },
    ],
  } satisfies LibraryStats,

  // Paginated response helpers
  catalogedPage: {
    items: catalogedRoms.slice(0, 2),
    nextCursor: 'rom-4',
    hasNextPage: true,
  } satisfies PageOfRom,

  // Empty states
  empty: [] satisfies Rom[],

  emptyStats: {
    totalRomFiles: '0',
    catalogedCount: '0',
    unroutedCount: '0',
    unidentifiedCount: '0',
    totalSizeBytes: '0',
    platformBreakdown: [],
  } satisfies LibraryStats,

  emptyPage: {
    items: [],
    nextCursor: null,
    hasNextPage: false,
  } satisfies PageOfRom,

  // Pagination helper function for dynamic pagination in handlers
  paginate: paginateArray,
};
