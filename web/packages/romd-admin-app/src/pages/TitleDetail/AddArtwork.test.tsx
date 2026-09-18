import { MantineProvider } from '@mantine/core';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { AddArtwork } from './AddArtwork';

const prefix = 'http://localhost/api/artwork';
const provider = { id: 'igdb', name: 'IGDB', isAvailable: true, supportsLanguageFilter: false, roles: [{ role: 'Poster', dimensions: [], styles: [] }, { role: 'Hero', dimensions: [], styles: [] }], mediaTypes: ['Cover', 'Background', 'Screenshot'], gameId: '42', gameName: 'Test game' };
const revoke = vi.fn();
function show() {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<MantineProvider env="test"><QueryClientProvider client={cache}><AddArtwork opened onClose={vi.fn()} titleId="title1" titleName="Test game" /></QueryClientProvider></MantineProvider>);
}

describe('Add artwork', () => {
  beforeEach(() => {
    client.setConfig({ baseUrl: 'http://localhost' });
    vi.stubGlobal('URL', class extends URL { static createObjectURL = () => 'blob:test'; static revokeObjectURL = revoke; });
    revoke.mockClear();
    server.use(
      http.get(`${prefix}/providers`, () => HttpResponse.json([provider])),
      http.get('http://localhost/api/provider-matches/titles/title1', () => HttpResponse.json([{ providerId: 'igdb', providerName: 'IGDB', isAvailable: true, state: 'Confirmed' }])),
      http.get(`${prefix}/titles/title1/candidates`, ({ request }) => {
        expect(new URL(request.url).searchParams.get('mediaType')).toBe('Screenshot');
        return HttpResponse.json({ items: [1, 2, 3].map(id => ({ reference: `ref${id}`, providerAssetId: `${id}`, role: 'Hero', mediaType: 'Screenshot', width: 640, height: 480, sourcePageUrl: 'https://www.igdb.com/games/test', isSaved: id === 3 })), nextCursor: null });
      }),
      http.post(`${prefix}/titles/title1/preview`, () => new HttpResponse(new Blob(['image'], { type: 'image/png' }))),
    );
  });
  afterEach(() => vi.unstubAllGlobals());

  it('collects screenshots without assigning artwork and retries only the failed image', async () => {
    const calls: string[] = [];
    let fail = true;
    server.use(http.post(`${prefix}/titles/title1/gallery`, async ({ request }) => {
      const body = await request.json() as { candidateReference: string };
      calls.push(body.candidateReference);
      if (body.candidateReference === 'ref2' && fail) return HttpResponse.json({ detail: 'Provider unavailable' }, { status: 503 });
      return HttpResponse.json({ id: body.candidateReference, type: 'Screenshot', sourceId: 'gallery:igdb', isPrimary: false, url: '/media/image' });
    }));
    show();
    const user = userEvent.setup();
    await screen.findByRole('button', { name: 'Select image 1' });
    expect(screen.getByRole('button', { name: 'Select image 3' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Select visible' }));
    await user.click(screen.getByRole('button', { name: 'Add 2 images' }));
    await screen.findByText('Provider unavailable');
    expect(calls).toEqual(['ref1', 'ref2']);
    expect(screen.getByRole('button', { name: 'Select image 1' })).toBeDisabled();
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Retry selected images' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Select image 2' })).toBeDisabled());
    expect(calls).toEqual(['ref1', 'ref2', 'ref2']);
  });

  it('uploads a batch with individual image types and keeps successful uploads on retry', async () => {
    const calls: string[] = [];
    let fail = true;
    let finishRetry = () => {};
    const retryResponse = new Promise<void>(resolve => { finishRetry = resolve; });
    server.use(http.post('http://localhost/api/titles/title1/media', async ({ request }) => {
      const type = new URL(request.url).searchParams.get('type');
      const file = { name: type === 'logo' ? 'logo.png' : 'screen.png' };
      calls.push(`${file.name}:${type}`);
      if (file.name === 'logo.png' && fail) return HttpResponse.json({}, { status: 500 });
      if (file.name === 'logo.png') await retryResponse;
      return HttpResponse.json({ id: file.name, type: new URL(request.url).searchParams.get('type'), sourceId: 'user', url: '/media/new', isPrimary: false });
    }));
    const view = show();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: 'Upload images' }));
    await user.upload(document.querySelector<HTMLInputElement>('input[type="file"]')!, [new File(['a'], 'screen.png', { type: 'image/png' }), new File(['b'], 'logo.png', { type: 'image/png' })]);
    await user.selectOptions(screen.getByLabelText('Type for logo.png'), 'Logo');
    await user.click(screen.getByRole('button', { name: 'Add 2 images' }));
    await screen.findByText('Upload failed. Retry this image.');
    fail = false;
    await user.click(screen.getByRole('button', { name: 'Retry remaining images' }));
    expect(screen.getByRole('progressbar', { name: 'Batch progress' })).toHaveAttribute('aria-valuenow', '0');
    expect(screen.getByText('0 of 1 processed this attempt')).toBeInTheDocument();
    expect(screen.getByRole('progressbar', { name: 'Transfer progress for logo.png' })).toBeInTheDocument();
    finishRetry();
    await screen.findByText('2 of 2 added');
    expect(calls).toEqual(['screen.png:screenshot', 'logo.png:logo', 'logo.png:logo']);
    view.unmount();
    expect(revoke).toHaveBeenCalled();
  });

  it('keeps unmatched providers visible and offers matching inside the artwork dialog', async () => {
    server.use(
      http.get(`${prefix}/providers`, ({ request }) => HttpResponse.json(new URL(request.url).searchParams.has('titleId') ? [] : [provider])),
      http.get('http://localhost/api/provider-matches/titles/title1', () => HttpResponse.json([{ providerId: 'igdb', providerName: 'IGDB', isAvailable: true, state: 'NeedsMatch', capabilities: ['artwork'], revision: 'revision' }])),
    );
    show();
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Find match' }));
    expect(await screen.findByRole('dialog', { name: 'Match IGDB' })).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Game name' })).toHaveValue('Test game');
  });

  it('hides disabled providers but retains enabled providers needing setup', async () => {
    server.use(http.get(`${prefix}/providers`, () => HttpResponse.json([provider, { ...provider, id: 'disabled', name: 'Disabled provider', isAvailable: false }, { ...provider, id: 'setup', name: 'Setup provider', isAvailable: false }])),
      http.get('http://localhost/api/provider-matches/titles/title1', () => HttpResponse.json([{ providerId: 'igdb' }, { providerId: 'setup', isAvailable: false }])));
    show();
    expect(await screen.findByRole('button', { name: /Setup provider/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Disabled provider/ })).not.toBeInTheDocument();
  });
  it('collects SteamGridDB logos with capability filters and a reviewable preview', async () => {
    const logoProvider = { ...provider, id: 'steamgriddb', name: 'SteamGridDB', mediaTypes: ['Logo'], roles: [{ role: 'Logo', dimensions: [], styles: ['official', 'white'] }] };
    const queries: URLSearchParams[] = [];
    const imported = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({ candidateReference: 'logo-reference' });
      return HttpResponse.json({ id: 'logo1', type: 'Logo', sourceId: 'gallery:steamgriddb', url: '/media/logo1' });
    });
    const apply = vi.fn(() => HttpResponse.json({}));
    server.use(
      http.get(`${prefix}/providers`, () => HttpResponse.json([logoProvider])),
      http.get('http://localhost/api/provider-matches/titles/title1', () => HttpResponse.json([{ providerId: 'steamgriddb', state: 'Confirmed' }])),
      http.get(`${prefix}/titles/title1/candidates`, ({ request }) => {
        queries.push(new URL(request.url).searchParams);
        return HttpResponse.json({ items: [{ reference: 'logo-reference', providerAssetId: '77', role: 'Logo', width: 1024, height: 400, style: 'official', attribution: 'Logo artist', sourcePageUrl: 'https://www.steamgriddb.com/logo/77' }], nextCursor: null });
      }),
      http.post(`${prefix}/titles/title1/gallery`, imported),
      http.post(`${prefix}/titles/title1/apply`, apply),
    );
    show();
    const user = userEvent.setup();
    await screen.findByRole('button', { name: 'Select image 77' });
    expect(screen.getByLabelText('Image type')).toHaveValue('Logo');
    expect(screen.queryByLabelText('Dimensions')).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Style'), 'white');
    await waitFor(() => expect(queries.some(query => query.get('style') === 'white')).toBe(true));
    expect(queries.every(query => query.get('role') === 'Logo' && query.get('mediaType') === 'Logo' && !query.has('dimension'))).toBe(true);
    await user.click(screen.getByRole('button', { name: 'Preview image 77' }));
    const preview = within(await screen.findByRole('dialog', { name: 'Preview logo' }));
    expect(preview.getByText('1024 × 400 · official · By Logo artist')).toBeInTheDocument();
    expect(preview.getByRole('link', { name: 'View original on SteamGridDB' })).toHaveAttribute('href', 'https://www.steamgriddb.com/logo/77');
    await user.click(preview.getByRole('button', { name: 'Select image' }));
    await user.click(screen.getByRole('button', { name: 'Add 1 image' }));
    await waitFor(() => expect(imported).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.getByRole('button', { name: 'Select image 77' })).toBeDisabled());
    expect(apply).not.toHaveBeenCalled();
  });

  it('does not browse a linked provider while it requires configuration', async () => {
    const browse = vi.fn(() => HttpResponse.json({ items: [], nextCursor: null }));
    server.use(http.get(`${prefix}/providers`, () => HttpResponse.json([{ ...provider, isAvailable: false }])), http.get(`${prefix}/titles/title1/candidates`, browse));
    show();
    expect(await screen.findByText(/This provider needs configuration/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Image type')).not.toBeInTheDocument();
    expect(browse).not.toHaveBeenCalled();
  });

});
