import {
  type ConsumerDiscSetStatus,
  issueConsumerDiscSetDownloadTicket,
  listConsumerDiscSets,
} from '@romd/consumer-api-client';
import { startBrowserDownload } from '@romd/delivery';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { DiscSetDownloads } from './DiscSetDownloads';

vi.mock('@romd/consumer-api-client', () => ({
  listConsumerDiscSets: vi.fn(),
  issueConsumerDiscSetDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));

const set: ConsumerDiscSetStatus = {
  contractVersion: 1,
  serverInstanceId: 'server',
  discSetId: 'set',
  revision: 'revision',
  titleId: 'title',
  systemKey: 'psx',
  name: 'Game (USA)',
  observationId: 'observed',
  expectedDiscCount: null,
  missingExpectedOrdinals: [],
  archiveFormats: [
    {
      format: 'original',
      label: 'original ZIP',
    },
    {
      format: 'chd',
      label: 'CHD where available',
    },
  ],
  discs: [
    {
      releaseId: 'disc1',
      ordinal: 1,
      name: 'Game disc 1',
      readiness: 'ready',
    },
    {
      releaseId: 'disc2',
      ordinal: 2,
      name: 'Game disc 2',
      readiness: 'ready',
    },
  ],
};

function listing(value: ConsumerDiscSetStatus) {
  vi.mocked(listConsumerDiscSets).mockResolvedValue({
    data: {
      items: [
        value,
      ],
      hasNextPage: false,
      nextCursor: null,
    },
    error: undefined,
    response: new Response(),
  });
}

describe('DiscSetDownloads', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    listing(set);
    vi.mocked(issueConsumerDiscSetDownloadTicket).mockResolvedValue({
      data: {
        downloadUrl: '/delivery/disc-set/ticket',
        expiresAt: '2030-01-01T00:00:00Z',
      },
      error: undefined,
      response: new Response(),
    });
  });

  it('downloads all known discs in published order without claiming an unknown total is complete', async () => {
    render(<DiscSetDownloads titleId="title" />);
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download CHD where available',
      }),
    );
    expect(issueConsumerDiscSetDownloadTicket).toHaveBeenCalledWith(
      expect.objectContaining({
        path: {
          discSetId: 'set',
        },
        query: {
          format: 'chd',
        },
        body: {
          contractVersion: 1,
          revision: 'revision',
          observationId: 'observed',
          selectedReleaseIds: [
            'disc1',
            'disc2',
          ],
          allowPartial: false,
        },
      }),
    );
    expect(screen.getByText('Total disc count is unknown.')).toBeInTheDocument();
    expect(startBrowserDownload).toHaveBeenCalledWith('/delivery/disc-set/ticket');
    expect(await screen.findByRole('status')).toHaveTextContent('Download handed to your browser.');
  });

  it('requires consent for missing discs and sends only selected membership', async () => {
    listing({
      ...set,
      expectedDiscCount: 3,
      missingExpectedOrdinals: [
        3,
      ],
      discs: [
        set.discs[0],
        {
          ...set.discs[1],
          readiness: 'missing-media',
        },
      ],
    });
    render(<DiscSetDownloads titleId="title" />);
    const user = userEvent.setup();
    const download = await screen.findByRole('button', {
      name: 'Download original ZIP',
    });
    expect(download).toBeDisabled();
    expect(
      screen.getByRole('checkbox', {
        name: /Disc 2:/,
      }),
    ).toBeDisabled();
    await user.click(
      screen.getByRole('checkbox', {
        name: /Download only the selected discs/,
      }),
    );
    await user.click(download);
    expect(issueConsumerDiscSetDownloadTicket).toHaveBeenCalledWith(
      expect.objectContaining({
        query: {
          format: 'original',
        },
        body: expect.objectContaining({
          selectedReleaseIds: [
            'disc1',
          ],
          allowPartial: true,
        }),
      }),
    );
  });

  it('requires new partial consent after membership changes and preserves published order on reselect', async () => {
    render(<DiscSetDownloads titleId="title" />);
    const user = userEvent.setup();
    const first = await screen.findByRole('checkbox', {
      name: /Disc 1:/,
    });
    await user.click(first);
    expect(
      screen.getByRole('button', {
        name: 'Download original ZIP',
      }),
    ).toBeDisabled();
    await user.click(first);
    await user.click(
      screen.getByRole('button', {
        name: 'Download original ZIP',
      }),
    );
    expect(issueConsumerDiscSetDownloadTicket).toHaveBeenCalledWith(
      expect.objectContaining({
        body: expect.objectContaining({
          selectedReleaseIds: [
            'disc1',
            'disc2',
          ],
          allowPartial: false,
        }),
      }),
    );
  });

  it('refreshes a rejected selection without handing off a download or silently retrying it', async () => {
    vi.mocked(issueConsumerDiscSetDownloadTicket).mockResolvedValue({
      error: {
        status: 409,
      },
      data: undefined,
      response: new Response(null, {
        status: 409,
      }),
    });
    render(<DiscSetDownloads titleId="title" />);
    const user = userEvent.setup();
    await user.click(
      await screen.findByRole('button', {
        name: 'Download original ZIP',
      }),
    );
    expect(await screen.findByText(/Review the refreshed disc set/)).toBeInTheDocument();
    await waitFor(() => expect(listConsumerDiscSets).toHaveBeenCalledTimes(2));
    expect(startBrowserDownload).not.toHaveBeenCalled();
    expect(issueConsumerDiscSetDownloadTicket).toHaveBeenCalledTimes(1);
  });
});
