import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { writtenTitleFixture } from '../../test/msw/fixtures/enrichment';
import { server } from '../../test/msw/server';
import { BatchArtworkUpload } from './BatchArtworkUpload';

const releases = [
  {
    value: 'cr-usa',
    label: 'Test Game (USA)',
    region: 'USA',
  },
  {
    value: 'cr-japan',
    label: 'Test Game (Japan)',
    region: 'Japan',
  },
];

function show() {
  const cache = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  });
  render(
    <MantineProvider env="test">
      <QueryClientProvider client={cache}>
        <BatchArtworkUpload
          titleId="title1"
          releases={releases}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Mantine's FileInput renders a button; the hidden input beside it receives the files. */
function fileInput() {
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (!input) throw new Error('Missing file input');
  return input;
}

const file = (name: string, type: string) =>
  new File(
    [
      'x',
    ],
    name,
    {
      type,
    },
  );

describe('Batch upload', () => {
  beforeEach(() => {
    client.setConfig({
      baseUrl: 'http://localhost',
    });
    vi.stubGlobal(
      'URL',
      class extends URL {
        static createObjectURL = () => 'blob:upload';
        static revokeObjectURL = vi.fn();
      },
    );
  });
  afterEach(() => {
    client.setConfig({
      baseUrl: '',
    });
    vi.unstubAllGlobals();
  });

  it('places each file on its own release and type, with shared values as a shortcut, and files PDFs as manuals', async () => {
    const uploads: Record<string, string | null>[] = [];
    let revision = 1;
    server.use(
      http.get('http://localhost/api/titles/title1', () =>
        HttpResponse.json(
          writtenTitleFixture({
            id: 'title1',
            revision: 'rev-1',
          }),
        ),
      ),
      http.post('http://localhost/api/titles/title1/media', ({ request }) => {
        const query = new URL(request.url).searchParams;
        uploads.push({
          ifMatch: request.headers.get('If-Match'),
          type: query.get('type'),
          releaseId: query.get('releaseId'),
          region: query.get('region'),
        });
        revision += 1;
        return HttpResponse.json(
          {
            id: `media${revision}`,
            type: query.get('type'),
            sourceId: 'user',
            url: '/media/new',
            isPrimary: false,
            contentType: 'image/png',
          },
          {
            status: 201,
            headers: {
              ETag: `"rev-${revision}"`,
            },
          },
        );
      }),
    );
    show();
    const user = userEvent.setup();
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'Type for new images',
      }),
      'BoxFront',
    );
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'Release for new files',
      }),
      'cr-japan',
    );
    await user.upload(fileInput(), [
      file('front.png', 'image/png'),
      file('back.png', 'image/png'),
      file('manual.pdf', 'application/pdf'),
    ]);
    expect(
      screen.getByRole('combobox', {
        name: 'Type for manual.pdf',
      }),
    ).toHaveValue('Manual');
    expect(
      screen.getByRole('combobox', {
        name: 'Type for manual.pdf',
      }),
    ).toBeDisabled();
    expect(
      screen.getByRole('combobox', {
        name: 'Release for front.png',
      }),
    ).toHaveValue('cr-japan');
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'Type for back.png',
      }),
      'BoxBack',
    );
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'Release for back.png',
      }),
      'cr-usa',
    );
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'Release for manual.pdf',
      }),
      '',
    );

    await user.click(
      screen.getByRole('button', {
        name: 'Add 3 files',
      }),
    );
    await waitFor(() => expect(uploads).toHaveLength(3));
    expect(uploads).toEqual([
      {
        ifMatch: '"rev-1"',
        type: 'boxfront',
        releaseId: 'cr-japan',
        region: 'Japan',
      },
      {
        ifMatch: '"rev-2"',
        type: 'boxback',
        releaseId: 'cr-usa',
        region: 'USA',
      },
      {
        ifMatch: '"rev-3"',
        type: 'manual',
        releaseId: null,
        region: null,
      },
    ]);
    expect(await screen.findByText('3 of 3 added')).toBeInTheDocument();
  });

  it('applies the shared type and release to every waiting file', async () => {
    show();
    const user = userEvent.setup();
    await user.upload(fileInput(), [
      file('one.png', 'image/png'),
      file('two.png', 'image/png'),
    ]);
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'Type for new images',
      }),
      'DiscLabel',
    );
    await user.selectOptions(
      screen.getByRole('combobox', {
        name: 'Release for new files',
      }),
      'cr-usa',
    );
    await user.click(
      screen.getByRole('button', {
        name: 'Apply these values to every file',
      }),
    );
    for (const name of [
      'one.png',
      'two.png',
    ]) {
      expect(
        screen.getByRole('combobox', {
          name: `Type for ${name}`,
        }),
      ).toHaveValue('DiscLabel');
      expect(
        screen.getByRole('combobox', {
          name: `Release for ${name}`,
        }),
      ).toHaveValue('cr-usa');
    }
  });

  it('skips files that are neither supported images nor PDFs', async () => {
    show();
    const user = userEvent.setup({
      applyAccept: false,
    });
    await user.upload(fileInput(), [
      file('clip.gif', 'image/gif'),
      file('front.png', 'image/png'),
    ]);
    expect(
      screen.getByText(
        'Some files were skipped. Choose JPEG, PNG, or WebP images up to 32 MB, or PDF manuals up to 128 MB.',
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('combobox', {
        name: 'Type for clip.gif',
      }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('combobox', {
        name: 'Type for front.png',
      }),
    ).toBeInTheDocument();
  });
});
