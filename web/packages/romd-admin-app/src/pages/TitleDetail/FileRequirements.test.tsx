import { createDownloadTicket, getRom } from '@romd/admin-api-client';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { render } from '../../test/utils/render';
import { FileRequirements } from './FileRequirements';

vi.mock('@romd/admin-api-client', async (original) => ({
  ...(await original<typeof import('@romd/admin-api-client')>()),
  getRom: vi.fn(),
  createDownloadTicket: vi.fn(),
}));
vi.mock('@romd/delivery', () => ({
  startBrowserDownload: vi.fn(),
}));
beforeEach(() => vi.resetAllMocks());

it('opens actual per-file formats using the owned ROM ID without mixed ZIP fallback', async () => {
  vi.mocked(getRom).mockResolvedValue({
    data: {
      id: 'rom',
      originalFilename: 'disc.iso',
      status: 'cataloged',
      size: '1024',
      sha1: 'a'.repeat(40),
      formats: [
        {
          format: 'original',
          label: 'Original',
          state: 'available',
          filename: 'disc.iso',
          sizeBytes: '1024',
          sha256: 'a'.repeat(64),
          profileVersion: 'rvz-import/v1',
          runtimeApplicability: [],
        },
        {
          format: 'rvz',
          label: 'RVZ',
          fileExtension: '.rvz',
          state: 'unsupported',
          profileVersion: 'unknown',
          runtimeApplicability: [],
        },
      ],
    },
  } as never);
  vi.mocked(createDownloadTicket).mockResolvedValue({
    data: {
      downloadUrl: '/api/download-tickets/original',
    },
  } as never);
  render(
    <FileRequirements
      files={[
        {
          id: 'catalog-file',
          name: 'disc.iso',
          isOwned: true,
          romFileId: 'rom',
        },
      ]}
    />,
  );
  expect(getRom).not.toHaveBeenCalled();
  const user = userEvent.setup();
  await user.click(
    screen.getByRole('button', {
      name: 'Download for disc.iso',
    }),
  );
  const dialog = await screen.findByRole('dialog', {
    name: 'Download formats',
  });
  expect(
    await within(dialog).findByRole('button', {
      name: 'RVZ unsupported',
    }),
  ).toBeDisabled();
  await user.click(
    within(dialog).getByRole('button', {
      name: 'Download Original',
    }),
  );
  await waitFor(() =>
    expect(createDownloadTicket).toHaveBeenCalledWith({
      body: {
        target: 'Rom',
        id: 'rom',
        format: 'original',
      },
    }),
  );
});

it('keeps missing and nodump files unavailable without fabricating ROM details', () => {
  render(
    <FileRequirements
      files={[
        {
          id: 'missing',
          name: 'missing.iso',
        },
        {
          id: 'nodump',
          name: 'nodump.iso',
          status: 'nodump',
        },
      ]}
    />,
  );
  expect(
    screen.getByRole('button', {
      name: 'Download for missing.iso',
    }),
  ).toBeDisabled();
  expect(
    screen.getByRole('button', {
      name: 'Download for nodump.iso',
    }),
  ).toBeDisabled();
  expect(getRom).not.toHaveBeenCalled();
});
