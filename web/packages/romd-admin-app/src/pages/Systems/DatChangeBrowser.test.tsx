import { MantineProvider } from '@mantine/core';
import type { DatChangePage, DatReplacementPreview } from '@romd/admin-api-client';
import { getCatalogSubscriptionChanges } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { DatChangeBrowser } from './DatChangeBrowser';

vi.mock('@romd/admin-api-client', () => ({
  getCatalogSubscriptionChanges: vi.fn(),
  getDatReplacementChanges: vi.fn(),
  getDatSubscriptionChanges: vi.fn(),
}));
const preview = {
  activeSha256: 'a'.repeat(64),
  candidateSha256: 'b'.repeat(64),
} as DatReplacementPreview;
const page: DatChangePage = {
  ...preview,
  offset: 0,
  pageSize: 50,
  total: 51,
  entryName: null,
  entryFields: [],
  files: [],
  entries: [
    {
      name: 'Disc A',
      change: 'Changed',
      filesAdded: 0,
      filesRemoved: 0,
      filesChanged: 1,
    },
  ],
};
function response(data: DatChangePage) {
  return {
    data,
  } as Awaited<ReturnType<typeof getCatalogSubscriptionChanges>>;
}
function show(onStale = vi.fn()) {
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
          subscriptionId="sub1"
          preview={preview}
          onStale={onStale}
        />
      </MantineProvider>
    </QueryClientProvider>,
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(getCatalogSubscriptionChanges).mockResolvedValue(response(page));
});
it('pages and searches using both reviewed fingerprints', async () => {
  const user = userEvent.setup();
  show();
  await screen.findByRole('button', {
    name: 'Disc A',
  });
  expect(
    screen.getByRole('button', {
      name: 'Previous changes',
    }),
  ).toBeDisabled();
  vi.mocked(getCatalogSubscriptionChanges).mockResolvedValue(
    response({
      ...page,
      offset: 50,
      entries: [
        {
          ...page.entries[0],
          name: 'Disc Z',
        },
      ],
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Next changes',
    }),
  );
  await screen.findByRole('button', {
    name: 'Disc Z',
  });
  expect(getCatalogSubscriptionChanges).toHaveBeenLastCalledWith(
    expect.objectContaining({
      body: expect.objectContaining({
        ...preview,
        offset: 50,
      }),
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Next changes',
    }),
  ).toBeDisabled();
  await user.type(screen.getByLabelText('Search changed entries'), 'Disc');
  await user.click(
    screen.getByRole('button', {
      name: 'Search',
    }),
  );
  await waitFor(() =>
    expect(getCatalogSubscriptionChanges).toHaveBeenLastCalledWith(
      expect.objectContaining({
        body: expect.objectContaining({
          ...preview,
          offset: 0,
          search: 'Disc',
        }),
      }),
    ),
  );
});
it('resets pagination when choosing a change filter', async () => {
  const user = userEvent.setup();
  show();
  await screen.findByRole('button', {
    name: 'Disc A',
  });
  await user.selectOptions(
    screen.getByRole('combobox', {
      name: 'Change type',
    }),
    'Removed',
  );
  await waitFor(() =>
    expect(getCatalogSubscriptionChanges).toHaveBeenLastCalledWith(
      expect.objectContaining({
        body: expect.objectContaining({
          ...preview,
          offset: 0,
          change: 'Removed',
        }),
      }),
    ),
  );
});
it('opens bounded file details with exact before and after values', async () => {
  const user = userEvent.setup();
  show();
  await screen.findByRole('button', {
    name: 'Disc A',
  });
  vi.mocked(getCatalogSubscriptionChanges).mockResolvedValue(
    response({
      ...page,
      total: 1,
      entryName: 'Disc A',
      entries: [],
      files: [
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
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Disc A',
    }),
  );
  await screen.findByText(/track.bin · rom · Changed · checksum changed/);
  await user.click(screen.getByText(/track.bin · rom · Changed · checksum changed/));
  expect(screen.getByText('9007199254740994')).toBeInTheDocument();
  expect(getCatalogSubscriptionChanges).toHaveBeenLastCalledWith(
    expect.objectContaining({
      body: expect.objectContaining({
        ...preview,
        entryName: 'Disc A',
        offset: 0,
      }),
    }),
  );
});
it('invalidates approval context when the server rejects stale versions', async () => {
  const onStale = vi.fn();
  vi.mocked(getCatalogSubscriptionChanges).mockResolvedValue({
    error: {
      detail: 'Candidate changed. Preview again.',
    },
    response: {
      status: 409,
    },
  } as Awaited<ReturnType<typeof getCatalogSubscriptionChanges>>);
  show(onStale);
  await waitFor(() => expect(onStale).toHaveBeenCalledWith('Candidate changed. Preview again.'));
  expect(
    screen.queryByRole('button', {
      name: 'Disc A',
    }),
  ).not.toBeInTheDocument();
});
it('offers retry for network failure and explains empty filtered results', async () => {
  const user = userEvent.setup();
  vi.mocked(getCatalogSubscriptionChanges).mockRejectedValueOnce(new Error('Network unavailable'));
  show();
  await screen.findByText('Network unavailable');
  vi.mocked(getCatalogSubscriptionChanges).mockResolvedValue(
    response({
      ...page,
      total: 0,
      entries: [],
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Retry page',
    }),
  );
  expect(await screen.findByText('No changes match these filters.')).toBeInTheDocument();
});
