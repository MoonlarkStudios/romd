import type { ConsumerReleaseDto, ConsumerReleaseManifestDto } from '@romd/consumer-api-client';
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
  regions: [
    'US',
  ],
  languages: [
    'en',
  ],
  sizeBytes: '5',
  isComplete: true,
};

describe('ReleaseManifestPanel', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn());
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue({
        isDeliveryComplete: true,
        serverInstanceId: '11111111-1111-4111-8111-111111111111',

        releaseId: 'release-1',
        titleId: 'title-1',

        systemKey: 'nes',
        name: 'World',
        revision: null,
        isComplete: true,
        runtime: {
          contentType: 'single_rom',
          launch: {
            type: 'file',
            relativePath: 'NES/Mario.rom',
          },
          packaging: 'direct_files',
          minimumInstallBytes: '5',
        },
        items: [
          {
            provenance: 'Original' as const,
            formats: [
              {
                format: 'original' as const,
                label: 'Original',
                state: 'available' as const,
                relativePath: 'NES/Mario.rom',
                filename: 'NES/Mario.rom'.split('/').at(-1),
                sizeBytes: '5',
                sha256: helloSha256,
                contentGrant: {
                  downloadUrl: '/delivery/content/signed-token',
                  expiresAt: '2026-06-04T12:00:00Z',
                },
                profileVersion: 'identity/v1',
                runtimeApplicability: [],
              },
            ],

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

  it('labels generated CUE provenance and uses its freshly issued dedicated grant', async () => {
    const initial = generatedManifest();
    const fresh = generatedManifest();
    fresh.items[0].contentGrant = {
      downloadUrl: '/delivery/derived-descriptor/fresh.signature',
      expiresAt: '2030-01-01T00:15:00Z',
    };
    const issue = vi.mocked(useIssueReleaseManifest().mutateAsync);
    issue.mockReset().mockResolvedValueOnce(initial).mockResolvedValueOnce(fresh);
    const downloads: {
      url: string;
      filename: string;
    }[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloads.push({
        url: this.href,
        filename: this.download,
      });
    });
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await user.click(
      screen.getByText('Files', {
        exact: true,
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Download generated CUE',
      }),
    );
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(downloads).toEqual([
      {
        url: expect.stringContaining('/delivery/derived-descriptor/fresh.signature'),
        filename: 'disc.cue',
      },
    ]);
    expect(issue).toHaveBeenCalledTimes(2);
    expect(fetch).not.toHaveBeenCalled();
    click.mockRestore();
  });
  it('does not silently substitute an arriving original for a generated CUE choice', async () => {
    const initial = generatedManifest();
    const fresh = generatedManifest();
    const cue = fresh.items[0];
    cue.provenance = 'Original';
    cue.contentGrant = {
      downloadUrl: '/delivery/content/original.signature',
      expiresAt: '2030-01-01T00:15:00Z',
    };
    cue.formats = [
      {
        format: 'original',
        label: 'Original',
        state: 'available',
        relativePath: 'disc.cue',
        filename: 'disc.cue',
        sizeBytes: cue.sizeBytes,
        sha256: cue.sha256,
        profileVersion: 'identity/v1',
        runtimeApplicability: [],
        contentGrant: cue.contentGrant,
      },
    ];
    fresh.isComplete = true;
    vi.mocked(useIssueReleaseManifest().mutateAsync)
      .mockReset()
      .mockResolvedValueOnce(initial)
      .mockResolvedValueOnce(fresh);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await user.click(
      screen.getByText('Files', {
        exact: true,
      }),
    );
    await user.click(
      await screen.findByRole('button', {
        name: 'Download generated CUE',
      }),
    );
    expect(
      await screen.findByText('We couldn’t prepare or download this file. Try again.'),
    ).toBeInTheDocument();
    expect(click).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
    click.mockRestore();
  });

  it('hands a fresh signed URL to the browser without fetching bytes', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    expect(useIssueReleaseManifest().mutateAsync).not.toHaveBeenCalled();
    await user.click(
      screen.getByRole('button', {
        name: 'Download',
        exact: true,
      }),
    );
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(fetch).not.toHaveBeenCalled();
    expect(screen.getByRole('status')).toHaveTextContent('Download started');
    click.mockRestore();
  });

  it('shows a recoverable preparation error', async () => {
    vi.mocked(useIssueReleaseManifest().mutateAsync).mockRejectedValue(new Error('offline'));
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Download',
        exact: true,
      }),
    );
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
    expect(fetch).not.toHaveBeenCalled();
  });

  it('offers individual files for a multi-file release without downloading them automatically', async () => {
    const issue = vi.mocked(useIssueReleaseManifest().mutateAsync);
    const manifest = await issue(release.id);
    issue.mockClear();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        ...manifest.items,
        {
          ...manifest.items[0],
          relativePath: 'missing.bin',
          isAvailable: false,
          sha256: null,
          contentGrant: null,
          formats: [
            {
              ...manifest.items[0].formats[0],
              state: 'unsupported',
              relativePath: 'missing.bin',
              filename: undefined,
              sizeBytes: undefined,
              sha256: undefined,
              contentGrant: null,
            },
          ],
        },
      ],
    });
    const user = userEvent.setup();
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await user.click(
      screen.getByRole('button', {
        name: 'Download',
        exact: true,
      }),
    );
    expect(await screen.findByText('missing.bin')).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
    expect(
      screen.getAllByRole('button', {
        name: 'Original unsupported',
      })[0],
    ).toBeDisabled();
  });

  it('does not start a download if preparation completes after unmount', async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const issue = vi.mocked(useIssueReleaseManifest().mutateAsync);
    const manifest = await issue(release.id);
    let finish!: (value: typeof manifest) => void;
    issue.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    const { unmount } = render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Download',
        exact: true,
      }),
    );
    unmount();
    finish(manifest);
    await waitFor(() => expect(click).not.toHaveBeenCalled());
    expect(fetch).not.toHaveBeenCalled();
  });

  async function withChdFormats() {
    const { issue, manifest } = await withDiscFormats();
    const originalPath = 'PS2/Game.iso';
    const chdPath = 'PS2/Game.chd';
    const next = {
      ...manifest,
      systemKey: 'ps2',
      runtime: {
        contentType: 'single_rom',
        launch: {
          type: 'file',
          relativePath: originalPath,
        },
        packaging: 'direct_files',
        minimumInstallBytes: '8192',
      },
      items: [
        {
          ...manifest.items[0],
          relativePath: originalPath,
          sizeBytes: '8192',
          sha256: 'a'.repeat(64),
          contentGrant: {
            downloadUrl: '/delivery/content/legacy-original',
            expiresAt: '2026-10-01T12:00:00Z',
          },
          formats: [
            {
              ...manifest.items[0].formats[0],
              relativePath: originalPath,
              filename: 'Game.iso',
              sizeBytes: '8192',
              sha256: 'a'.repeat(64),
            },
            {
              format: 'chd' as const,
              label: 'CHD',
              fileExtension: '.chd',
              state: 'available' as const,
              relativePath: chdPath,
              filename: 'Game.chd',
              sizeBytes: '4096',
              sha256: 'b'.repeat(64),
              md5: 'c'.repeat(32),
              sha1: 'd'.repeat(40),
              profileVersion: 'chd-observed/v1',
              runtimeApplicability: [
                'pcsx2',
              ],
              runtime: {
                contentType: 'single_rom',
                launch: {
                  type: 'file',
                  relativePath: chdPath,
                },
                packaging: 'direct_files',
                minimumInstallBytes: '4096',
              },
              contentGrant: {
                downloadUrl: '/delivery/content/chd-old',
                expiresAt: '2026-10-01T12:00:00Z',
              },
            },
          ],
        },
      ],
    } satisfies ConsumerReleaseManifestDto;
    issue.mockResolvedValue(next);
    return {
      issue,
      manifest: next,
    };
  }

  it('refreshes CHD using the Original item key and hands off its fresh artifact grant', async () => {
    const { issue, manifest } = await withChdFormats();
    const downloads: {
      url: string;
      filename: string;
    }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloads.push({
        url: this.href,
        filename: this.download,
      });
    });
    render(
      <ReleaseManifestPanel
        release={{
          ...release,
          sizeBytes: '8192',
        }}
      />,
      {
        withAuth: false,
      },
    );
    const user = userEvent.setup();
    await user.click(screen.getByText('Files'));
    const button = await screen.findByRole('button', {
      name: 'Download CHD',
    });
    expect(screen.getByText('Game.chd')).toBeVisible();
    expect(screen.getByText('4 KB')).toBeVisible();
    expect(screen.getByText('Game.iso')).toBeVisible();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            manifest.items[0].formats[0],
            {
              ...manifest.items[0].formats[1],
              contentGrant: {
                downloadUrl: '/delivery/content/chd-fresh',
                expiresAt: '2026-10-01T13:00:00Z',
              },
            },
          ],
        },
      ],
    });
    await user.click(button);
    await waitFor(() => expect(issue).toHaveBeenCalledTimes(2));
    expect(screen.getByRole('status')).toHaveTextContent('Download started');
    await waitFor(() =>
      expect(downloads).toEqual([
        {
          url: expect.stringContaining('/delivery/content/chd-fresh'),
          filename: 'Game.chd',
        },
      ]),
    );
    expect(issue).toHaveBeenNthCalledWith(1, release.id);
    expect(issue).toHaveBeenNthCalledWith(2, release.id);
    expect(issue).toHaveBeenCalledTimes(2);
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([
    'withdrawn',
    'null-grant',
  ] as const)('keeps CHD on retry after %s and never uses an Original grant', async (defect) => {
    const { issue, manifest } = await withChdFormats();
    const downloads: {
      url: string;
      filename: string;
    }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloads.push({
        url: this.href,
        filename: this.download,
      });
    });
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    const user = userEvent.setup();
    await user.click(screen.getByText('Files'));
    const button = await screen.findByRole('button', {
      name: 'Download CHD',
    });
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats:
            defect === 'withdrawn'
              ? [
                  manifest.items[0].formats[0],
                ]
              : [
                  manifest.items[0].formats[0],
                  {
                    ...manifest.items[0].formats[1],
                    contentGrant: null,
                  },
                ],
        },
      ],
    });
    await user.click(button);
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
    expect(downloads).toEqual([]);
    await user.click(
      screen.getByRole('button', {
        name: 'Retry',
      }),
    );
    await waitFor(() => expect(issue).toHaveBeenCalledTimes(3));
    expect(downloads).toEqual([]);
    expect(screen.getByRole('alert')).toHaveTextContent('Try again');
    issue.mockResolvedValue(manifest);
    await user.click(
      screen.getByRole('button', {
        name: 'Retry',
      }),
    );
    await waitFor(() =>
      expect(downloads).toEqual([
        {
          url: expect.stringContaining('/delivery/content/chd-old'),
          filename: 'Game.chd',
        },
      ]),
    );
    expect(issue).toHaveBeenCalledTimes(4);
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([
    'filename',
    'sizeBytes',
    'sha256',
    'contentGrant',
  ] as const)('disables available CHD with missing %s without using logical metadata', async (field) => {
    const { issue, manifest } = await withChdFormats();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            manifest.items[0].formats[0],
            {
              ...manifest.items[0].formats[1],
              [field]: null,
            },
          ],
        },
      ],
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(screen.getByText('Files'));
    const button = await screen.findByRole('button', {
      name: 'Download CHD',
    });
    expect(button).toBeDisabled();
    await userEvent.click(button);
    expect(issue).toHaveBeenCalledTimes(1);
    expect(click).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([
    'preparing',
    'unsupported',
  ] as const)('disables CHD marked %s even when artifact metadata is present', async (state) => {
    const { issue, manifest } = await withChdFormats();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            {
              ...manifest.items[0].formats[1],
              state,
            },
          ],
        },
      ],
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(screen.getByText('Files'));
    expect(
      await screen.findByRole('button', {
        name: `CHD ${state}`,
      }),
    ).toBeDisabled();
    expect(issue).toHaveBeenCalledTimes(1);
    expect(click).not.toHaveBeenCalled();
  });

  it('defaults a PS2 manifest with CHD to the Original artifact', async () => {
    const { issue } = await withChdFormats();
    const downloads: {
      url: string;
      filename: string;
    }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloads.push({
        url: this.href,
        filename: this.download,
      });
    });
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Download',
        exact: true,
      }),
    );
    await waitFor(() =>
      expect(downloads).toEqual([
        {
          url: expect.stringContaining('/delivery/content/original-format'),
          filename: 'Game.iso',
        },
      ]),
    );
    expect(issue).toHaveBeenCalledTimes(1);
    expect(fetch).not.toHaveBeenCalled();
  });

  async function withDiscFormats() {
    const issue = vi.mocked(useIssueReleaseManifest().mutateAsync);
    const manifest = await issue(release.id);
    const item = manifest.items[0];
    const next = {
      ...manifest,
      items: [
        {
          ...item,
          relativePath: 'game.iso',
          sizeBytes: '1024',
          formats: [
            {
              format: 'original' as const,
              label: 'Original',
              state: 'available' as const,
              relativePath: 'game.iso',
              filename: 'game.iso',
              sizeBytes: '1024',
              sha256: helloSha256,
              profileVersion: 'rvz-import/v1',
              runtimeApplicability: [],
              contentGrant: {
                downloadUrl: '/delivery/content/original-format',
                expiresAt: '2026-10-01T12:00:00Z',
              },
            },
            {
              format: 'rvz' as const,
              label: 'RVZ',
              fileExtension: '.rvz',
              state: 'available' as const,
              relativePath: 'game.rvz',
              filename: 'game.rvz',
              sizeBytes: '512',
              sha256: 'b'.repeat(64),
              profileVersion: 'rvz-canonical/v1',
              runtimeApplicability: [
                'dolphin',
              ],
              contentGrant: {
                downloadUrl: '/delivery/content/rvz-old',
                expiresAt: '2026-10-01T12:00:00Z',
              },
            },
          ],
        },
      ],
    };
    issue.mockClear();
    issue.mockResolvedValue(next);
    return {
      issue,
      manifest: next,
    };
  }

  it('refreshes the chosen format and hands its delivered filename and fresh grant to the browser', async () => {
    const { issue, manifest } = await withDiscFormats();
    const downloads: {
      url: string;
      filename: string;
    }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloads.push({
        url: this.href,
        filename: this.download,
      });
    });
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    const user = userEvent.setup();
    await user.click(screen.getByText('Files'));
    const button = await screen.findByRole('button', {
      name: 'Download RVZ',
    });
    expect(screen.getByText('game.rvz')).toBeVisible();
    expect(screen.getByText('512 B')).toBeVisible();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            manifest.items[0].formats[0],
            {
              ...manifest.items[0].formats[1],
              contentGrant: {
                downloadUrl: '/delivery/content/rvz-fresh',
                expiresAt: '2026-10-01T13:00:00Z',
              },
            },
          ],
        },
      ],
    });
    await user.click(button);
    await waitFor(() =>
      expect(downloads).toEqual([
        {
          url: expect.stringContaining('/delivery/content/rvz-fresh'),
          filename: 'game.rvz',
        },
      ]),
    );
    expect(issue).toHaveBeenCalledTimes(2);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('keeps the RVZ choice on retry and never substitutes an original grant', async () => {
    const { issue, manifest } = await withDiscFormats();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    const user = userEvent.setup();
    await user.click(screen.getByText('Files'));
    const button = await screen.findByRole('button', {
      name: 'Download RVZ',
    });
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            manifest.items[0].formats[0],
            {
              ...manifest.items[0].formats[1],
              contentGrant: null,
            },
          ],
        },
      ],
    });
    await user.click(button);
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
    expect(click).not.toHaveBeenCalled();
    issue.mockResolvedValue(manifest);
    await user.click(
      screen.getByRole('button', {
        name: 'Retry',
      }),
    );
    await waitFor(() => expect(click).toHaveBeenCalledOnce());
    expect(issue).toHaveBeenCalledTimes(3);
  });

  it('defaults to the original format grant while keeping the original browse metadata', async () => {
    await withDiscFormats();
    const downloads: {
      url: string;
      filename: string;
    }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloads.push({
        url: this.href,
        filename: this.download,
      });
    });
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Download',
        exact: true,
      }),
    );
    await waitFor(() =>
      expect(downloads).toEqual([
        {
          url: expect.stringContaining('/delivery/content/original-format'),
          filename: 'game.iso',
        },
      ]),
    );
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([
    'chd',
    'rvz',
  ] as const)('keeps held Unsupported %s grant-free while Original downloads', async (encoding) => {
    const { issue, manifest } = await withDiscFormats();
    const unavailable = {
      format: encoding,
      label: encoding.toUpperCase(),
      fileExtension: `.${encoding}`,
      state: 'unsupported' as const,
      relativePath: `game.${encoding}`,
      profileVersion: 'unknown',
      runtimeApplicability: [],
      contentGrant: null,
    };
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            manifest.items[0].formats[0],
            unavailable,
          ],
        },
      ],
    });
    const downloads: {
      url: string;
      filename: string;
    }[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (
      this: HTMLAnchorElement,
    ) {
      downloads.push({
        url: this.href,
        filename: this.download,
      });
    });
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    const user = userEvent.setup();
    await user.click(screen.getByText('Files'));
    const unsupported = await screen.findByRole('button', {
      name: `${encoding.toUpperCase()} unsupported`,
    });
    expect(unsupported).toBeDisabled();
    await user.click(unsupported);
    expect(issue).toHaveBeenCalledTimes(1);
    expect(fetch).not.toHaveBeenCalled();
    expect(downloads).toEqual([]);
    expect(
      screen.getByRole('button', {
        name: 'Download Original',
      }),
    ).toBeEnabled();

    await user.click(
      screen.getByRole('button', {
        name: 'Download Original',
      }),
    );

    await waitFor(() =>
      expect(downloads).toEqual([
        {
          url: expect.stringContaining('/delivery/content/original-format'),
          filename: 'game.iso',
        },
      ]),
    );
    expect(issue).toHaveBeenCalledTimes(2);
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([
    'preparing',
    'unsupported',
  ] as const)('disables RVZ delivery when it is %s', async (state) => {
    const { issue, manifest } = await withDiscFormats();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            manifest.items[0].formats[0],
            {
              ...manifest.items[0].formats[1],
              state,
              contentGrant: null,
            },
          ],
        },
      ],
    });
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(screen.getByText('Files'));
    expect(
      await screen.findByRole('button', {
        name: `RVZ ${state}`,
      }),
    ).toBeDisabled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('labels each format with the label the server sends', async () => {
    const { issue, manifest } = await withDiscFormats();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats: [
            {
              ...manifest.items[0].formats[0],
              label: 'Plain image',
            },
            {
              ...manifest.items[0].formats[1],
              label: 'Dolphin image',
              state: 'preparing',
              contentGrant: null,
            },
          ],
        },
      ],
    });
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(screen.getByText('Files'));
    expect(
      await screen.findByRole('button', {
        name: 'Download Plain image',
      }),
    ).toBeEnabled();
    expect(
      screen.getByRole('button', {
        name: 'Dolphin image preparing',
      }),
    ).toBeDisabled();
  });

  it.each([
    'empty',
    'unsupported',
    'preparing',
  ] as const)('does not use a legacy original grant when formats is %s', async (state) => {
    const { issue, manifest } = await withDiscFormats();
    issue.mockResolvedValue({
      ...manifest,
      items: [
        {
          ...manifest.items[0],
          formats:
            state === 'empty'
              ? []
              : [
                  {
                    ...manifest.items[0].formats[0],
                    state,
                    contentGrant: null,
                  },
                  manifest.items[0].formats[1],
                ],
        },
      ],
    });
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    render(<ReleaseManifestPanel release={release} />, {
      withAuth: false,
    });
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Download',
        exact: true,
      }),
    );
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
    expect(click).not.toHaveBeenCalled();
  });
});

function generatedManifest(): ConsumerReleaseManifestDto {
  return {
    serverInstanceId: '11111111-1111-4111-8111-111111111111',
    releaseId: release.id,
    titleId: 'title-1',
    systemKey: 'psx',
    name: 'Synthetic CD',
    revision: null,
    isComplete: false,
    isDeliveryComplete: true,
    runtime: {
      contentType: 'unknown',
      launch: {
        type: 'file',
        relativePath: 'disc.cue',
      },
      packaging: 'direct_files',
      minimumInstallBytes: '10',
    },
    items: [
      {
        relativePath: 'disc.cue',
        role: 'rom',
        provenance: 'Derived',
        sizeBytes: '5',
        sha256: helloSha256,
        isAvailable: true,
        formats: [],
        contentGrant: {
          downloadUrl: '/delivery/derived-descriptor/old.signature',
          expiresAt: '2030-01-01T00:15:00Z',
        },
      },
      {
        relativePath: 'track.bin',
        role: 'rom',
        provenance: 'Original',
        sizeBytes: '5',
        sha256: 'b'.repeat(64),
        isAvailable: true,
        contentGrant: {
          downloadUrl: '/delivery/content/bin.signature',
          expiresAt: '2030-01-01T00:15:00Z',
        },
        formats: [
          {
            format: 'original',
            label: 'Original',
            state: 'available',
            relativePath: 'track.bin',
            filename: 'track.bin',
            sizeBytes: '5',
            sha256: 'b'.repeat(64),
            profileVersion: 'identity/v1',
            runtimeApplicability: [],
            contentGrant: {
              downloadUrl: '/delivery/content/bin.signature',
              expiresAt: '2030-01-01T00:15:00Z',
            },
          },
        ],
      },
    ],
  };
}
