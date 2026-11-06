// Test fixtures shared with host apps through `@romd/consumer-ui/testing`.
import type { ReactNode } from 'react';
import {
  type ReferenceCatalogData,
  ReferenceCatalogProvider,
} from '../components/metadata/ReferenceCatalog';

const icon = (name: string) => ({
  url: `/api/assets/${name}`,
  sha256: name,
  contentType: 'image/png',
  monochrome: false,
});
export const referenceCatalog: ReferenceCatalogData = {
  revision: 'fixture-v1',
  systems: [
    {
      key: 'snes',
      name: 'Super Nintendo Entertainment System',
      compactLabel: 'SNES',
      icon: icon('snes'),
    },
  ],
  ratingBoards: [
    {
      key: 'Esrb',
      name: 'ESRB',
    },
    {
      key: 'Pegi',
      name: 'PEGI',
    },
  ],
  ratings: [
    {
      board: 'Esrb',
      code: 'E',
      name: 'ESRB E',
      description: 'ESRB Everyone',
      icon: icon('everyone'),
    },
    {
      board: 'Esrb',
      code: 'T',
      name: 'ESRB T',
      description: 'ESRB Teen',
      icon: icon('teen'),
    },
    {
      board: 'Pegi',
      code: 'PEGI 7',
      name: 'PEGI 7',
      icon: icon('pegi7'),
    },
    {
      board: 'Pegi',
      code: 'PEGI 12',
      name: 'PEGI 12',
      icon: icon('pegi12'),
    },
  ],
};
export function CatalogFixture({ children }: { children: ReactNode }) {
  return <ReferenceCatalogProvider catalog={referenceCatalog}>{children}</ReferenceCatalogProvider>;
}
