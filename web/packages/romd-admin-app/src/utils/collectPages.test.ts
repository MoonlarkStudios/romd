import { describe, expect, it, vi } from 'vitest';
import { type CursorPage, collectPages } from './collectPages';

const terminal = (items: string[]): CursorPage<string> => ({
  items,
  nextCursor: null,
  hasNextPage: false,
});

describe('collectPages', () => {
  it('follows empty intermediate pages and retains the complete ordered list', async () => {
    const load = vi
      .fn<(cursor: string | undefined) => Promise<CursorPage<string>>>()
      .mockResolvedValueOnce({
        items: [
          'first',
        ],
        nextCursor: 'a',
        hasNextPage: true,
      })
      .mockResolvedValueOnce({
        items: [],
        nextCursor: 'b',
        hasNextPage: true,
      })
      .mockResolvedValueOnce(
        terminal([
          'last',
        ]),
      );

    await expect(collectPages(load)).resolves.toEqual([
      'first',
      'last',
    ]);
    expect(load.mock.calls).toEqual([
      [
        undefined,
      ],
      [
        'a',
      ],
      [
        'b',
      ],
    ]);
  });

  it('continues an embedded first page without fetching it twice', async () => {
    const load = vi.fn().mockResolvedValue(
      terminal([
        'last',
      ]),
    );
    await expect(
      collectPages(load, {
        items: [
          'first',
        ],
        nextCursor: 'a',
        hasNextPage: true,
      }),
    ).resolves.toEqual([
      'first',
      'last',
    ]);
    expect(load).toHaveBeenCalledExactlyOnceWith('a');
  });

  it('rejects late failures instead of returning a partial list', async () => {
    const failure = new Error('Later page failed');
    const load = vi
      .fn()
      .mockResolvedValueOnce({
        items: [
          'first',
        ],
        nextCursor: 'a',
        hasNextPage: true,
      })
      .mockRejectedValueOnce(failure);
    await expect(collectPages(load)).rejects.toBe(failure);
  });

  it('rejects a repeated cursor instead of looping or treating it as completion', async () => {
    const load = vi.fn().mockResolvedValue({
      items: [],
      nextCursor: 'a',
      hasNextPage: true,
    });
    await expect(collectPages(load)).rejects.toThrow('repeated a page cursor');
    expect(load).toHaveBeenCalledTimes(2);
  });

  it.each([
    null,
    {},
    {
      items: [],
      hasNextPage: false,
    },
    {
      items: {},
      nextCursor: null,
      hasNextPage: false,
    },
    {
      items: [],
      nextCursor: null,
      hasNextPage: true,
    },
    {
      items: [],
      nextCursor: 'a',
      hasNextPage: false,
    },
    {
      items: [],
      nextCursor: '',
      hasNextPage: true,
    },
    {
      items: [],
      nextCursor: 1,
      hasNextPage: true,
    },
  ])('rejects an incoherent page %j', async (page) => {
    const load = vi.fn().mockResolvedValue(page);
    await expect(collectPages<string>(load)).rejects.toThrow('invalid page');
    expect(load).toHaveBeenCalledOnce();
  });
});
