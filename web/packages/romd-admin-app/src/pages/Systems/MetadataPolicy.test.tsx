import { MantineProvider } from '@mantine/core';
import {
  getJob,
  getSystemEnrichmentDefaults,
  updateSystemEnrichmentDefaults,
} from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { beforeEach, expect, it, vi } from 'vitest';
import { MetadataPolicy } from './MetadataPolicy';

vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    canEditMetadataPolicy: true,
  }),
}));
vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  getSystemEnrichmentDefaults: vi.fn(),
  updateSystemEnrichmentDefaults: vi.fn(),
  getJob: vi.fn(),
}));

const policy = (overrides: Record<string, unknown> = {}) => ({
  revision: 'body-revision-is-not-the-validator',
  defaults: {
    Description: 'igdb',
  },
  globalSourcePriority: [
    'igdb',
    'igdb',
    'other',
  ],
  affectedTitles: 42,
  pendingJobId: null,
  ...overrides,
});
const reply = (data: ReturnType<typeof policy>, etag: string | null) => ({
  data,
  response: new Response(null, {
    headers: etag
      ? {
          ETag: etag,
        }
      : {},
  }),
});
function loaded(data = policy(), etag: string | null = '"saved-tag"') {
  vi.mocked(getSystemEnrichmentDefaults).mockResolvedValue(
    reply(data, etag) as Awaited<ReturnType<typeof getSystemEnrichmentDefaults>>,
  );
}
function saved(data = policy(), etag = '"next-tag"') {
  vi.mocked(updateSystemEnrichmentDefaults).mockResolvedValue(
    reply(data, etag) as Awaited<ReturnType<typeof updateSystemEnrichmentDefaults>>,
  );
}
function show() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <MemoryRouter>
          <MetadataPolicy systemKey="system" />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return cache;
}
async function chooseDescription(label: string) {
  fireEvent.click(
    await screen.findByRole('combobox', {
      name: 'Description',
    }),
  );
  await userEvent.click(
    await screen.findByRole('option', {
      name: label,
    }),
  );
}
async function save() {
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Save policy',
    }),
  );
}
beforeEach(() => vi.resetAllMocks());

it('deduplicates providers, explicitly clears to Automatic, and chains the returned body and actual ETag without another GET', async () => {
  loaded();
  saved(
    policy({
      defaults: {},
    }),
  );
  show();
  expect(
    await screen.findByRole('combobox', {
      name: 'Description',
    }),
  ).toHaveValue('IGDB');
  await chooseDescription('Automatic (igdb → other)');
  await save();
  await waitFor(() =>
    expect(updateSystemEnrichmentDefaults).toHaveBeenCalledExactlyOnceWith({
      path: {
        systemKey: 'system',
      },
      headers: {
        'If-Match': '"saved-tag"',
      },
      body: {
        defaults: {
          Description: '',
        },
      },
    }),
  );
  expect(await screen.findByText('Policy saved.')).toBeInTheDocument();
  expect(getSystemEnrichmentDefaults).toHaveBeenCalledTimes(1);
  expect(
    screen.getByRole('button', {
      name: 'Save policy',
    }),
  ).toBeDisabled();
  await chooseDescription('OTHER');
  saved(
    policy({
      defaults: {
        Description: 'other',
      },
    }),
    '"third-tag"',
  );
  await save();
  await waitFor(() =>
    expect(updateSystemEnrichmentDefaults).toHaveBeenLastCalledWith({
      path: {
        systemKey: 'system',
      },
      headers: {
        'If-Match': '"next-tag"',
      },
      body: {
        defaults: {
          Description: 'other',
        },
      },
    }),
  );
  expect(getSystemEnrichmentDefaults).toHaveBeenCalledTimes(1);
});

it('keeps the edited baseline and dirty draft through a background refresh and 412, then explicitly reloads body and ETag together', async () => {
  loaded();
  const cache = show();
  await chooseDescription('OTHER');
  loaded(
    policy({
      defaults: {
        Description: '',
      },
    }),
    '"concurrent-tag"',
  );
  await act(async () => {
    await cache.invalidateQueries({
      queryKey: [
        'metadata-policy',
        'system',
      ],
    });
  });
  expect(
    screen.getByRole('combobox', {
      name: 'Description',
    }),
  ).toHaveValue('OTHER');
  vi.mocked(updateSystemEnrichmentDefaults).mockResolvedValue({
    error: {
      status: 412,
      detail: 'The policy changed. Reload the saved policy before trying again.',
    },
    response: new Response(null, {
      status: 412,
    }),
  } as Awaited<ReturnType<typeof updateSystemEnrichmentDefaults>>);
  await save();
  expect(await screen.findByText(/The policy changed/)).toBeInTheDocument();
  expect(
    screen.getByRole('combobox', {
      name: 'Description',
    }),
  ).toHaveValue('OTHER');
  expect(updateSystemEnrichmentDefaults).toHaveBeenCalledWith(
    expect.objectContaining({
      headers: {
        'If-Match': '"saved-tag"',
      },
    }),
  );
  expect(
    screen.getByRole('button', {
      name: 'Save policy',
    }),
  ).toBeEnabled();
  vi.spyOn(window, 'confirm').mockReturnValueOnce(true);
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Reload saved policy',
    }),
  );
  await waitFor(() =>
    expect(
      screen.getByRole('combobox', {
        name: 'Description',
      }),
    ).toHaveValue('Automatic (igdb → other)'),
  );
  expect(
    screen.getByRole('button', {
      name: 'Save policy',
    }),
  ).toBeDisabled();
  await chooseDescription('IGDB');
  saved(policy());
  await save();
  await waitFor(() =>
    expect(updateSystemEnrichmentDefaults).toHaveBeenLastCalledWith(
      expect.objectContaining({
        headers: {
          'If-Match': '"concurrent-tag"',
        },
      }),
    ),
  );
});

it('polls a saved pending job to completion without a socket connection', async () => {
  loaded();
  saved(
    policy({
      defaults: {},
      pendingJobId: 'metadata-job',
    }),
  );
  vi.mocked(getJob)
    .mockResolvedValueOnce({
      data: {
        id: 'metadata-job',
        jobType: 'metadata-rematerialization',
        phase: 'Rematerializing',
        isTerminal: false,
        totalTitles: 2,
        processedCount: 1,
        deferredCount: 0,
      },
    } as Awaited<ReturnType<typeof getJob>>)
    .mockResolvedValue({
      data: {
        id: 'metadata-job',
        jobType: 'metadata-rematerialization',
        phase: 'Completed',
        isTerminal: true,
        totalTitles: 2,
        processedCount: 2,
        deferredCount: 0,
      },
    } as Awaited<ReturnType<typeof getJob>>);
  show();
  await chooseDescription('Automatic (igdb → other)');
  await save();
  expect(await screen.findByText('Updating titles · 1 of 2 processed')).toBeInTheDocument();
  expect(
    await screen.findByText(
      'Titles updated.',
      {},
      {
        timeout: 3500,
      },
    ),
  ).toBeInTheDocument();
  expect(getJob).toHaveBeenCalledTimes(2);
  expect(getSystemEnrichmentDefaults).toHaveBeenCalledTimes(1);
});

it('observes a pending job from GET and explains automatic retries without offering Cancel', async () => {
  loaded(
    policy({
      pendingJobId: 'metadata-job',
    }),
  );
  vi.mocked(getJob).mockResolvedValue({
    data: {
      id: 'metadata-job',
      jobType: 'metadata-rematerialization',
      phase: 'CompletedWithErrors',
      isTerminal: true,
      totalTitles: 42,
      processedCount: 42,
      deferredCount: 2,
    },
  } as Awaited<ReturnType<typeof getJob>>);
  show();
  expect(
    await screen.findByText('Titles updated. 2 titles will retry automatically.'),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: /cancel/i,
    }),
  ).not.toBeInTheDocument();
});

it('ignores an older in-flight GET that resolves after PATCH and chains the PATCH validator', async () => {
  loaded();
  const cache = show();
  await chooseDescription('OTHER');
  let finishRead: (value: Awaited<ReturnType<typeof getSystemEnrichmentDefaults>>) => void =
    () => {};
  vi.mocked(getSystemEnrichmentDefaults).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finishRead = resolve;
      }),
  );
  const refresh = cache.invalidateQueries({
    queryKey: [
      'metadata-policy',
      'system',
    ],
  });
  await waitFor(() => expect(getSystemEnrichmentDefaults).toHaveBeenCalledTimes(2));
  saved(
    policy({
      defaults: {
        Description: 'other',
      },
    }),
    '"patch-tag"',
  );
  await save();
  expect(await screen.findByText('Policy saved.')).toBeInTheDocument();
  await act(async () => {
    finishRead(
      reply(policy(), '"late-old-tag"') as Awaited<ReturnType<typeof getSystemEnrichmentDefaults>>,
    );
    await refresh;
  });
  expect(
    cache.getQueryData([
      'metadata-policy',
      'system',
    ]),
  ).toEqual({
    policy: policy({
      defaults: {
        Description: 'other',
      },
    }),
    etag: '"patch-tag"',
  });
  expect(
    screen.getByRole('combobox', {
      name: 'Description',
    }),
  ).toHaveValue('OTHER');
  await chooseDescription('IGDB');
  saved(policy(), '"last-tag"');
  await save();
  await waitFor(() =>
    expect(updateSystemEnrichmentDefaults).toHaveBeenLastCalledWith(
      expect.objectContaining({
        headers: {
          'If-Match': '"patch-tag"',
        },
      }),
    ),
  );
});

it('ignores a background GET started during PATCH when that older read resolves last', async () => {
  loaded();
  const cache = show();
  await chooseDescription('OTHER');
  let finishPatch: (value: Awaited<ReturnType<typeof updateSystemEnrichmentDefaults>>) => void =
    () => {};
  vi.mocked(updateSystemEnrichmentDefaults).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finishPatch = resolve;
      }),
  );
  await save();
  await waitFor(() => expect(updateSystemEnrichmentDefaults).toHaveBeenCalledTimes(1));
  let finishRead: (value: Awaited<ReturnType<typeof getSystemEnrichmentDefaults>>) => void =
    () => {};
  vi.mocked(getSystemEnrichmentDefaults).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        finishRead = resolve;
      }),
  );
  const refresh = cache.invalidateQueries({
    queryKey: [
      'metadata-policy',
      'system',
    ],
  });
  await waitFor(() => expect(getSystemEnrichmentDefaults).toHaveBeenCalledTimes(2));
  await act(async () => {
    finishPatch(
      reply(
        policy({
          defaults: {
            Description: 'other',
          },
        }),
        '"patch-tag"',
      ) as Awaited<ReturnType<typeof updateSystemEnrichmentDefaults>>,
    );
  });
  expect(await screen.findByText('Policy saved.')).toBeInTheDocument();
  await act(async () => {
    finishRead(
      reply(policy(), '"late-old-tag"') as Awaited<ReturnType<typeof getSystemEnrichmentDefaults>>,
    );
    await refresh;
  });
  expect(
    cache.getQueryData([
      'metadata-policy',
      'system',
    ]),
  ).toEqual({
    policy: policy({
      defaults: {
        Description: 'other',
      },
    }),
    etag: '"patch-tag"',
  });
  await chooseDescription('IGDB');
  saved(policy(), '"last-tag"');
  await save();
  await waitFor(() =>
    expect(updateSystemEnrichmentDefaults).toHaveBeenLastCalledWith(
      expect.objectContaining({
        headers: {
          'If-Match': '"patch-tag"',
        },
      }),
    ),
  );
});
