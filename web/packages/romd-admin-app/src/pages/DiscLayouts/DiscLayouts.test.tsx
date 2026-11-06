import {
  getCdStorageRecipe,
  listOriginalDiscLayoutAudit,
  listOriginalDiscLayoutCandidates,
  listOriginalDiscLayouts,
  type OriginalDiscLayoutDto,
  startMediaReconciliationSubject,
  updateOriginalDiscLayout,
} from '@romd/admin-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { DiscLayouts } from './index';

const state = vi.hoisted(() => ({
  admin: true,
  upload: vi.fn(),
}));
vi.mock('@romd/admin-api-client', async () => ({
  ...(await vi.importActual('@romd/admin-api-client')),
  listOriginalDiscLayouts: vi.fn(),
  getCdStorageRecipe: vi.fn(),
  startMediaReconciliationSubject: vi.fn(),
  listOriginalDiscLayoutCandidates: vi.fn(),
  listOriginalDiscLayoutAudit: vi.fn(),
  updateOriginalDiscLayout: vi.fn(),
}));
vi.mock('../../hooks/usePermissions', () => ({
  usePermissions: () => ({
    hasRole: () => state.admin,
  }),
}));
vi.mock('../../hooks/api/useUpload', () => ({
  useUploadGeneric: () => ({
    mutate: state.upload,
    isPending: false,
    isError: false,
    isSuccess: false,
  }),
}));
const hash = 'ab'.repeat(32);
const layout: OriginalDiscLayoutDto = {
  id: 'layout1',
  revision: '11111111-1111-4111-8111-111111111111',
  state: 'pending',
  issue: 'Dependencies require review',
  entryPoint: 'Disc.cue',
  descriptorSha256: hash,
  descriptorSizeBytes: '64',
  sourceJobId: 'job1',
  sourceRelativePath: 'Disc.cue',
  systemKey: null,
  releaseId: null,
  fingerprint: null,
  dependencies: [
    {
      path: 'Track.bin',
      romId: null,
      sha256: null,
      sizeBytes: null,
    },
  ],
  audit: {
    items: [],
    nextCursor: null,
    hasNextPage: false,
  },
};
const page = <T,>(items: T[]) => ({
  items,
  nextCursor: null,
  hasNextPage: false,
});
const result = <T,>(data: T) => ({
  data,
  error: undefined,
  request: new Request('http://localhost'),
  response: new Response(),
});
beforeEach(() => {
  vi.clearAllMocks();
  state.admin = true;
  vi.mocked(getCdStorageRecipe).mockResolvedValue(
    result({
      available: false,
      recipeVersion: 'chd-ps1-canonical/v1',
      hunkBytes: 39168,
      codecs: [
        'cdzs',
        'cdfl',
      ],
      qualifiedRid: 'linux-x64',
    }),
  );
  vi.mocked(listOriginalDiscLayouts).mockResolvedValue(
    result(
      page([
        layout,
      ]),
    ),
  );
  vi.mocked(listOriginalDiscLayoutCandidates).mockResolvedValue(
    result(
      page([
        {
          releaseId: 'r1',
          systemKey: 'psx',
          systemName: 'PlayStation',
          name: 'Game (USA) (Disc 1)',
          fingerprint: 'fingerprint',
          currentLayoutId: null,
          currentLayoutRevision: null,
          requirements: [
            {
              name: 'Track.bin',
              sha1: 'ab'.repeat(20),
              sizeBytes: '2048',
              originalAvailable: true,
              satisfiedByDescriptor: false,
            },
          ],
          files: [
            {
              romId: 'rom1',
              name: 'Track.bin',
              sha256: hash,
              sizeBytes: '2048',
            },
          ],
        },
      ]),
    ),
  );
  vi.mocked(updateOriginalDiscLayout).mockResolvedValue(result(layout));
});
describe('DiscLayouts', () => {
  it('loads audit continuation from the embedded first page when decision history opens', async () => {
    const audit = {
      revision: 'audit-1',
      action: 'reject',
      at: '2026-10-21T12:00:00Z',
      actor: null,
      reason: 'Latest decision',
    };
    vi.mocked(listOriginalDiscLayouts).mockResolvedValue(
      result(
        page([
          {
            ...layout,
            audit: {
              items: [
                audit,
              ],
              nextCursor: 'older',
              hasNextPage: true,
            },
          },
        ]),
      ),
    );
    vi.mocked(listOriginalDiscLayoutAudit).mockResolvedValue(
      result(
        page([
          {
            ...audit,
            revision: 'audit-2',
            reason: 'Earlier decision',
          },
        ]),
      ),
    );
    render(<DiscLayouts />);
    fireEvent.click(await screen.findByText('Decision history'));
    expect(await screen.findByText(/Earlier decision/)).toBeInTheDocument();
    expect(listOriginalDiscLayoutAudit).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          layoutId: layout.id,
        },
        query: {
          cursor: 'older',
          limit: 100,
        },
      }),
    );
  });

  it('requires admin permission without querying repair data', () => {
    state.admin = false;
    render(<DiscLayouts />);
    expect(screen.getByText(/requires an administrator/)).toBeInTheDocument();
    expect(listOriginalDiscLayouts).not.toHaveBeenCalled();
  });
  it('requires explicit release and exact dependency choices before confirming', async () => {
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Review and repair',
      }),
    );
    expect(
      screen.getByRole('button', {
        name: 'Confirm exact layout',
      }),
    ).toBeDisabled();
    await screen.findByRole('option', {
      name: /Game \(USA\)/,
    });
    fireEvent.change(screen.getByLabelText('Catalog release'), {
      target: {
        value: 'r1',
      },
    });
    expect(
      screen.getByRole('button', {
        name: 'Confirm exact layout',
      }),
    ).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Content for Track.bin'), {
      target: {
        value: 'rom1',
      },
    });
    fireEvent.change(screen.getByLabelText('Repair decision reason'), {
      target: {
        value: 'Original CUE supplied',
      },
    });
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Confirm exact layout',
      }),
    );
    await waitFor(() =>
      expect(updateOriginalDiscLayout).toHaveBeenCalledWith(
        expect.objectContaining({
          path: {
            layoutId: 'layout1',
          },
          headers: {
            'If-Match': `"${layout.revision}"`,
          },
          body: {
            action: 'confirm',
            releaseId: 'r1',
            dependencies: [
              {
                path: 'Track.bin',
                romId: 'rom1',
              },
            ],
            reason: 'Original CUE supplied',
            replacesLayoutId: null,
            replacesRevision: null,
          },
        }),
      ),
    );
  });
  it.each([
    true,
    false,
  ])('uses server descriptor coverage %s when reviewing catalog requirements', async (satisfiedByDescriptor) => {
    vi.mocked(listOriginalDiscLayoutCandidates).mockResolvedValue(
      result(
        page([
          {
            releaseId: 'r1',
            systemKey: 'psx',
            systemName: 'PlayStation',
            name: 'Game',
            fingerprint: 'edition',
            currentLayoutId: null,
            currentLayoutRevision: null,
            files: [
              {
                romId: 'rom1',
                name: 'Track.bin',
                sha256: hash,
                sizeBytes: '2048',
              },
            ],
            requirements: [
              {
                name: 'Track.bin',
                sha1: 'ab'.repeat(20),
                sizeBytes: '2048',
                originalAvailable: true,
                satisfiedByDescriptor: false,
              },
              {
                name: 'CatalogName.cue',
                sha1: 'cd'.repeat(20),
                sizeBytes: '64',
                originalAvailable: false,
                satisfiedByDescriptor,
              },
            ],
          },
        ]),
      ),
    );
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Review and repair',
      }),
    );
    await screen.findByRole('option', {
      name: 'Game · PlayStation',
    });
    fireEvent.change(screen.getByLabelText('Catalog release'), {
      target: {
        value: 'r1',
      },
    });
    fireEvent.change(screen.getByLabelText('Content for Track.bin'), {
      target: {
        value: 'rom1',
      },
    });
    const confirm = screen.getByRole('button', {
      name: 'Confirm exact layout',
    });
    if (!satisfiedByDescriptor) {
      expect(screen.getByLabelText('Content for CatalogName.cue')).toBeInTheDocument();
      expect(confirm).toBeDisabled();
      expect(updateOriginalDiscLayout).not.toHaveBeenCalled();
      return;
    }
    expect(screen.queryByLabelText('Content for CatalogName.cue')).not.toBeInTheDocument();
    expect(screen.getByText('Provided by descriptor: CatalogName.cue')).toBeInTheDocument();
    expect(screen.queryByText('Missing original companion')).not.toBeInTheDocument();
    expect(confirm).toBeEnabled();
    fireEvent.click(confirm);
    await waitFor(() =>
      expect(updateOriginalDiscLayout).toHaveBeenCalledWith(
        expect.objectContaining({
          headers: {
            'If-Match': `"${layout.revision}"`,
          },
          body: expect.objectContaining({
            dependencies: [
              {
                path: 'Track.bin',
                romId: 'rom1',
              },
            ],
          }),
        }),
      ),
    );
  });
  it('requires additional catalog companions even when they are not FILE references', async () => {
    vi.mocked(listOriginalDiscLayoutCandidates).mockResolvedValue(
      result(
        page([
          {
            releaseId: 'r1',
            systemKey: 'psx',
            systemName: 'PlayStation',
            name: 'Game',
            fingerprint: 'fp',
            currentLayoutId: null,
            currentLayoutRevision: null,
            requirements: [
              {
                name: 'Track.bin',
                sha1: 'ab'.repeat(20),
                sizeBytes: '2048',
                originalAvailable: true,
                satisfiedByDescriptor: false,
              },
              {
                name: 'Subcode.sbi',
                sha1: 'cd'.repeat(20),
                sizeBytes: '32',
                originalAvailable: true,
                satisfiedByDescriptor: false,
              },
            ],
            files: [
              {
                romId: 'rom1',
                name: 'Track.bin',
                sha256: hash,
                sizeBytes: '2048',
              },
              {
                romId: 'rom2',
                name: 'Subcode.sbi',
                sha256: 'cd'.repeat(32),
                sizeBytes: '32',
              },
            ],
          },
        ]),
      ),
    );
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Review and repair',
      }),
    );
    await screen.findByRole('option', {
      name: 'Game · PlayStation',
    });
    fireEvent.change(screen.getByLabelText('Catalog release'), {
      target: {
        value: 'r1',
      },
    });
    fireEvent.change(screen.getByLabelText('Content for Track.bin'), {
      target: {
        value: 'rom1',
      },
    });
    expect(
      screen.getByRole('button', {
        name: 'Confirm exact layout',
      }),
    ).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Content for Subcode.sbi'), {
      target: {
        value: 'rom2',
      },
    });
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Confirm exact layout',
      }),
    );
    await waitFor(() =>
      expect(updateOriginalDiscLayout).toHaveBeenCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            dependencies: [
              {
                path: 'Track.bin',
                romId: 'rom1',
              },
              {
                path: 'Subcode.sbi',
                romId: 'rom2',
              },
            ],
          }),
        }),
      ),
    );
  });
  it('pins the observed prior layout when explicitly replacing it', async () => {
    const revision = '22222222-2222-4222-8222-222222222222';
    const response = await vi.mocked(listOriginalDiscLayoutCandidates)({
      path: {
        layoutId: 'layout1',
      },
    });
    if (!response.data) throw new Error('Candidate fixture missing');
    vi.mocked(listOriginalDiscLayoutCandidates).mockResolvedValue(
      result(
        page(
          response.data.items.map((c) => ({
            ...c,
            currentLayoutId: 'prior',
            currentLayoutRevision: revision,
          })),
        ),
      ),
    );
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Review and repair',
      }),
    );
    await screen.findByRole('option', {
      name: /Game \(USA\)/,
    });
    fireEvent.change(screen.getByLabelText('Catalog release'), {
      target: {
        value: 'r1',
      },
    });
    expect(screen.getByText(/currently selected original descriptor/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Content for Track.bin'), {
      target: {
        value: 'rom1',
      },
    });
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Confirm exact layout',
      }),
    );
    await waitFor(() =>
      expect(updateOriginalDiscLayout).toHaveBeenCalledWith(
        expect.objectContaining({
          body: expect.objectContaining({
            replacesLayoutId: 'prior',
            replacesRevision: revision,
          }),
        }),
      ),
    );
  });
  it('rejects with the observed revision and no invented content binding', async () => {
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Review and repair',
      }),
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Reject descriptor',
      }),
    );
    await waitFor(() =>
      expect(updateOriginalDiscLayout).toHaveBeenCalledWith(
        expect.objectContaining({
          headers: {
            'If-Match': `"${layout.revision}"`,
          },
          body: expect.objectContaining({
            action: 'reject',
            releaseId: null,
            dependencies: null,
          }),
        }),
      ),
    );
  });
  it('allows detaching a catalog-stale accepted layout', async () => {
    vi.mocked(listOriginalDiscLayouts).mockResolvedValue(
      result(
        page([
          {
            ...layout,
            state: 'stale',
            issue: 'CatalogContentUnavailable',
            releaseId: 'r1',
          },
        ]),
      ),
    );
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Review and repair',
      }),
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Detach layout',
      }),
    );
    await waitFor(() =>
      expect(updateOriginalDiscLayout).toHaveBeenCalledWith(
        expect.objectContaining({
          headers: {
            'If-Match': `"${layout.revision}"`,
          },
          body: expect.objectContaining({
            action: 'detach',
          }),
        }),
      ),
    );
  });
  it('shows a stale correction conflict and a reload action', async () => {
    vi.mocked(updateOriginalDiscLayout).mockResolvedValue({
      data: undefined,
      error: {
        title: 'Conflict',
      },
      request: new Request('http://localhost'),
      response: new Response(null, {
        status: 412,
      }),
    });
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Review and repair',
      }),
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Reject descriptor',
      }),
    );
    expect(await screen.findByText(/Reload before confirming/)).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Reload layouts',
      }),
    );
    await waitFor(() =>
      expect(vi.mocked(listOriginalDiscLayouts).mock.calls.length).toBeGreaterThan(1),
    );
  });
  it('queues whole-disc compression using the exact reviewed revision', async () => {
    vi.mocked(getCdStorageRecipe).mockResolvedValue(
      result({
        available: true,
        recipeVersion: 'chd-ps1-canonical/v1',
        hunkBytes: 39168,
        codecs: [
          'cdzs',
          'cdfl',
        ],
        qualifiedRid: 'linux-x64',
      }),
    );
    vi.mocked(listOriginalDiscLayouts).mockResolvedValue(
      result(
        page([
          {
            ...layout,
            dependencies: [
              {
                path: 'Track.bin',
                romId: 'rom1',
                sha256: hash,
                sizeBytes: '150528',
              },
            ],
          },
        ]),
      ),
    );
    vi.mocked(startMediaReconciliationSubject).mockResolvedValue(
      result({
        jobId: 'compression-job',
      }),
    );
    // Self-hosted admins may serve over plain HTTP, where randomUUID is unavailable.
    const randomUUID = vi.spyOn(crypto, 'randomUUID');
    render(<DiscLayouts />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Compress to CHD',
      }),
    );
    expect(
      await screen.findByRole('link', {
        name: 'Compression job',
      }),
    ).toHaveAttribute('href', '/jobs/compression-job');
    expect(startMediaReconciliationSubject).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          subjectKind: 'OriginalLayout',
          subjectId: layout.id,
        },
        body: {
          requestId: expect.stringMatching(
            /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/,
          ),
          conversionRequested: true,
        },
      }),
    );
    expect(randomUUID).not.toHaveBeenCalled();
    randomUUID.mockRestore();
    expect(updateOriginalDiscLayout).not.toHaveBeenCalled();
  });
});
