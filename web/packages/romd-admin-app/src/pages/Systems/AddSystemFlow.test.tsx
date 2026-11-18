import type {
  DatCatalogSubscription,
  ManagedSystem,
  PublishedDatCatalog,
} from '@romd/admin-api-client';
import {
  applyDatSubscription,
  checkDatSubscription,
  createDatSubscription,
  getDatSubscriptionPreview,
  getJob,
  importSystemDat,
  listDatCatalogs,
  listDatSubscriptions,
  listManagedSystems,
  listSystemDats,
  previewSystemDat,
  setSystemEnabled,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { completePage } from '../../test/fixtures/referencePages';
import { render } from '../../test/utils/render';
import { AddSourceFlow } from './AddSourceFlow';
import { AddSystemFlow } from './AddSystemFlow';

vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listManagedSystems: vi.fn(),
  listDatCatalogs: vi.fn(),
  listDatSubscriptions: vi.fn(),
  setSystemEnabled: vi.fn(),
  listSystemDats: vi.fn(),
  previewSystemDat: vi.fn(),
  importSystemDat: vi.fn(),
  getJob: vi.fn(),
  applyDatSubscription: vi.fn(),
  checkDatSubscription: vi.fn(),
  createDatSubscription: vi.fn(),
  getDatSubscriptionPreview: vi.fn(),
}));
const psx: ManagedSystem = {
  key: 'psx',
  name: 'PlayStation',
  shortName: 'psx',
  manufacturer: 'Sony',
  enabled: false,
  state: 'NeedsCatalog',
  message: null,
  catalogCount: 0,
  ownedTitles: 0,
};
const page = <T,>(items: T[]) => ({
  items,
  nextCursor: null,
  hasNextPage: false,
});
const result = <T,>(data: T) =>
  ({
    data,
  }) as never;
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(listManagedSystems).mockResolvedValue(
    result(
      completePage([
        psx,
      ]),
    ),
  );
  vi.mocked(listSystemDats).mockResolvedValue(result(page([])));
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
      catalogs: [],
    }),
  );
  vi.mocked(listDatSubscriptions).mockResolvedValue(result([]));
  vi.mocked(setSystemEnabled).mockResolvedValue(result(undefined));
});
it('lets assistive technology dismiss system setup without adding a system', async () => {
  const user = userEvent.setup();
  const close = vi.fn();
  render(<AddSystemFlow onClose={close} />);

  await user.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Close',
      exact: true,
    }),
  );

  expect(close).toHaveBeenCalledOnce();
  expect(setSystemEnabled).not.toHaveBeenCalled();
  expect(createDatSubscription).not.toHaveBeenCalled();
});
it('adds a system independently only after choosing Add without a DAT', async () => {
  const user = userEvent.setup();
  const close = vi.fn();
  render(<AddSystemFlow onClose={close} />);
  await user.type(screen.getByLabelText('Find a system'), 'PS1');
  expect(setSystemEnabled).not.toHaveBeenCalled();
  await user.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  expect(
    await screen.findByRole('heading', {
      name: 'Choose the first DAT',
    }),
  ).toBeVisible();
  expect(setSystemEnabled).not.toHaveBeenCalled();
  await user.click(
    screen.getByRole('button', {
      name: 'Add without a DAT',
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
  expect(createDatSubscription).not.toHaveBeenCalled();
  expect(importSystemDat).not.toHaveBeenCalled();
  expect(close).toHaveBeenCalled();
});
it.each([
  'healthy',
  'failed',
])('offers a retained verified catalog with %s refresh health', async (health) => {
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
      catalogs: [
        {
          catalogId: 'redump/psx/discs',
          systemId: 'psx',
          name: 'Sony - PlayStation',
          provider: 'redump',
          health,
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
      name: 'Subscribe and check',
    }),
  ).toBeInTheDocument();
  expect(screen.getByText(/10,974 DAT entries/)).toBeInTheDocument();
  if (health === 'failed')
    expect(await screen.findByText('Latest refresh delayed')).toBeInTheDocument();
  expect(createDatSubscription).not.toHaveBeenCalled();
});
it('offers No-Intro only for SNES and describes cartridges as entries and files', async () => {
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
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
      name: 'Subscribe and check',
    }),
  ).toBeInTheDocument();
  expect(screen.getByText(/4,358 DAT entries · 4,358 expected files/)).toBeInTheDocument();
  expect(screen.queryByText('Sony - PlayStation')).not.toBeInTheDocument();
  expect(createDatSubscription).not.toHaveBeenCalled();
});
it('keeps manual upload and setup later usable when the publisher fails', async () => {
  vi.mocked(listDatCatalogs).mockRejectedValue(new Error('offline'));
  render(<AddSystemFlow onClose={vi.fn()} />);
  await userEvent.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  expect(
    await screen.findByRole('button', {
      name: 'Retry catalog availability',
    }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Add without a DAT',
    }),
  ).toBeEnabled();
  expect(screen.getByTestId('dat-upload-zone')).toBeInTheDocument();
  expect(
    screen.queryByText('There is no verified subscription available for this system.'),
  ).not.toBeInTheDocument();
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
  const input = (await screen.findByTestId('dat-upload-zone')).querySelector('input[type="file"]');
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
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  await user.click(
    screen.getByRole('button', {
      name: 'Continue with PlayStation',
    }),
  );
  expect(await screen.findByText(/A DAT may cover only part/)).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Add reviewed source',
    }),
  ).toBeEnabled();
  expect(importSystemDat).not.toHaveBeenCalled();
});

it('waits for catalog readiness after the upload job completes', async () => {
  vi.mocked(listManagedSystems).mockResolvedValue(
    result(
      completePage([
        {
          ...psx,
          enabled: true,
          state: 'Processing',
        },
      ]),
    ),
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
    }),
  );
  vi.mocked(getJob).mockResolvedValue(
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
  await waitFor(() => expect(getJob).toHaveBeenCalled());
  expect(screen.queryByText('Your source is ready')).not.toBeInTheDocument();
  expect(screen.getByText('Processing your source')).toBeInTheDocument();
  vi.mocked(listManagedSystems).mockResolvedValue(
    result(
      completePage([
        {
          ...psx,
          enabled: true,
          state: 'Ready',
          catalogCount: 1,
        },
      ]),
    ),
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
it('subscribes by enrolling the catalog, then finishes when it is already up to date', async () => {
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
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
  vi.mocked(createDatSubscription).mockResolvedValue(
    result({
      id: 'sub',
      catalogId: 'redump/psx/discs',
      name: 'Sony - PlayStation',
      state: 'UpToDate',
    }),
  );
  const close = vi.fn();
  render(
    <AddSourceFlow
      system={psx}
      onClose={close}
    />,
  );
  const subscribe = await screen.findByRole('button', {
    name: 'Subscribe and check',
  });
  await waitFor(() => expect(subscribe).toBeEnabled());
  await userEvent.click(subscribe);
  await waitFor(() => expect(close).toHaveBeenCalled());
  expect(createDatSubscription).toHaveBeenCalledWith({
    body: {
      catalogId: 'redump/psx/discs',
    },
  });
});
it('waits for local subscription status before offering to subscribe', async () => {
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
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
  vi.mocked(listDatSubscriptions).mockResolvedValue({
    error: {},
  } as never);
  render(
    <AddSourceFlow
      system={psx}
      onClose={vi.fn()}
    />,
  );
  expect(
    await screen.findByRole('button', {
      name: 'Subscribe and check',
    }),
  ).toBeDisabled();
  expect(createDatSubscription).not.toHaveBeenCalled();
});

it.each([
  'PS1',
  'PSX',
  'Pláy-Státion',
])('uses complete server alias-aware search for %s without embedded aliases', async (search) => {
  vi.mocked(listManagedSystems).mockImplementation(async (options) =>
    result(
      completePage(
        options?.query?.query === search
          ? [
              psx,
            ]
          : [],
      ),
    ),
  );
  render(<AddSystemFlow onClose={vi.fn()} />);
  await userEvent.type(screen.getByLabelText('Find a system'), search);
  expect(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  ).toBeInTheDocument();
  expect(listManagedSystems).toHaveBeenCalledWith(
    expect.objectContaining({
      signal: expect.any(AbortSignal),
      query: {
        query: search,
        cursor: undefined,
        limit: 100,
      },
    }),
  );
});

const playStationOffer: PublishedDatCatalog = {
  catalogId: 'redump/psx/discs',
  systemId: psx.key,
  name: 'Sony - PlayStation',
  provider: 'redump',
  health: 'healthy',
  documentHash: 'a'.repeat(64),
  entryCount: 10974,
  fileCount: 60444,
};
const pendingSubscription: DatCatalogSubscription = {
  id: 'sub',
  catalogId: playStationOffer.catalogId,
  systemId: psx.key,
  name: playStationOffer.name,
  systemKey: psx.key,
  activeDatId: null,
  state: 'ReadyToImport',
  lastCheckedAt: null,
  message: null,
  candidateSha256: 'b'.repeat(64),
  jobId: null,
};

function offer(...catalogs: PublishedDatCatalog[]) {
  vi.mocked(listDatCatalogs).mockResolvedValue(
    result({
      enabled: true,
      message: null,
      catalogs,
    }),
  );
}

it('preselects the sole verified DAT, then imports only after the explicit review', async () => {
  offer(playStationOffer);
  vi.mocked(createDatSubscription).mockResolvedValue(result(pendingSubscription));
  vi.mocked(getDatSubscriptionPreview).mockResolvedValue(
    result({
      activeSha256: 'none',
      candidateSha256: pendingSubscription.candidateSha256,
      activeEntries: 0,
      candidateEntries: playStationOffer.entryCount,
      entriesAdded: playStationOffer.entryCount,
      entriesRemoved: 0,
      entriesChanged: 0,
      filesAdded: playStationOffer.fileCount,
      filesRemoved: 0,
      filesChanged: 0,
      hashesChanged: 0,
      activeBiosEntries: 0,
      candidateBiosEntries: 0,
      changes: [],
      truncated: false,
    }),
  );
  vi.mocked(applyDatSubscription).mockResolvedValue(
    result({
      jobId: 'job-1',
    }),
  );
  vi.mocked(getJob).mockResolvedValue(
    result({
      id: 'job-1',
      isTerminal: true,
      hasErrors: false,
      phase: 'Completed',
      progressPercent: '100',
    }),
  );
  const open = vi.fn();
  const user = userEvent.setup();
  render(
    <AddSystemFlow
      onClose={vi.fn()}
      onSystemOpen={open}
    />,
  );

  await user.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  const subscribe = await screen.findByRole('button', {
    name: 'Add system and check',
  });
  await waitFor(() => expect(subscribe).toBeEnabled());
  expect(
    screen.getByRole('radio', {
      name: playStationOffer.name,
    }),
  ).toBeChecked();
  expect(setSystemEnabled).not.toHaveBeenCalled();

  await user.click(subscribe);
  const apply = await screen.findByRole('button', {
    name: 'Import reviewed catalog',
  });
  expect(createDatSubscription).toHaveBeenCalledWith({
    body: {
      catalogId: playStationOffer.catalogId,
    },
  });
  expect(applyDatSubscription).not.toHaveBeenCalled();
  vi.mocked(listManagedSystems).mockResolvedValue(
    result(
      completePage([
        {
          ...psx,
          enabled: true,
          state: 'Ready',
          catalogCount: 1,
        },
      ]),
    ),
  );
  await user.click(apply);
  expect(applyDatSubscription).toHaveBeenCalledWith({
    path: {
      subscriptionId: pendingSubscription.id,
    },
    body: {
      activeSha256: 'none',
      candidateSha256: pendingSubscription.candidateSha256,
    },
  });
  await user.click(
    await screen.findByRole('button', {
      name: 'Open catalog sources',
    }),
  );
  expect(open).toHaveBeenCalledWith(psx.key, 'sources');
});

it('requires a choice when the signed directory offers multiple formats', async () => {
  const second = {
    ...playStationOffer,
    catalogId: 'no-intro/psx/digital',
    provider: 'no-intro',
    name: 'Sony - PlayStation (Digital)',
  };
  offer(playStationOffer, second);
  vi.mocked(createDatSubscription).mockResolvedValue(
    result({
      ...pendingSubscription,
      catalogId: second.catalogId,
      state: 'UpToDate',
    }),
  );
  const user = userEvent.setup();
  render(<AddSystemFlow onClose={vi.fn()} />);
  await user.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );

  const choice = await screen.findByRole('radio', {
    name: second.name,
  });
  const subscribe = screen.getByRole('button', {
    name: 'Add system and check',
  });
  expect(choice).not.toBeChecked();
  expect(
    screen.getByRole('radio', {
      name: playStationOffer.name,
    }),
  ).not.toBeChecked();
  expect(subscribe).toBeDisabled();
  expect(createDatSubscription).not.toHaveBeenCalled();

  await waitFor(() => expect(choice).toBeEnabled());
  await user.click(choice);
  await user.click(subscribe);
  await waitFor(() =>
    expect(createDatSubscription).toHaveBeenCalledWith({
      body: {
        catalogId: second.catalogId,
      },
    }),
  );
});

it('retries the existing subscription after its first check fails', async () => {
  offer(playStationOffer);
  vi.mocked(createDatSubscription).mockResolvedValue(
    result({
      ...pendingSubscription,
      state: 'CheckFailed',
      message: 'Catalog verification failed.',
    }),
  );
  vi.mocked(checkDatSubscription).mockResolvedValue(
    result({
      ...pendingSubscription,
      state: 'UpToDate',
    }),
  );
  const open = vi.fn();
  const user = userEvent.setup();
  render(
    <AddSystemFlow
      onClose={vi.fn()}
      onSystemOpen={open}
    />,
  );
  await user.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );

  const subscribe = await screen.findByRole('button', {
    name: 'Add system and check',
  });
  await waitFor(() => expect(subscribe).toBeEnabled());
  await user.click(subscribe);
  await user.click(
    await screen.findByRole('button', {
      name: 'Retry check',
    }),
  );

  await waitFor(() => expect(open).toHaveBeenCalledWith(psx.key, 'sources'));
  expect(checkDatSubscription).toHaveBeenCalledWith({
    path: {
      subscriptionId: pendingSubscription.id,
    },
  });
  expect(createDatSubscription).toHaveBeenCalledOnce();
  expect(setSystemEnabled).toHaveBeenCalledOnce();
  expect(applyDatSubscription).not.toHaveBeenCalled();
});

it('restores a removed system with its saved setup only after confirmation', async () => {
  vi.mocked(listManagedSystems).mockResolvedValue(
    result(
      completePage([
        {
          ...psx,
          state: 'Ready',
          catalogCount: 1,
        },
      ]),
    ),
  );
  const open = vi.fn();
  const user = userEvent.setup();
  render(
    <AddSystemFlow
      onClose={vi.fn()}
      onSystemOpen={open}
    />,
  );
  await user.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  expect(setSystemEnabled).not.toHaveBeenCalled();
  await user.click(
    await screen.findByRole('button', {
      name: 'Restore system',
    }),
  );

  await waitFor(() => expect(open).toHaveBeenCalledWith(psx.key, 'sources'));
  expect(setSystemEnabled).toHaveBeenCalledOnce();
  expect(createDatSubscription).not.toHaveBeenCalled();
});

it('previews an uploaded first DAT without adding the system and imports the reviewed bytes', async () => {
  offer(playStationOffer);
  const file = new File(
    [
      'data',
    ],
    'catalog.dat',
    {
      type: 'text/xml',
    },
  );
  const reviewedHash = 'c'.repeat(64);
  vi.mocked(previewSystemDat).mockResolvedValue(
    result({
      name: playStationOffer.name,
      bytes: file.size,
      suggestedSystemKey: psx.key,
      existingDatIds: [],
      preview: {
        candidateEntries: 1,
        candidateFiles: 1,
        candidateBiosEntries: 0,
        candidateVersion: '1',
        candidateSha256: reviewedHash,
        filesAdded: 1,
      },
    }),
  );
  vi.mocked(importSystemDat).mockResolvedValue(
    result({
      jobId: 'job-1',
    }),
  );
  vi.mocked(getJob).mockResolvedValue(
    result({
      id: 'job-1',
      isTerminal: false,
      phase: 'Validating',
      progressPercent: '12',
    }),
  );
  const user = userEvent.setup();
  render(<AddSystemFlow onClose={vi.fn()} />);
  await user.click(
    await screen.findByRole('button', {
      name: 'Choose PlayStation',
    }),
  );
  const upload = await screen.findByRole('button', {
    name: 'Upload a DAT',
  });
  await user.click(upload);
  const input = screen.getByTestId('dat-upload-zone').querySelector('input[type="file"]');
  if (!input) throw new Error('Missing file input');
  fireEvent.change(input, {
    target: {
      files: [
        file,
      ],
    },
  });
  const importButton = await screen.findByRole('button', {
    name: 'Add reviewed source',
  });

  expect(previewSystemDat).toHaveBeenCalledWith({
    body: {
      file,
    },
  });
  expect(setSystemEnabled).not.toHaveBeenCalled();
  expect(importSystemDat).not.toHaveBeenCalled();
  await user.click(importButton);
  expect(importSystemDat).toHaveBeenCalledWith({
    path: {
      systemKey: psx.key,
    },
    body: {
      file,
    },
    query: {
      sha256: reviewedHash,
    },
  });
  expect(await screen.findByText('Processing your source')).toBeInTheDocument();
});
