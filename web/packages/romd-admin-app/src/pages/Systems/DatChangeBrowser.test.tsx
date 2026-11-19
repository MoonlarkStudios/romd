import { MantineProvider } from '@mantine/core';
import type {
  DatChangesPage,
  DatEntryChange,
  DatEntryChangeDetail,
  DatReplacementPreview,
} from '@romd/admin-api-client';
import {
  getDatSubscriptionChange,
  listDatReplacementChanges,
  listDatSubscriptionChanges,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { DatChangeBrowser } from './DatChangeBrowser';

vi.mock('@romd/admin-api-client', () => ({
  getDatReplacementChange: vi.fn(),
  getDatSubscriptionChange: vi.fn(),
  listDatReplacementChanges: vi.fn(),
  listDatSubscriptionChanges: vi.fn(),
}));
const pins = {
  activeSha256: 'a'.repeat(64),
  candidateSha256: 'b'.repeat(64),
};
const preview = {
  ...pins,
  entriesAdded: 0,
  entriesRemoved: 1,
  entriesChanged: 1,
} as DatReplacementPreview;
const discA: DatEntryChange = {
  id: 'RGlzYyBB',
  name: 'Disc A',
  change: 'Changed',
  filesAdded: 0,
  filesRemoved: 0,
  filesChanged: 1,
};
const page: DatChangesPage = {
  items: [
    discA,
  ],
  nextCursor: 'next-page',
  hasNextPage: true,
  total: 2,
};
function response(data: DatChangesPage) {
  return {
    data,
  } as Awaited<ReturnType<typeof listDatSubscriptionChanges>>;
}
function show({ onStale = vi.fn(), file }: { onStale?: () => void; file?: File } = {}) {
  render(
    <QueryClientProvider
      client={
        new QueryClient({
          defaultOptions: {
            queries: {
              retry: false,
            },
          },
        })
      }
    >
      <MantineProvider>
        <DatChangeBrowser
          datId="dat1"
          subscriptionId={file ? undefined : 'sub1'}
          file={file}
          preview={preview}
          renderSummary={(viewChanges) => (
            <button
              type="button"
              onClick={() => viewChanges('Removed')}
            >
              View removed entries
            </button>
          )}
          onStale={onStale}
        />
      </MantineProvider>
    </QueryClientProvider>,
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue(response(page));
});
it('pages forward by cursor and back through the visited pages, pinned to both fingerprints', async () => {
  const user = userEvent.setup();
  show();
  await screen.findByRole('button', {
    name: /^Disc A/,
  });
  expect(
    screen.getByRole('button', {
      name: 'Previous changes',
    }),
  ).toBeDisabled();
  expect(screen.getByText('1–1 of 2')).toBeInTheDocument();
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue(
    response({
      items: [
        {
          ...discA,
          id: 'RGlzYyBa',
          name: 'Disc Z',
        },
      ],
      nextCursor: null,
      hasNextPage: false,
      total: 2,
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Next changes',
    }),
  );
  await screen.findByRole('button', {
    name: /^Disc Z/,
  });
  expect(screen.getByText('2–2 of 2')).toBeInTheDocument();
  expect(listDatSubscriptionChanges).toHaveBeenLastCalledWith(
    expect.objectContaining({
      path: {
        subscriptionId: 'sub1',
      },
      query: expect.objectContaining({
        ...pins,
        cursor: 'next-page',
      }),
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Next changes',
    }),
  ).toBeDisabled();
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue(response(page));
  await user.click(
    screen.getByRole('button', {
      name: 'Previous changes',
    }),
  );
  await screen.findByRole('button', {
    name: /^Disc A/,
  });
  expect(screen.getByText('1–1 of 2')).toBeInTheDocument();
});
it('restarts from the first page when searching or choosing a change filter', async () => {
  const user = userEvent.setup();
  show();
  await screen.findByRole('button', {
    name: /^Disc A/,
  });
  await user.click(
    screen.getByRole('button', {
      name: 'Next changes',
    }),
  );
  await waitFor(() =>
    expect(listDatSubscriptionChanges).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          cursor: 'next-page',
        }),
      }),
    ),
  );
  await user.type(screen.getByLabelText('Search changed entries'), 'Disc');
  await user.click(
    screen.getByRole('button', {
      name: 'Search',
    }),
  );
  await waitFor(() =>
    expect(listDatSubscriptionChanges).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: {
          ...pins,
          cursor: undefined,
          query: 'Disc',
          change: undefined,
        },
      }),
    ),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Removed 1',
    }),
  );
  await waitFor(() =>
    expect(listDatSubscriptionChanges).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: {
          ...pins,
          cursor: undefined,
          query: 'Disc',
          change: 'Removed',
        },
      }),
    ),
  );
});
it('opens the removed-entry filter from the summary and moves focus to the results', async () => {
  const user = userEvent.setup();
  show();
  await screen.findByRole('button', {
    name: /^Disc A/,
  });
  await user.click(
    screen.getByRole('button', {
      name: 'View removed entries',
    }),
  );
  expect(
    screen.getByRole('heading', {
      name: 'Removed',
    }),
  ).toHaveFocus();
  await waitFor(() =>
    expect(listDatSubscriptionChanges).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          change: 'Removed',
        }),
      }),
    ),
  );
});

it('opens one entry by its id with exact before and after file values', async () => {
  const user = userEvent.setup();
  const detail: DatEntryChangeDetail = {
    ...pins,
    id: discA.id,
    name: discA.name,
    change: 'Changed',
    fields: [],
    files: {
      items: [
        {
          name: 'track.bin',
          kind: 'rom',
          change: 'Changed',
          checksumsChanged: true,
          fields: [
            {
              field: 'Size',
              before: '9007199254740993',
              after: '9007199254740994',
            },
          ],
        },
      ],
      nextCursor: null,
      hasNextPage: false,
    },
  };
  vi.mocked(getDatSubscriptionChange).mockResolvedValue({
    data: detail,
  } as Awaited<ReturnType<typeof getDatSubscriptionChange>>);
  show();
  await user.click(
    await screen.findByRole('button', {
      name: /^Disc A/,
    }),
  );
  expect(await screen.findByText('track.bin')).toBeVisible();
  await user.click(screen.getByText('Full values'));
  expect(screen.getByText('9007199254740994')).toBeVisible();
  expect(
    screen.getByRole('heading', {
      name: 'Disc A',
    }),
  ).toBeVisible();
  expect(getDatSubscriptionChange).toHaveBeenLastCalledWith(
    expect.objectContaining({
      path: {
        subscriptionId: 'sub1',
        entryId: discA.id,
      },
      query: {
        ...pins,
        cursor: undefined,
      },
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Next entry',
    }),
  ).toBeEnabled();
});
it('invalidates approval context only for a stale snapshot binding', async () => {
  const onStale = vi.fn();
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue({
    error: {
      errorCode: 'DatEnrollment.Stale',
      detail: 'Candidate changed. Preview again.',
      traceId: 'trace',
    },
  } as Awaited<ReturnType<typeof listDatSubscriptionChanges>>);
  show({
    onStale,
  });
  await waitFor(() => expect(onStale).toHaveBeenCalledWith('Candidate changed. Preview again.'));
  expect(
    screen.queryByRole('button', {
      name: /^Disc A/,
    }),
  ).not.toBeInTheDocument();
});
it('shows a state conflict that is not staleness with its own message', async () => {
  const onStale = vi.fn();
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue({
    error: {
      errorCode: 'DatEnrollment.PlatformMissing',
      detail: 'This publisher system key is unresolved.',
      traceId: 'trace',
    },
  } as Awaited<ReturnType<typeof listDatSubscriptionChanges>>);
  show({
    onStale,
  });
  expect(await screen.findByText('This publisher system key is unresolved.')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Retry page',
    }),
  ).toBeInTheDocument();
  expect(onStale).not.toHaveBeenCalled();
});
it('offers retry for network failure and explains empty filtered results', async () => {
  const user = userEvent.setup();
  vi.mocked(listDatSubscriptionChanges).mockRejectedValueOnce(new Error('Network unavailable'));
  show();
  await screen.findByText('Network unavailable');
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue(
    response({
      items: [],
      nextCursor: null,
      hasNextPage: false,
      total: 0,
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Retry page',
    }),
  );
  expect(await screen.findByText('No changes match these filters.')).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Next changes',
    }),
  ).not.toBeInTheDocument();
});
it('resends a manual replacement file with each page request', async () => {
  const file = new File(
    [
      'dat',
    ],
    'update.dat',
  );
  vi.mocked(listDatReplacementChanges).mockResolvedValue(response(page));
  show({
    file,
  });
  await screen.findByRole('button', {
    name: /^Disc A/,
  });
  expect(listDatReplacementChanges).toHaveBeenCalledWith(
    expect.objectContaining({
      path: {
        datId: 'dat1',
      },
      body: expect.objectContaining({
        ...pins,
        file,
      }),
    }),
  );
  expect(listDatSubscriptionChanges).not.toHaveBeenCalled();
});

it('returns from an entry to the filtered mobile list after moving across a page boundary', async () => {
  const user = userEvent.setup();
  const mockMedia = vi.spyOn(window, 'matchMedia').mockImplementation((query) => ({
    matches: query === '' || query === '(max-width: 64em)',
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));
  try {
    const discZ = {
      ...discA,
      id: 'RGlzYyBa',
      name: 'Disc Z',
    };
    vi.mocked(listDatSubscriptionChanges).mockImplementation(async (request) =>
      response(
        request.query.cursor
          ? {
              items: [
                discZ,
              ],
              nextCursor: null,
              hasNextPage: false,
              total: 2,
            }
          : page,
      ),
    );
    vi.mocked(getDatSubscriptionChange).mockImplementation(async (request) => {
      const entry = request.path.entryId === discA.id ? discA : discZ;
      return {
        data: {
          ...pins,
          id: entry.id,
          name: entry.name,
          change: entry.change,
          fields: [],
          files: {
            items: [],
            nextCursor: null,
            hasNextPage: false,
          },
        },
      } as Awaited<ReturnType<typeof getDatSubscriptionChange>>;
    });
    show();
    await user.click(
      screen.getByRole('button', {
        name: 'View removed entries',
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: /^Disc A/,
      }),
    );
    expect(
      screen.getByRole('heading', {
        name: 'Disc A',
      }),
    ).toHaveFocus();
    await user.click(
      screen.getByRole('button', {
        name: 'Next entry',
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Disc Z',
      }),
    ).toBeVisible();
    await user.click(
      screen.getByRole('button', {
        name: 'Back to removed',
      }),
    );
    expect(
      screen.getByRole('button', {
        name: /^Disc Z/,
      }),
    ).toHaveFocus();
    expect(screen.getByText('2–2 of 2')).toBeVisible();
    expect(
      screen.getByRole('button', {
        name: 'Removed 1',
      }),
    ).toHaveAttribute('aria-pressed', 'true');
  } finally {
    mockMedia.mockRestore();
  }
});
