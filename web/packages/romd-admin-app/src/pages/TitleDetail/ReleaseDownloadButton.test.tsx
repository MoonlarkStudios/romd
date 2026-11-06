import { type ArchiveFormatDto, createDownloadTicket } from '@romd/admin-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { ReleaseDownloadButton } from './ReleaseDownloadButton';

vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  createDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));
const files = [
  {
    id: 'one',
    name: 'disc1.bin',
    isOwned: true,
    romFileId: 'rom-one',
  },
  {
    id: 'two',
    name: 'disc2.bin',
    isOwned: true,
    romFileId: 'rom-two',
  },
];
const allFormats: ArchiveFormatDto[] = [
  {
    format: 'original',
    label: 'Original',
  },
  {
    format: 'rvz',
    label: 'RVZ',
  },
  {
    format: 'chd',
    label: 'CHD',
  },
];
const response = {
  data: {
    downloadUrl: '/api/download-tickets/ticket',
    expiresAt: '2026-09-25T00:00:00Z',
  },
  request: new Request('http://localhost'),
  response: new Response(),
};

describe('release downloads', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(createDownloadTicket).mockResolvedValue(response);
  });
  it('requests one ZIP for the canonical release, including partial releases', async () => {
    render(
      <ReleaseDownloadButton
        formats={allFormats}
        releaseId="canonical"
        name="Game"
        files={[
          ...files,
          {
            id: 'missing',
            name: 'disc3',
          },
        ]}
      />,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Download available files for Game',
      }),
    );
    expect(createDownloadTicket).toHaveBeenCalledExactlyOnceWith({
      body: {
        target: 'Release',
        id: 'canonical',
        format: 'original',
      },
    });
    expect(startBrowserDownload).toHaveBeenCalledExactlyOnceWith(response.data.downloadUrl);
  });
  it('disables releases without local files or canonical IDs', () => {
    render(
      <>
        <ReleaseDownloadButton
          formats={allFormats}
          releaseId="canonical"
          name="Missing"
          files={[
            {
              id: 'missing',
              name: 'missing',
            },
          ]}
        />
        <ReleaseDownloadButton
          formats={allFormats}
          name="Unknown"
          files={files}
        />
      </>,
    );
    expect(
      screen.getByRole('button', {
        name: 'Download available files for Missing',
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole('button', {
        name: 'Download ZIP for Unknown',
      }),
    ).toBeDisabled();
  });
  it('prevents duplicate requests and does not hand off failed preparations', async () => {
    let rejectRequest: (error: Error) => void = () => {};
    vi.mocked(createDownloadTicket).mockImplementationOnce(
      () =>
        new Promise((_, reject) => {
          rejectRequest = reject;
        }),
    );
    render(
      <ReleaseDownloadButton
        formats={allFormats}
        releaseId="canonical"
        name="Game"
        files={files}
      />,
    );
    const button = screen.getByRole('button', {
      name: 'Download ZIP for Game',
    });
    await userEvent.click(button);
    await userEvent.click(button);
    expect(createDownloadTicket).toHaveBeenCalledTimes(1);
    rejectRequest(new Error('offline'));
    await waitFor(() => expect(button).not.toHaveAttribute('data-loading'));
    expect(startBrowserDownload).not.toHaveBeenCalled();
  });

  it('lists only the formats the server offers for the release, with its labels', async () => {
    render(
      <ReleaseDownloadButton
        formats={[
          allFormats[0],
          {
            format: 'chd',
            label: 'Compressed disc',
          },
        ]}
        releaseId="canonical"
        name="Game"
        files={files}
      />,
    );
    const user = userEvent.setup();
    const selection = screen.getByRole('combobox', {
      name: 'ZIP format for Game',
    });
    expect(
      Array.from(selection.querySelectorAll('option')).map((option) => option.textContent),
    ).toEqual([
      'Original',
      'Compressed disc where available',
    ]);
    await user.selectOptions(selection, 'chd');
    await user.click(
      screen.getByRole('button', {
        name: 'Download ZIP for Game',
      }),
    );
    expect(createDownloadTicket).toHaveBeenCalledExactlyOnceWith({
      body: {
        target: 'Release',
        id: 'canonical',
        format: 'chd',
      },
    });
  });

  it('downloads an original-only release with one action and no format choice', async () => {
    render(
      <ReleaseDownloadButton
        formats={allFormats.slice(0, 1)}
        releaseId="canonical"
        name="Game"
        files={files}
      />,
    );
    expect(
      screen.queryByRole('combobox', {
        name: 'ZIP format for Game',
      }),
    ).not.toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Download ZIP for Game',
      }),
    );
    expect(createDownloadTicket).toHaveBeenCalledExactlyOnceWith({
      body: {
        target: 'Release',
        id: 'canonical',
        format: 'original',
      },
    });
  });

  it('falls back to original when a refreshed release stops offering the chosen format', async () => {
    const { rerender } = render(
      <ReleaseDownloadButton
        formats={allFormats}
        releaseId="canonical"
        name="Game"
        files={files}
      />,
    );
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'ZIP format for Game',
      }),
      'chd',
    );
    rerender(
      <ReleaseDownloadButton
        formats={allFormats.slice(0, 2)}
        releaseId="canonical"
        name="Game"
        files={files}
      />,
    );
    expect(
      screen.getByRole('combobox', {
        name: 'ZIP format for Game',
      }),
    ).toHaveValue('original');
    await user.click(
      screen.getByRole('button', {
        name: 'Download ZIP for Game',
      }),
    );
    expect(createDownloadTicket).toHaveBeenCalledExactlyOnceWith({
      body: {
        target: 'Release',
        id: 'canonical',
        format: 'original',
      },
    });
  });

  it('requests an RVZ-where-available archive for the chosen canonical release', async () => {
    render(
      <ReleaseDownloadButton
        formats={allFormats}
        releaseId="canonical"
        name="Game"
        files={files}
      />,
    );
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'ZIP format for Game',
      }),
      'rvz',
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Download ZIP for Game',
      }),
    );
    expect(createDownloadTicket).toHaveBeenCalledWith({
      body: {
        target: 'Release',
        id: 'canonical',
        format: 'rvz',
      },
    });
    expect(startBrowserDownload).toHaveBeenCalledWith(response.data.downloadUrl);
  });

  it('keeps CHD selected after a denied preparation and retries the canonical release', async () => {
    vi.mocked(createDownloadTicket)
      .mockResolvedValueOnce({
        error: {
          status: 403,
        },
      } as never)
      .mockResolvedValueOnce(response);
    render(
      <ReleaseDownloadButton
        formats={allFormats}
        releaseId="canonical"
        name="Game"
        files={files}
      />,
    );
    const user = userEvent.setup();
    const selection = screen.getByRole('combobox', {
      name: 'ZIP format for Game',
    });
    expect(selection).toHaveValue('original');
    await user.selectOptions(selection, 'chd');
    const button = screen.getByRole('button', {
      name: 'Download ZIP for Game',
    });
    await user.click(button);
    await waitFor(() => expect(button).not.toHaveAttribute('data-loading'));
    expect(startBrowserDownload).not.toHaveBeenCalled();
    expect(selection).toHaveValue('chd');
    await user.click(button);
    await waitFor(() =>
      expect(startBrowserDownload).toHaveBeenCalledExactlyOnceWith(response.data.downloadUrl),
    );
    for (const attempt of [
      1,
      2,
    ])
      expect(createDownloadTicket).toHaveBeenNthCalledWith(attempt, {
        body: {
          target: 'Release',
          id: 'canonical',
          format: 'chd',
        },
      });
    // One release ticket per attempt, never one per file.
    expect(createDownloadTicket).toHaveBeenCalledTimes(2);
  });
});
