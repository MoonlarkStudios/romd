import { createHash, webcrypto } from 'node:crypto';
import { client } from '@romd/admin-api-client';
import { HttpResponse, http } from 'msw';
import { afterEach } from 'vitest';
import type { AssembledReferenceCatalog } from '../../utils/referenceCatalog';
import { canonicalReferenceJson } from '../../utils/referenceCatalogCanonical';
import { server } from '../msw/server';

afterEach(() =>
  client.setConfig({
    baseUrl: '',
  }),
);

type Fixture = Partial<AssembledReferenceCatalog>;
const routes = {
  systems: 'systems',
  companies: 'companies',
  regions: 'regions',
  languages: 'languages',
  ratingBoards: 'rating-boards',
  ratings: 'ratings',
} as const;

/** Install the actual manifest/six-feed boundary; legacy UI fixtures only supply facts they display. */
export function installReferenceCatalog(fixture: Fixture = {}) {
  // Node's Request requires an absolute URL even though browser fetch accepts relative routes.
  client.setConfig({
    baseUrl: window.location.origin,
  });
  const catalog: AssembledReferenceCatalog = {
    schemaVersion: 1,
    builtInVersion: fixture.builtInVersion ?? 1,
    revision: /^[a-f0-9]{64}$/.test(fixture.revision ?? '')
      ? (fixture.revision ?? '')
      : 'a'.repeat(64),
    systems: (fixture.systems ?? []).map((item) => ({
      manufacturerKeys: null,
      retired: false,
      manufacturers: null,
      ...item,
    })),
    companies: (fixture.companies ?? []).map((item) => ({
      retired: false,
      ...item,
    })),
    regions: (fixture.regions ?? []).map((item) => ({
      retired: false,
      ...item,
    })),
    languages: (fixture.languages ?? []).map((item) => ({
      retired: false,
      ...item,
    })),
    ratingBoards: (fixture.ratingBoards ?? []).map((item) => ({
      retired: false,
      ...item,
    })),
    ratings: (fixture.ratings ?? []).map((item) => ({
      designation: null,
      minimumAge: null,
      retired: false,
      ...item,
    })),
  };
  const descriptor = {
    schemaVersion: 1,
    builtInVersion: catalog.builtInVersion,
    revision: 'b'.repeat(64),
    catalogRevision: catalog.revision,
    contentHash: createHash('sha256').update(canonicalReferenceJson(catalog)).digest('hex'),
    counts: {
      systems: catalog.systems.length,
      companies: catalog.companies.length,
      regions: catalog.regions.length,
      languages: catalog.languages.length,
      ratingBoards: catalog.ratingBoards.length,
      ratings: catalog.ratings.length,
    },
  };
  // jsdom lacks SubtleCrypto; use the platform implementation, not a mock digest.
  Object.defineProperty(globalThis, 'crypto', {
    configurable: true,
    value: webcrypto,
  });
  const requests: URL[] = [];
  const root = () => ({
    data: descriptor,
    response: new Response(JSON.stringify(descriptor), {
      status: 200,
      headers: {
        ETag: `"${descriptor.revision}"`,
      },
    }),
  });
  server.use(
    http.get('/api/catalog-snapshot', ({ request }) => {
      requests.push(new URL(request.url));
      return HttpResponse.json(descriptor, {
        headers: {
          ETag: `"${descriptor.revision}"`,
        },
      });
    }),
    ...Object.entries(routes).map(([family, route]) =>
      http.get(`/api/catalog-snapshot/${route}`, ({ request }) => {
        requests.push(new URL(request.url));
        const query = new URL(request.url).searchParams;
        if (query.get('revision') !== catalog.revision)
          return HttpResponse.json(
            {
              type: 'about:blank',
              title: 'Snapshot changed',
              status: 409,
              errorCode: 'ReferenceCatalog.SnapshotChanged',
              errors: {},
            },
            {
              status: 409,
            },
          );
        const offset = Number(query.get('cursor') ?? '0');
        const limit = Number(query.get('limit') ?? '100');
        const items = catalog[family as keyof typeof routes];
        const nextCursor = offset + limit < items.length ? String(offset + limit) : null;
        return HttpResponse.json({
          items: items.slice(offset, offset + limit),
          nextCursor,
          hasNextPage: nextCursor !== null,
        });
      }),
    ),
  );
  return {
    catalog,
    descriptor,
    root,
    requests,
  };
}
