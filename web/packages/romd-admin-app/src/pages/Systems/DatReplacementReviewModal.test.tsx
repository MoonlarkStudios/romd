import { MantineProvider } from '@mantine/core';
import type { Dat, DatReplacementPreview } from '@romd/admin-api-client';
import {
  applyCatalogSubscription,
  applyDatSubscription,
  applyReviewedDatReplacement,
  previewCatalogSubscription,
  previewDatReplacement,
  previewDatSubscription,
} from '@romd/admin-api-client';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';

vi.mock('@romd/admin-api-client', () => ({
  applyCatalogSubscription: vi.fn(),
  previewCatalogSubscription: vi.fn(),
  previewDatReplacement: vi.fn(),
  previewDatSubscription: vi.fn(),
  applyDatSubscription: vi.fn(),
  applyReviewedDatReplacement: vi.fn(),
}));
vi.mock('./DatChangeBrowser', () => ({
  DatChangeBrowser: () => <div>Explore all changes</div>,
}));
const dat: Dat = {
  id: 'dat-1',
  name: 'Nintendo - Super Nintendo Entertainment System',
  sourceId: 'source',
  catalogSourceId: 'catalog',
  lifecycle: 'Active',
  sourceStatus: 'Active',
  type: 'Redump',
};
const preview: DatReplacementPreview = {
  activeSha256: 'before',
  candidateSha256: 'after',
  activeVersion: '1',
  candidateVersion: '2',
  unchanged: false,
  activeEntries: 10,
  candidateEntries: 11,
  entriesAdded: 1,
  entriesRemoved: 0,
  entriesChanged: 0,
  filesAdded: 2,
  filesRemoved: 0,
  filesChanged: 0,
  hashesChanged: 0,
  activeBiosEntries: 0,
  candidateBiosEntries: 0,
  changes: [
    {
      name: 'New disc',
      change: 'Added',
      filesAdded: 2,
      filesRemoved: 0,
      filesChanged: 0,
    },
  ],
  truncated: true,
};
function respond(data: DatReplacementPreview) {
  vi.mocked(previewDatReplacement).mockResolvedValue({
    data,
  } as Awaited<ReturnType<typeof previewDatReplacement>>);
}
function show() {
  const onClose = vi.fn();
  const onAccepted = vi.fn();
  render(
    <MantineProvider>
      <DatReplacementReviewModal
        dat={dat}
        file={
          new File(
            [
              'dat',
            ],
            'update.dat',
          )
        }
        onClose={onClose}
        onAccepted={onAccepted}
      />
    </MantineProvider>,
  );
  return {
    onClose,
    onAccepted,
  };
}
describe('DAT review', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    respond(preview);
  });
  it('offers the full change browser and cancellation never applies', async () => {
    const user = userEvent.setup();
    const { onClose } = show();
    await screen.findByText('Explore all changes');
    expect(screen.queryByText(/first 50 changed entries/)).not.toBeInTheDocument();
    expect(screen.getByText(/Library impact is not calculated/)).toBeInTheDocument();
    await user.click(
      screen.getByRole('button', {
        name: 'Cancel',
      }),
    );
    expect(onClose).toHaveBeenCalled();
    expect(applyReviewedDatReplacement).not.toHaveBeenCalled();
  });
  it('does not round a small nonzero change down to zero percent', async () => {
    respond({
      ...preview,
      activeEntries: 10000,
      activeFiles: 60000,
    });
    show();
    expect((await screen.findAllByText('<0.1% of current')).length).toBeGreaterThan(0);
  });
  it('does not offer replacement for identical content', async () => {
    respond({
      ...preview,
      unchanged: true,
    });
    show();
    await screen.findByText('Already up to date');
    expect(
      screen.queryByRole('button', {
        name: 'Apply reviewed update',
      }),
    ).not.toBeInTheDocument();
  });
  it('shows validation errors with an explicit retry', async () => {
    vi.mocked(previewDatReplacement).mockResolvedValue({
      error: {
        detail: 'Invalid checksum.',
      },
    } as Awaited<ReturnType<typeof previewDatReplacement>>);
    const user = userEvent.setup();
    show();
    await screen.findByText('Invalid checksum.');
    expect(
      screen.queryByRole('button', {
        name: 'Apply reviewed update',
      }),
    ).not.toBeInTheDocument();
    respond(preview);
    await user.click(
      screen.getByRole('button', {
        name: 'Preview again',
      }),
    );
    await screen.findByRole('button', {
      name: 'Apply reviewed update',
    });
    expect(applyReviewedDatReplacement).not.toHaveBeenCalled();
  });
  it('requires a new preview after a stale approval response', async () => {
    const user = userEvent.setup();
    show();
    vi.mocked(applyReviewedDatReplacement).mockResolvedValue({
      error: {
        detail: 'The active catalog changed.',
      },
    } as Awaited<ReturnType<typeof applyReviewedDatReplacement>>);
    await user.click(
      await screen.findByRole('button', {
        name: 'Apply reviewed update',
      }),
    );
    await screen.findByText('The active catalog changed.');
    await waitFor(() =>
      expect(
        screen.queryByRole('button', {
          name: 'Apply reviewed update',
        }),
      ).not.toBeInTheDocument(),
    );
    expect(
      screen.getByRole('button', {
        name: 'Preview again',
      }),
    ).toBeEnabled();
  });
});

it('reviews a retained subscription candidate and approves its exact hashes without uploading a file', async () => {
  vi.mocked(previewDatSubscription).mockResolvedValue({
    data: preview,
  } as Awaited<ReturnType<typeof previewDatSubscription>>);
  const accepted = {
    jobId: 'job',
    backgroundJobId: 'job',
    statusUrl: '/jobs/job',
  };
  vi.mocked(applyDatSubscription).mockResolvedValue({
    data: accepted,
  } as Awaited<ReturnType<typeof applyDatSubscription>>);
  const user = userEvent.setup();
  const onAccepted = vi.fn();
  render(
    <MantineProvider>
      <DatReplacementReviewModal
        dat={dat}
        onClose={vi.fn()}
        onAccepted={onAccepted}
      />
    </MantineProvider>,
  );
  await screen.findByText('Explore all changes');
  expect(screen.getByText(/saved in ROMD/)).toBeInTheDocument();
  await user.click(
    screen.getByRole('button', {
      name: 'Apply reviewed update',
    }),
  );
  expect(applyDatSubscription).toHaveBeenCalledWith({
    path: {
      datId: dat.id,
    },
    body: {
      activeSha256: 'before',
      candidateSha256: 'after',
    },
  });
  await waitFor(() => expect(onAccepted).toHaveBeenCalledWith(accepted));
});

it('reviews an initial catalog against an explicit empty baseline and submits exact fingerprints', async () => {
  const user = userEvent.setup();
  vi.mocked(previewCatalogSubscription).mockResolvedValue({
    data: {
      ...preview,
      activeSha256: '',
      activeVersion: null,
      activeEntries: 0,
    },
  } as Awaited<ReturnType<typeof previewCatalogSubscription>>);
  vi.mocked(applyCatalogSubscription).mockResolvedValue({
    data: {
      jobId: 'job1',
      backgroundJobId: 'job1',
    },
  } as Awaited<ReturnType<typeof applyCatalogSubscription>>);
  const accepted = vi.fn();
  render(
    <MantineProvider>
      <DatReplacementReviewModal
        subscription={{
          id: 'sub1',
          catalogId: 'no-intro/snes/standard',
          systemId: 'snes',
          name: 'Nintendo - Super Nintendo Entertainment System',
          systemKey: 'p1',
          activeDatId: null,
          state: 'ReadyToImport',
          lastCheckedAt: null,
          message: null,
          candidateSha256: 'after',
          jobId: null,
        }}
        onClose={vi.fn()}
        onAccepted={accepted}
      />
    </MantineProvider>,
  );
  expect(await screen.findByText('No installed document yet')).toBeInTheDocument();
  expect(screen.getByText('Verified subscription catalog')).toBeInTheDocument();
  expect(screen.queryByText(/PlayStation discs/)).not.toBeInTheDocument();
  expect(applyCatalogSubscription).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('button', {
      name: 'Import reviewed catalog',
    }),
  );
  await waitFor(() => expect(accepted).toHaveBeenCalled());
  expect(applyCatalogSubscription).toHaveBeenCalledWith({
    path: {
      subscriptionId: 'sub1',
    },
    body: {
      activeSha256: '',
      candidateSha256: 'after',
    },
  });
});
