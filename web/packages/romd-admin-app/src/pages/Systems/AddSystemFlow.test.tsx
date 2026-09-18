import type { ManagedSystemDto } from '@romd/admin-api-client';
import {
  checkCatalogSubscription,
  discoverDatCatalogs,
  getJobById,
  importSystemDat,
  listDatsByPlatform,
  listManagedSystems,
  previewSystemDat,
  setSystemEnabled,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { AddSourceFlow } from './AddSourceFlow';
import { AddSystemFlow } from './AddSystemFlow';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listManagedSystems: vi.fn(),
  discoverDatCatalogs: vi.fn(),
  setSystemEnabled: vi.fn(),
  listDatsByPlatform: vi.fn(),
  previewSystemDat: vi.fn(),
  importSystemDat: vi.fn(),
  getJobById: vi.fn(),
  checkCatalogSubscription: vi.fn(),
}));
const psx: ManagedSystemDto = {
  key: 'psx',
  name: 'PlayStation',
  shortName: 'psx',
  manufacturer: 'Sony',
  aliases: [
    'PS1',
  ],
  enabled: false,
  state: 'NeedsCatalog',
  message: null,
  catalogCount: 0,
  ownedTitles: 0,
};
const result = <T,>(data: T) =>
  ({
    data,
  }) as never;
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(listManagedSystems).mockResolvedValue(
    result([
      psx,
    ]),
  );
  vi.mocked(listDatsByPlatform).mockResolvedValue(result([]));
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
      catalogs: [],
      subscriptions: [],
    }),
  );
  vi.mocked(setSystemEnabled).mockResolvedValue(result(undefined));
});
it('adds a system independently of any source or subscription', async () => {
  const user = userEvent.setup();
  const close = vi.fn();
  render(<AddSystemFlow onClose={close} />);
  await user.type(screen.getByLabelText('Find a system'), 'PS1');
  expect(setSystemEnabled).not.toHaveBeenCalled();
  await user.click(
    await screen.findByRole('button', {
      name: 'Add PlayStation',
    }),
  );
  await waitFor(() =>
    expect(setSystemEnabled).toHaveBeenCalledWith({
      path: {
        systemKey: 'psx',
      },
      body: {
        enabled: true,
      },
    }),
  );
  expect(checkCatalogSubscription).not.toHaveBeenCalled();
  expect(importSystemDat).not.toHaveBeenCalled();
  expect(close).toHaveBeenCalled();
});
it('offers a subscription only for a verified available catalog', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
      subscriptions: [],
      catalogs: [
        {
          catalogId: 'redump/psx/discs',
          systemId: 'psx',
          name: 'Sony - PlayStation',
          provider: 'redump',
          health: 'healthy',
          documentHash: 'a'.repeat(64),
          entryCount: 10974,
          fileCount: 60444,
        },
      ],
    }),
  );
  render(
    <AddSourceFlow
      system={psx}
      onClose={vi.fn()}
    />,
  );
  expect(
    await screen.findByRole('button', {
      name: 'Subscribe to Redump',
    }),
  ).toBeInTheDocument();
  expect(screen.getByText(/10,974 entries/)).toBeInTheDocument();
  expect(checkCatalogSubscription).not.toHaveBeenCalled();
});
it('offers No-Intro only for SNES and describes cartridges as entries and files', async () => {
  vi.mocked(discoverDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      subscriptions: [],
      catalogs: [
        {
          catalogId: 'no-intro/snes/standard',
          systemId: 'snes',
          name: 'Nintendo - Super Nintendo Entertainment System',
          provider: 'no-intro',
          health: 'healthy',
          documentHash: 'b'.repeat(64),
          entryCount: 4358,
          fileCount: 4358,
        },
        {
          catalogId: 'redump/psx/discs',
          systemId: 'psx',
          name: 'Sony - PlayStation',
          provider: 'redump',
          health: 'healthy',
          documentHash: 'a'.repeat(64),
          entryCount: 10974,
          fileCount: 60444,
        },
      ],
    }),
  );
  render(
    <AddSourceFlow
      system={{
        ...psx,
        key: 'snes',
        name: 'Super Nintendo',
        shortName: 'snes',
      }}
      onClose={vi.fn()}
    />,
  );
  expect(
    await screen.findByRole('button', {
      name: 'Subscribe to No-Intro',
    }),
  ).toBeInTheDocument();
  expect(screen.getByText(/4,358 entries · 4,358 files/)).toBeInTheDocument();
  expect(
    screen.queryByRole('button', {
      name: 'Subscribe to Redump',
    }),
  ).not.toBeInTheDocument();
  expect(checkCatalogSubscription).not.toHaveBeenCalled();
});
it('keeps manual upload and setup later usable when the publisher fails', async () => {
  vi.mocked(discoverDatCatalogs).mockRejectedValue(new Error('offline'));
  render(
    <AddSourceFlow
      system={psx}
      onClose={vi.fn()}
    />,
  );
  expect(
    await screen.findByRole('button', {
      name: 'Retry catalog availability',
    }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Done',
    }),
  ).toBeEnabled();
  expect(screen.getByTestId('dat-upload-zone')).toBeInTheDocument();
});
it('unknown DAT asks for a system and reviews coverage before any import', async () => {
  const preview = {
    candidateEntries: 1,
    candidateFiles: 1,
    candidateBiosEntries: 0,
    candidateVersion: '1',
    candidateSha256: 'a'.repeat(64),
    filesAdded: 1,
  };
  vi.mocked(previewSystemDat).mockResolvedValue(
    result({
      name: 'Unknown DAT',
      bytes: 200,
      suggestedSystemKey: null,
      existingDatIds: [],
      preview,
    }),
  );
  const user = userEvent.setup();
  render(
    <AddSystemFlow
      startWithUpload
      onClose={vi.fn()}
    />,
  );
  await screen.findByRole('button', {
    name: 'Choose PlayStation',
  });
  const input = screen.getByTestId('dat-upload-zone').querySelector('input[type="file"]');
  expect(input).not.toBeNull();
  if (!input) throw new Error('Missing file input');
  fireEvent.change(input, {
    target: {
      files: [
        new File(
          [
            'data',
          ],
          'catalog.dat',
          {
            type: 'text/xml',
          },
        ),
      ],
    },
  });
  expect(await screen.findByText(/could not identify one clear match/)).toBeInTheDocument();
  expect(importSystemDat).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Continue with PlayStation',
    }),
  );
  expect(
    await screen.findByText(/does not establish that the catalog covers every release/),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Add reviewed source',
    }),
  ).toBeEnabled();
  expect(importSystemDat).not.toHaveBeenCalled();
});

it('waits for catalog readiness after the upload job completes', async () => {
  vi.mocked(listManagedSystems).mockResolvedValue(
    result([
      {
        ...psx,
        enabled: true,
        state: 'Processing',
      },
    ]),
  );
  vi.mocked(previewSystemDat).mockResolvedValue(
    result({
      name: 'PS1',
      bytes: 200,
      suggestedSystemKey: psx.key,
      existingDatIds: [],
      preview: {
        candidateEntries: 1,
        candidateFiles: 1,
        candidateBiosEntries: 0,
        candidateVersion: '1',
        candidateSha256: 'a'.repeat(64),
        filesAdded: 1,
      },
    }),
  );
  vi.mocked(importSystemDat).mockResolvedValue(
    result({
      jobId: 'job1',
      backgroundJobId: 'job1',
      statusUrl: '/jobs/job1',
    }),
  );
  vi.mocked(getJobById).mockResolvedValue(
    result({
      id: 'job1',
      isTerminal: true,
      hasErrors: false,
      phase: 'Completed',
      progressPercent: '100',
    }),
  );
  const user = userEvent.setup();
  render(
    <AddSourceFlow
      system={psx}
      onClose={vi.fn()}
    />,
  );
  const input = (await screen.findByTestId('dat-upload-zone')).querySelector('input[type="file"]');
  if (!input) throw new Error('Missing file input');
  fireEvent.change(input, {
    target: {
      files: [
        new File(
          [
            'data',
          ],
          'catalog.dat',
          {
            type: 'text/xml',
          },
        ),
      ],
    },
  });
  await user.click(
    await screen.findByRole('button', {
      name: 'Add reviewed source',
    }),
  );
  await waitFor(() => expect(getJobById).toHaveBeenCalled());
  expect(screen.queryByText('Your source is ready')).not.toBeInTheDocument();
  expect(screen.getByText('Processing your source')).toBeInTheDocument();
  vi.mocked(listManagedSystems).mockResolvedValue(
    result([
      {
        ...psx,
        enabled: true,
        state: 'Ready',
        catalogCount: 1,
      },
    ]),
  );
  expect(
    await screen.findByText(
      'Your source is ready',
      {},
      {
        timeout: 5000,
      },
    ),
  ).toBeInTheDocument();
}, 10000);
