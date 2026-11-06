import { waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { describe, expect, it } from 'vitest';
import { installReferenceCatalog } from '../test/fixtures/referenceCatalog';
import { server } from '../test/msw/server';
import { type AssembledReferenceCatalog, loadAdminReferenceCatalog } from './referenceCatalog';

const system = (key: string) => ({
  key,
  name: `Élan 😀 <>& ${key}`,
  compactLabel: key,
  description: null,
  icon: null,
  manufacturerKeys: [],
  manufacturers: [],
  retired: false,
});
const company = (key: string) => ({
  key,
  name: key,
  description: null,
  retired: false,
});
const taxonomy = (key: string) => ({
  ...company(key),
  sortOrder: 0,
  languageCode: null,
});
const rating = (board: string, code: string) => ({
  board,
  code,
  name: code,
  description: null,
  icon: null,
  designation: null,
  minimumAge: null,
  retired: false,
});
const conflict = () =>
  HttpResponse.json(
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
const load = () => loadAdminReferenceCatalog(new AbortController().signal);
const page = (items: unknown[], nextCursor: string | null = null) => ({
  items,
  nextCursor,
  hasNextPage: nextCursor !== null,
});

describe('coherent admin reference catalog transport', () => {
  it('traverses every family beyond a page boundary and verifies the unchanged complete content', async () => {
    const keys = Array.from(
      {
        length: 101,
      },
      (_, index) => `local-${String(index).padStart(3, '0')}`,
    );
    const fixture = installReferenceCatalog({
      systems: keys.map(system),
      companies: keys.map(company),
      regions: keys.map(taxonomy),
      languages: keys.map(taxonomy),
      ratingBoards: keys.map(company),
      ratings: keys.map((key) => rating(key, 'E')),
    });
    await expect(load()).resolves.toEqual(fixture.catalog);
    const pages = fixture.requests.filter((url) => url.pathname !== '/api/catalog-snapshot');
    expect(pages).toHaveLength(12);
    expect(
      pages.every(
        (url) =>
          url.searchParams.get('revision') === fixture.catalog.revision &&
          url.searchParams.get('limit') === '100',
      ),
    ).toBe(true);
    expect(pages.filter((url) => url.searchParams.get('cursor') === '100')).toHaveLength(6);
  });
  it('accepts equal rating codes belonging to distinct boards as distinct compound identities', async () => {
    const fixture = installReferenceCatalog({
      ratingBoards: [
        company('A'),
        company('B'),
      ],
      ratings: [
        rating('A', 'E'),
        rating('B', 'E'),
      ],
    });
    await expect(load()).resolves.toEqual(fixture.catalog);
  });
  it('discards all old families and restarts once after a real SnapshotChanged problem', async () => {
    const original = installReferenceCatalog({
      systems: [
        system('old'),
      ],
      companies: [
        company('old-company'),
      ],
    });
    let replacement: ReturnType<typeof installReferenceCatalog> | undefined;
    server.use(
      http.get('/api/catalog-snapshot/companies', () => {
        replacement = installReferenceCatalog({
          revision: 'c'.repeat(64),
          systems: [
            system('new'),
          ],
          companies: [
            company('new-company'),
          ],
        });
        return conflict();
      }),
    );
    const catalog = await load();
    expect(original.requests.some((url) => url.pathname.endsWith('/systems'))).toBe(true);
    expect(catalog).toEqual(replacement?.catalog);
    expect(catalog.systems.map((item) => item.key)).toEqual([
      'new',
    ]);
    expect(catalog.companies.map((item) => item.key)).toEqual([
      'new-company',
    ]);
  });
  it('stops after the second conflict instead of multiplying retries', async () => {
    const fixture = installReferenceCatalog();
    server.use(http.get('/api/catalog-snapshot/systems', conflict));
    await expect(load()).rejects.toThrow('changed while loading');
    expect(fixture.requests.filter((url) => url.pathname === '/api/catalog-snapshot')).toHaveLength(
      2,
    );
  });
  it('does not restart an unrelated 409 problem', async () => {
    const fixture = installReferenceCatalog();
    server.use(
      http.get('/api/catalog-snapshot/systems', () =>
        HttpResponse.json(
          {
            errorCode: 'Other.Conflict',
          },
          {
            status: 409,
          },
        ),
      ),
    );
    await expect(load()).rejects.toThrow('could not be loaded');
    expect(fixture.requests).toHaveLength(1);
  });
  it.each([
    [
      'duplicate identity',
      (rows: AssembledReferenceCatalog['systems']) =>
        page([
          rows[0],
          rows[0],
        ]),
    ],
    [
      'wrong count',
      (rows: AssembledReferenceCatalog['systems']) =>
        page([
          rows[0],
        ]),
    ],
    [
      'unknown record field',
      (rows: AssembledReferenceCatalog['systems']) =>
        page([
          {
            ...rows[0],
            unexpected: true,
          },
          rows[1],
        ]),
    ],
    [
      'missing explicit null',
      (rows: AssembledReferenceCatalog['systems']) =>
        page(rows.map(({ description: _description, ...rest }) => rest)),
    ],
    [
      'nonterminal empty progress',
      () => page([], 'unique-cursor'),
    ],
    [
      'incoherent terminal state',
      (rows: AssembledReferenceCatalog['systems']) => ({
        ...page(rows),
        hasNextPage: true,
      }),
    ],
  ] as const)('rejects %s before publishing any data', async (_name, body) => {
    const fixture = installReferenceCatalog({
      systems: [
        system('one'),
        system('two'),
      ],
    });
    server.use(
      http.get('/api/catalog-snapshot/systems', () =>
        HttpResponse.json(body(fixture.catalog.systems)),
      ),
    );
    await expect(load()).rejects.toThrow();
  });
  it('rejects a repeated cursor with distinct records rather than following it forever', async () => {
    const fixture = installReferenceCatalog({
      systems: [
        system('one'),
        system('two'),
        system('three'),
      ],
    });
    let requests = 0;
    server.use(
      http.get('/api/catalog-snapshot/systems', () =>
        HttpResponse.json(
          page(
            [
              fixture.catalog.systems[requests++],
            ],
            'repeat',
          ),
        ),
      ),
    );
    await expect(load()).rejects.toThrow('repeated a page cursor');
    expect(requests).toBe(2);
  });
  it('rejects reordered content even when identities, counts and shapes match', async () => {
    const fixture = installReferenceCatalog({
      systems: [
        system('one'),
        system('two'),
      ],
    });
    server.use(
      http.get('/api/catalog-snapshot/systems', () =>
        HttpResponse.json(
          page(
            [
              ...fixture.catalog.systems,
            ].reverse(),
          ),
        ),
      ),
    );
    await expect(load()).rejects.toThrow('content hash mismatch');
  });
  it('fails a late family without returning the already assembled families', async () => {
    installReferenceCatalog({
      systems: [
        system('one'),
      ],
    });
    server.use(
      http.get('/api/catalog-snapshot/ratings', () =>
        HttpResponse.json(
          {
            errorCode: 'Server.Unavailable',
          },
          {
            status: 503,
          },
        ),
      ),
    );
    await expect(load()).rejects.toThrow('could not be loaded');
  });
  it.each([
    {
      schemaVersion: 2,
    },
    {
      contentHash: 'bad',
    },
    {
      counts: {
        systems: 1,
      },
    },
  ])('rejects malformed descriptor %j before page requests', async (change) => {
    const fixture = installReferenceCatalog();
    server.use(
      http.get('/api/catalog-snapshot', () =>
        HttpResponse.json(
          {
            ...fixture.descriptor,
            ...change,
          },
          {
            headers: {
              ETag: `"${fixture.descriptor.revision}"`,
            },
          },
        ),
      ),
    );
    await expect(load()).rejects.toThrow('descriptor');
    expect(fixture.requests).toHaveLength(0);
  });
  it('requires the descriptor representation validator, not the catalog identity, in ETag', async () => {
    const fixture = installReferenceCatalog();
    server.use(
      http.get('/api/catalog-snapshot', () =>
        HttpResponse.json(fixture.descriptor, {
          headers: {
            ETag: `"${fixture.catalog.revision}"`,
          },
        }),
      ),
    );
    await expect(load()).rejects.toThrow('descriptor');
    expect(fixture.requests).toHaveLength(0);
  });
  it('aborts an in-flight page and never starts another family', async () => {
    const fixture = installReferenceCatalog({
      systems: [
        system('one'),
      ],
    });
    const controller = new AbortController();
    let requested = false;
    let release!: () => void;
    const blocked = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.get('/api/catalog-snapshot/systems', async () => {
        requested = true;
        await blocked;
        return HttpResponse.json(page(fixture.catalog.systems));
      }),
    );
    const pending = loadAdminReferenceCatalog(controller.signal);
    const aborted = expect(pending).rejects.toMatchObject({
      name: 'AbortError',
    });
    try {
      await waitFor(() => expect(requested).toBe(true));
      controller.abort();
      await aborted;
      expect(fixture.requests).toHaveLength(1);
    } finally {
      controller.abort();
      release();
      await blocked;
    }
  });
});
