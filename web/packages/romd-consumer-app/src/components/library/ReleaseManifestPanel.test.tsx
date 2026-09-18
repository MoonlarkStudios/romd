import type { ConsumerReleaseDto } from '@romd/consumer-api-client';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useIssueReleaseManifest } from '../../hooks/useConsumerLibrary';
import { render } from '../../test/utils/render';
import { ReleaseManifestPanel } from './ReleaseManifestPanel';

vi.mock('../../hooks/useConsumerLibrary', () => ({
  useIssueReleaseManifest: vi.fn(),
}));

const helloSha256 = '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824';

const release: ConsumerReleaseDto = {
  id: 'release-1',
  name: 'World',
  revision: null,
  regions: ['US'],
  languages: ['en'],
  sizeBytes: '5',
  isComplete: true,
};

describe('ReleaseManifestPanel', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn());
    vi.stubGlobal('URL', {
      createObjectURL: vi.fn(() => 'blob:verified'),
      revokeObjectURL: vi.fn(),
    });
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue({
        releaseId: 'release-1',
        titleId: 'title-1',

        systemKey: 'nes',
        name: 'World',
        revision: null,
        isComplete: true,
        runtime: {
          contentType: 'application/octet-stream',
          launch: {
            type: 'file',
            relativePath: 'NES/Mario.rom',
          },
          packaging: 'singleFile',
          minimumInstallBytes: '5',
        },
        items: [
          {
            relativePath: 'NES/Mario.rom',
            role: 'rom',
            sizeBytes: '5',
            sha256: helloSha256,
            isAvailable: true,
            contentGrant: {
              downloadUrl: '/delivery/content/signed-token',
              expiresAt: '2026-06-04T12:00:00Z',
            },
          },
        ],
      }),
      isPending: false,
      isError: false,
    } as ReturnType<typeof useIssueReleaseManifest>);
    vi.mocked(fetch).mockResolvedValue(
      new Response(new TextEncoder().encode('hello'), {
        status: 200,
        headers: {
          'Content-Length': '5',
          'Content-Type': 'application/octet-stream',
        },
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('downloads, verifies, and hands a single file to the browser with one click', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, { withAuth: false });
    expect(useIssueReleaseManifest().mutateAsync).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Download', exact: true }));
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(screen.getByRole('button', { name: 'Save file' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('sent to your browser');
    click.mockRestore();
  });

  it('does not save corrupt bytes and retries with fresh authorization', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    vi.mocked(fetch).mockResolvedValueOnce(new Response('wrong'));
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, { withAuth: false });
    await user.click(screen.getByRole('button', { name: 'Download', exact: true }));
    await screen.findByRole('button', { name: 'Retry' });
    expect(click).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Save file' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(useIssueReleaseManifest().mutateAsync).toHaveBeenCalledTimes(2);
    click.mockRestore();
  });

  it('shows a recoverable preparation error', async () => {
    vi.mocked(useIssueReleaseManifest().mutateAsync).mockRejectedValue(new Error('offline'));
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, { withAuth: false });
    await user.click(screen.getByRole('button', { name: 'Download', exact: true }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
    expect(fetch).not.toHaveBeenCalled();
  });

  it('offers individual files for a multi-file release without downloading them automatically', async () => {
    const issue = vi.mocked(useIssueReleaseManifest().mutateAsync);
    const manifest = await issue(release.id);
    issue.mockClear();
    issue.mockResolvedValue({ ...manifest, items: [...manifest.items, { ...manifest.items[0], relativePath: 'missing.bin', isAvailable: false }] });
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, { withAuth: false });
    await user.click(screen.getByRole('button', { name: 'Download', exact: true }));
    expect(await screen.findByText('missing.bin')).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
    expect(screen.getAllByRole('button', { name: 'Download file' })[1]).toBeDisabled();
  });

  it('aborts work and never saves a result after leaving the release', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    let finish!: (response: Response) => void;
    vi.mocked(fetch).mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    const user = userEvent.setup();
    const { unmount } = render(<ReleaseManifestPanel release={release} />, { withAuth: false });
    await user.click(screen.getByRole('button', { name: 'Download', exact: true }));
    await waitFor(() => expect(fetch).toHaveBeenCalledOnce());
    const signal = vi.mocked(fetch).mock.calls[0][1]?.signal;
    unmount();
    expect(signal?.aborted).toBe(true);
    finish(new Response('hello'));
    await waitFor(() => expect(URL.revokeObjectURL).toHaveBeenCalled());
    expect(click).not.toHaveBeenCalled();
    click.mockRestore();
  });
});
