import type { Platform } from '@romd/admin-api-client';

export const platformFixtures = {
  list: [
    {
      id: 'plat-1',
      name: 'Super Nintendo Entertainment System',
      shortName: 'SNES',
      manufacturer: 'Nintendo',
      igdbPlatformId: '19',
    },
    {
      id: 'plat-2',
      name: 'Sega Genesis',
      shortName: 'Genesis',
      manufacturer: 'Sega',
      igdbPlatformId: '29',
    },
    {
      id: 'plat-3',
      name: 'Nintendo Entertainment System',
      shortName: 'NES',
      manufacturer: 'Nintendo',
      igdbPlatformId: '18',
    },
    {
      id: 'plat-4',
      name: 'Sony PlayStation',
      shortName: 'PS1',
      manufacturer: 'Sony',
      igdbPlatformId: '7',
    },
  ] satisfies Platform[],

  single: {
    id: 'plat-1',
    name: 'Super Nintendo Entertainment System',
    shortName: 'SNES',
    manufacturer: 'Nintendo',
    igdbPlatformId: '19',
  } satisfies Platform,

  empty: [] satisfies Platform[],
};
