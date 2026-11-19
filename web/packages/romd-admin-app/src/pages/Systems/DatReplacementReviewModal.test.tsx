import { MantineProvider } from '@mantine/core';
import type { Dat, DatCatalogSubscription, DatReplacementPreview } from '@romd/admin-api-client';
import {
  applyDatSubscription,
  createDatReplacement,
  getDatSubscriptionPreview,
  previewDatReplacement,
} from '@romd/admin-api-client';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ComponentProps } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DatChangeBrowser } from './DatChangeBrowser';
import { DatReplacementReviewModal } from './DatReplacementReviewModal';

vi.mock('@romd/admin-api-client', () => ({
  previewDatReplacement: vi.fn(),
  getDatSubscriptionPreview: vi.fn(),
  applyDatSubscription: vi.fn(),
  createDatReplacement: vi.fn(),
}));
vi.mock('./DatChangeBrowser', () => ({
  DatChangeBrowser: ({ header, footnotes }: ComponentProps<typeof DatChangeBrowser>) => (
    <>
      {header}
      <div>Explore all changes</div>
      {footnotes}
    </>
  ),
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
const subscription: DatCatalogSubscription = {
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
      id: 'TmV3IGRpc2M',
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
beforeEach(() => {
  vi.clearAllMocks();
});
describe('DAT review', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    respond(preview);
  });
  it('offers the full change browser and cancellation never applies', async () => {
    const user = userEvent.setup();
    const { onClose } = show();
    await screen.findByText('Explore all changes');
    expect(screen.getByText('Manual upload')).toBeVisible();
    expect(screen.getByText(/update\.dat/)).toBeVisible();
    expect(
      screen.getByText('Applying runs in a background job. Stored ROM files are never deleted.'),
    ).toBeVisible();
    await user.click(
      screen.getByRole('button', {
        name: 'Discard upload',
      }),
    );
    expect(onClose).toHaveBeenCalled();
    expect(createDatReplacement).not.toHaveBeenCalled();
  });
  it('leads with dated versions and puts the consequence beside the decision', async () => {
    respond({
      ...preview,
      activeVersion: '20261012-015604',
      candidateVersion: '20261025-205653',
      activeEntries: 7712,
      candidateEntries: 7718,
      entriesRemoved: 2,
      filesRemoved: 3,
      entriesChanged: 1,
      hashesChanged: 1,
      impact: {
        needsReviewEntries: 2,
        removedWithStoredRoms: 1,
        removedWithTitleLinks: 1,
        lostRomMatches: 2,
        newRomMatches: 0,
        sha1ChangedEntries: 1,
        sha1ChangedFiles: 1,
        metadataOnlyEntries: 0,
        otherChecksumOnlyEntries: 0,
        diskChangedEntries: 0,
      },
    });
    show();
    const versions = await screen.findByRole('region', {
      name: 'DAT versions',
    });
    expect(versions).toHaveTextContent('Installed DAT');
    expect(versions).toHaveTextContent('20261012-015604');
    expect(versions).toHaveTextContent('Incoming DAT · 13 days newer');
    expect(versions).toHaveTextContent('20261025-205653');
    expect(versions).toHaveTextContent('7,718 entries (+6)');
    expect(versions).toHaveTextContent('4 changed entries');
    expect(
      screen.getByText(
        'Applying unlinks 1 title and removes the catalog match for 2 stored ROMs. Libraries refresh after the job.',
      ),
    ).toBeVisible();
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
        name: 'Apply uploaded DAT',
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
        name: 'Apply uploaded DAT',
      }),
    ).not.toBeInTheDocument();
    respond(preview);
    await user.click(
      screen.getByRole('button', {
        name: 'Preview again',
      }),
    );
    await screen.findByRole('button', {
      name: 'Apply uploaded DAT',
    });
    expect(createDatReplacement).not.toHaveBeenCalled();
  });
  it('requires a new preview after a stale approval response', async () => {
    const user = userEvent.setup();
    show();
    vi.mocked(createDatReplacement).mockResolvedValue({
      error: {
        detail: 'The active catalog changed.',
      },
    } as Awaited<ReturnType<typeof createDatReplacement>>);
    await user.click(
      await screen.findByRole('button', {
        name: 'Apply uploaded DAT',
      }),
    );
    await screen.findByText('The active catalog changed.');
    await waitFor(() =>
      expect(
        screen.queryByRole('button', {
          name: 'Apply uploaded DAT',
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

it('reviews a saved subscription update and approves its exact hashes without uploading a file', async () => {
  vi.mocked(getDatSubscriptionPreview).mockResolvedValue({
    data: preview,
  } as Awaited<ReturnType<typeof getDatSubscriptionPreview>>);
  const accepted = {
    jobId: 'job',
  };
  vi.mocked(applyDatSubscription).mockResolvedValue({
    data: accepted,
  } as Awaited<ReturnType<typeof applyDatSubscription>>);
  const user = userEvent.setup();
  const onAccepted = vi.fn();
  render(
    <MantineProvider>
      <DatReplacementReviewModal
        subscription={{
          ...subscription,
          activeDatId: dat.id,
          state: 'UpdateAvailable',
        }}
        onClose={vi.fn()}
        onAccepted={onAccepted}
      />
    </MantineProvider>,
  );
  await screen.findByText('Explore all changes');
  expect(
    screen.getByRole('button', {
      name: 'Decide later',
    }),
  ).toBeInTheDocument();
  expect(getDatSubscriptionPreview).toHaveBeenCalledWith(
    expect.objectContaining({
      path: {
        subscriptionId: 'sub1',
      },
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Apply update',
    }),
  );
  expect(applyDatSubscription).toHaveBeenCalledWith({
    path: {
      subscriptionId: 'sub1',
    },
    body: {
      activeSha256: 'before',
      candidateSha256: 'after',
    },
  });
  await waitFor(() =>
    expect(onAccepted).toHaveBeenCalledWith(accepted, {
      version: '2',
      summary: '1 added',
    }),
  );
});

it('reviews an initial catalog against an explicit empty baseline and submits exact fingerprints', async () => {
  const user = userEvent.setup();
  vi.mocked(getDatSubscriptionPreview).mockResolvedValue({
    data: {
      ...preview,
      activeSha256: '',
      activeVersion: null,
      activeEntries: 0,
    },
  } as Awaited<ReturnType<typeof getDatSubscriptionPreview>>);
  vi.mocked(applyDatSubscription).mockResolvedValue({
    data: {
      jobId: 'job1',
    },
  } as Awaited<ReturnType<typeof applyDatSubscription>>);
  const accepted = vi.fn();
  render(
    <MantineProvider>
      <DatReplacementReviewModal
        subscription={subscription}
        onClose={vi.fn()}
        onAccepted={accepted}
      />
    </MantineProvider>,
  );
  expect(await screen.findByText('No installed document yet')).toBeInTheDocument();
  expect(screen.getByText('Subscription')).toBeInTheDocument();
  expect(screen.queryByText(/PlayStation discs/)).not.toBeInTheDocument();
  expect(applyDatSubscription).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('button', {
      name: 'Import catalog',
    }),
  );
  await waitFor(() => expect(accepted).toHaveBeenCalled());
  expect(applyDatSubscription).toHaveBeenCalledWith({
    path: {
      subscriptionId: 'sub1',
    },
    body: {
      activeSha256: '',
      candidateSha256: 'after',
    },
  });
});
