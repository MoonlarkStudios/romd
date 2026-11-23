import { MantineProvider } from '@mantine/core';
import type {
  DatEntryChange,
  DatEntryChangeDetail,
  DatReplacementPreview,
  PageOfDatEntryChange,
} from '@romd/admin-api-client';
import {
  getDatReplacementChange,
  getDatSubscriptionChange,
  getDatSubscriptionChangeCount,
  listDatReplacementChanges,
  listDatSubscriptionChanges,
  previewDatReplacementChangeCount,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { DatChangeBrowser } from './DatChangeBrowser';

vi.mock('@romd/admin-api-client', () => ({
  getDatReplacementChange: vi.fn(),
  getDatSubscriptionChange: vi.fn(),
  getDatSubscriptionChangeCount: vi.fn(),
  listDatReplacementChanges: vi.fn(),
  listDatSubscriptionChanges: vi.fn(),
  previewDatReplacementChangeCount: vi.fn(),
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
const page: PageOfDatEntryChange = {
  items: [
    discA,
  ],
  nextCursor: 'next-page',
  hasNextPage: true,
};
function response(data: PageOfDatEntryChange) {
  return {
    data,
  } as Awaited<ReturnType<typeof listDatSubscriptionChanges>>;
}
function show({
  onStale = vi.fn(),
  file,
  reviewed = preview,
}: {
  onStale?: () => void;
  file?: File;
  reviewed?: DatReplacementPreview;
} = {}) {
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
        <MemoryRouter>
          <DatChangeBrowser
            datId="dat1"
            subscriptionId={file ? undefined : 'sub1'}
            file={file}
            preview={reviewed}
            header={<p>Review header</p>}
            linkTitles={!file}
            onStale={onStale}
          />
        </MemoryRouter>
      </MantineProvider>
    </QueryClientProvider>,
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  const count = {
    data: {
      total: 2,
    },
  } as Awaited<ReturnType<typeof getDatSubscriptionChangeCount>>;
  vi.mocked(getDatSubscriptionChangeCount).mockResolvedValue(count);
  vi.mocked(previewDatReplacementChangeCount).mockResolvedValue(count);
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
  expect(await screen.findByText('1–1 of 2')).toBeInTheDocument();
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
  expect(await screen.findByText('1–1 of 2')).toBeInTheDocument();
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
  await user.type(screen.getByLabelText('Search changed entries'), 'Disc{Enter}');
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
      name: 'Removed 1',
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

it('opens one entry by its id with exact relationship and file values', async () => {
  const user = userEvent.setup();
  const detail: DatEntryChangeDetail = {
    ...pins,
    id: discA.id,
    name: discA.name,
    change: 'Changed',
    fields: [
      {
        field: 'CloneOfId',
        before: '0001',
        after: ' 0001 ',
      },
      {
        field: 'ParentStatus',
        before: 'Resolved',
        after: 'Missing',
      },
    ],
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
  await user.click(screen.getByText('Entry metadata'));
  expect(screen.getByText('Clone of (ID)')).toBeVisible();
  expect(screen.getByText('0001')).toBeVisible();
  expect(screen.getByText('" 0001 "')).toBeVisible();
  expect(screen.getByText('Parent resolution')).toBeVisible();
  expect(screen.getByText('Missing')).toBeVisible();
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
it('resends a manual replacement file with the page and count requests', async () => {
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
  expect(await screen.findByText('1–1 of 2')).toBeInTheDocument();
  expect(previewDatReplacementChangeCount).toHaveBeenCalledWith(
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
  expect(getDatSubscriptionChangeCount).not.toHaveBeenCalled();
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
    expect(screen.getByText('Review header')).toBeVisible();
    await user.click(
      screen.getByRole('button', {
        name: 'Removed 1',
      }),
    );
    expect(screen.queryByText('Review header')).not.toBeInTheDocument();
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
      screen.getByRole('heading', {
        name: 'Removed',
      }),
    ).toBeVisible();
    await user.click(
      screen.getByRole('button', {
        name: 'Back to summary',
      }),
    );
    expect(screen.getByText('Review header')).toBeVisible();
  } finally {
    mockMedia.mockRestore();
  }
});

const impactPreview = {
  ...preview,
  impact: {
    needsReviewEntries: 1,
    removedWithStoredRoms: 0,
    removedWithTitleLinks: 1,
    lostRomMatches: 0,
    newRomMatches: 0,
    sha1ChangedEntries: 1,
    sha1ChangedFiles: 1,
    metadataOnlyEntries: 0,
    otherChecksumOnlyEntries: 0,
    diskChangedEntries: 0,
  },
} as DatReplacementPreview;

it('reserves the warning tone for catalog impact and starts on the entries that need review', async () => {
  show({
    reviewed: impactPreview,
  });
  expect(
    screen.getByRole('region', {
      name: 'Review verdict',
    }),
  ).toHaveTextContent('1 change needs reviewRemoves 1 title link. No stored ROM loses its match.');
  const filters = screen.getByRole('navigation', {
    name: 'Review filters',
  });
  expect(
    within(filters).getByRole('button', {
      name: 'Needs review 1',
    }),
  ).toHaveAttribute('aria-pressed', 'true');
  expect(
    within(filters).getByRole('button', {
      name: 'Removes a title link 1',
    }),
  ).toHaveAttribute('data-tone', 'review');
  // A SHA-1 change that touches no stored ROM is a change, not a warning.
  expect(
    within(filters).getByRole('button', {
      name: 'SHA-1 changed 1',
    }),
  ).not.toHaveAttribute('data-tone');
  // Zero rows stay visible so the admin can see they were checked.
  expect(
    within(filters).getByRole('button', {
      name: 'ROMs losing a match —',
    }),
  ).toBeDisabled();
  await waitFor(() =>
    expect(listDatSubscriptionChanges).toHaveBeenLastCalledWith(
      expect.objectContaining({
        query: expect.objectContaining({
          change: 'NeedsReview',
        }),
      }),
    ),
  );
});

it('names the title a removal unlinks, links to it, and collapses a removed file to one line', async () => {
  const user = userEvent.setup();
  const removed: DatEntryChange = {
    id: 'QWllbiBDYXQgMg',
    name: 'Aien Cat 2 (World) (Aftermarket) (Unl)',
    change: 'Removed',
    filesAdded: 0,
    filesRemoved: 1,
    filesChanged: 0,
    impact: {
      storedRoms: 0,
      lostRomMatches: 0,
      newRomMatches: 0,
      titleLink: 'Aien Cat 2',
      titleLinkAtRisk: true,
      sha1ChangedFiles: 0,
      metadataOnly: false,
      otherChecksumOnly: false,
      needsReview: true,
      titleId: 'title7',
      titleOtherSourceLinks: 0,
    },
  };
  vi.mocked(listDatSubscriptionChanges).mockResolvedValue(
    response({
      items: [
        removed,
      ],
      nextCursor: null,
      hasNextPage: false,
    }),
  );
  vi.mocked(getDatSubscriptionChange).mockResolvedValue({
    data: {
      ...pins,
      id: removed.id,
      name: removed.name,
      change: 'Removed',
      fields: [],
      impact: removed.impact,
      files: {
        items: [
          {
            name: 'Aien Cat 2 (World) (Aftermarket) (Unl).nes',
            kind: 'rom',
            change: 'Removed',
            checksumsChanged: false,
            fields: [
              {
                field: 'Sha1',
                before: 'fc43da81aaaaaaaaaaaaaaaaaaaaaaaaaaaa5305',
                after: null,
              },
              {
                field: 'Crc',
                before: '14939d21',
                after: null,
              },
            ],
            impact: {
              storedBefore: false,
              storedAfter: false,
              losesCatalogMatch: false,
              gainsCatalogMatch: false,
            },
          },
        ],
        nextCursor: null,
        hasNextPage: false,
      },
    },
  } as Awaited<ReturnType<typeof getDatSubscriptionChange>>);
  show({
    reviewed: impactPreview,
  });
  const row = await screen.findByRole('button', {
    name: /^Aien Cat 2/,
  });
  expect(within(row).getByText('Unlinks title')).toBeInTheDocument();
  expect(within(row).getByText('1 file')).toBeInTheDocument();
  await user.click(row);
  expect(await screen.findByText('Unlinks from title')).toBeInTheDocument();
  expect(
    screen.getByRole('link', {
      name: /Aien Cat 2/,
    }),
  ).toHaveAttribute('href', '/titles/title7');
  // Admin sessions are per tab; a new tab would land on sign-in.
  expect(
    screen.getByRole('link', {
      name: /Aien Cat 2/,
    }),
  ).not.toHaveAttribute('target');
  expect(screen.getByText(/No existing source links are projected to remain/)).toBeInTheDocument();
  expect(await screen.findByText('ROM · removed')).toBeInTheDocument();
  expect(screen.getByText('fc43da81…5305')).toBeInTheDocument();
  expect(
    screen.queryByRole('list', {
      name: 'Checksums',
    }),
  ).not.toBeInTheDocument();
  expect(screen.getByText('No stored ROM matches this file.')).toBeInTheDocument();
  // One matching entry has nowhere to step to.
  expect(
    screen.queryByRole('button', {
      name: 'Next entry',
    }),
  ).not.toBeInTheDocument();
  expect(
    screen.getByRole('heading', {
      name: removed.name,
    }),
  ).toHaveFocus();
});

it('names the title without leaving a manual upload review whose bytes live only on this page', async () => {
  const user = userEvent.setup();
  const impact = {
    storedRoms: 0,
    lostRomMatches: 0,
    newRomMatches: 0,
    titleLink: 'Aien Cat 2',
    titleLinkAtRisk: true,
    sha1ChangedFiles: 0,
    metadataOnly: false,
    otherChecksumOnly: false,
    needsReview: true,
    titleId: 'title7',
    titleOtherSourceLinks: 2,
  };
  const removed: DatEntryChange = {
    ...discA,
    change: 'Removed',
    impact,
  };
  vi.mocked(listDatReplacementChanges).mockResolvedValue(
    response({
      items: [
        removed,
      ],
      nextCursor: null,
      hasNextPage: false,
    }),
  );
  vi.mocked(getDatReplacementChange).mockResolvedValue({
    data: {
      ...pins,
      id: removed.id,
      name: removed.name,
      change: 'Removed',
      fields: [],
      impact,
      files: {
        items: [],
        nextCursor: null,
        hasNextPage: false,
      },
    },
  } as Awaited<ReturnType<typeof getDatReplacementChange>>);
  show({
    file: new File(
      [
        'dat',
      ],
      'update.dat',
    ),
    reviewed: impactPreview,
  });
  await user.click(
    await screen.findByRole('button', {
      name: /^Disc A/,
    }),
  );
  expect(await screen.findByText('Aien Cat 2')).toBeInTheDocument();
  expect(screen.getByText('The title keeps 2 other source links.')).toBeInTheDocument();
  expect(screen.queryByRole('link')).not.toBeInTheDocument();
});
