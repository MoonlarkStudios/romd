import { MantineProvider } from '@mantine/core';
import type { TitleDetail } from '@romd/admin-api-client';
import { client } from '@romd/admin-api-client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/msw/server';
import { ArtworkPanel } from './ArtworkPanel';

vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { roles: ['Manager'] } }) }));
const prefix = 'http://localhost/api/artwork';
const title = { id: 'title1', systemKey: 'platform1', name: 'Test Game', enrichmentStatus: 'None' };
const selections = [{ role: 'Poster', mode: 'Pinned', revision: '1', pinnedAssetId: 'asset1', pendingJobId: null },
  { role: 'Hero', mode: 'Automatic', revision: '0', pinnedAssetId: null, pendingJobId: null },
  { role: 'Backdrop', mode: 'Automatic', revision: '0', pinnedAssetId: null, pendingJobId: null },
  { role: 'Logo', mode: 'Automatic', revision: '0', pinnedAssetId: null, pendingJobId: null }];
const candidate = { reference: 'protected-reference', providerAssetId: '7', role: 'Poster', width: 600, height: 900,
  style: 'alternate', attribution: 'Artist', sourcePageUrl: 'https://www.steamgriddb.com/grid/7' };
const createUrl = vi.fn(() => 'blob:artwork-preview');
const revokeUrl = vi.fn();
function renderPanel(value: TitleDetail = title) {
  const cache = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<MantineProvider env="test"><QueryClientProvider client={cache}><ArtworkPanel title={value} titleId="title1" /></QueryClientProvider></MantineProvider>);
}
async function discover() {
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Choose poster' }));
  await user.click(await screen.findByRole('tab', { name: 'SteamGridDB' }));
  await screen.findByRole('button', { name: 'Select artwork 7' });
  return user;
}
describe('Artwork curation', () => {
  beforeEach(() => {
    client.setConfig({ baseUrl: 'http://localhost' });
    vi.stubGlobal('URL', class extends URL { static createObjectURL = createUrl; static revokeObjectURL = revokeUrl; });
    createUrl.mockClear(); revokeUrl.mockClear();
    server.use(
      http.get('http://localhost/api/provider-matches/titles/title1', () => HttpResponse.json([{ providerId: 'steamgriddb', providerName: 'SteamGridDB', isAvailable: true, capabilities: ['artwork'], revision: '00000000-0000-0000-0000-000000000001', state: 'Confirmed', externalId: '42', game: { id: '42', name: 'Test Game', url: 'https://www.steamgriddb.com/game/42' }, metadataNeedsRefresh: false }])),
      http.get('http://localhost/api/enrichment/artwork/title1', () => HttpResponse.json({ outcomes: [], activeJobId: null })),
      http.get('http://localhost/api/platforms/platform1', () => HttpResponse.json({ id: 'platform1', name: 'Test platform' })),
      http.get(`${prefix}/providers`, () => HttpResponse.json([{ id: 'steamgriddb', name: 'SteamGridDB', isAvailable: true, supportsLanguageFilter: false, gameId: '42', gameName: 'Test Game', gameUrl: 'https://www.steamgriddb.com/game/42',
        roles: [{ role: 'Poster', dimensions: ['600x900'], styles: ['alternate'] }, { role: 'Hero', dimensions: ['1920x620'], styles: ['alternate'] }, { role: 'Logo', dimensions: [], styles: ['official'] }] }])),
      http.get(`${prefix}/titles/title1/selections`, () => HttpResponse.json(selections)),
      http.get(`${prefix}/titles/title1/saved`, () => HttpResponse.json([])),
      http.get(`${prefix}/titles/title1/games`, () => HttpResponse.json([{ id: '42', name: 'Test Game' }])),
      http.get(`${prefix}/titles/title1/candidates`, () => HttpResponse.json({ items: [candidate], nextCursor: null })),
      http.post(`${prefix}/titles/title1/preview`, () => new HttpResponse(new Uint8Array([1, 2, 3]), { headers: { 'Content-Type': 'image/png' } })),
    );
  });
  afterEach(() => { client.setConfig({ baseUrl: '' }); vi.unstubAllGlobals(); });
  it('renders linked provider capabilities without provider-specific filters or labels', async () => {
    const queries: URLSearchParams[] = [];
    server.use(http.get(`${prefix}/providers`, ({ request }) => {
      queries.push(new URL(request.url).searchParams);
      return HttpResponse.json([{ id: 'igdb', name: 'IGDB', isAvailable: true, gameId: '42', gameName: 'Test Game', gameUrl: 'https://www.igdb.com/games/test-game', roles: [{ role: 'Poster', dimensions: [], styles: [] }] }]);
    }));
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Choose poster' }));
    await user.click(await screen.findByRole('tab', { name: 'IGDB' }));
    await user.click(await screen.findByRole('button', { name: 'Select artwork 7' }));
    expect(screen.queryByRole('textbox', { name: 'Dimensions' })).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox', { name: 'Style' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View on IGDB' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'SteamGridDB' })).not.toBeInTheDocument();
    expect(queries.some(query => query.get('titleId') === 'title1' && query.get('role') === 'Poster')).toBe(true);
  });

  it('opens backdrop review with suitable candidates and previews without applying', async () => {
    const apply = vi.fn();
    server.use(
      http.get('http://localhost/api/enrichment/artwork/title1', () => HttpResponse.json({ outcomes: [{ role: 'Backdrop', status: 'NeedsReview', updatedAt: '2026-09-15T00:00:00Z' }], activeJobId: null })),
      http.get(`${prefix}/providers`, () => HttpResponse.json([{ id: 'igdb', name: 'IGDB', isAvailable: true, gameId: '42', roles: [{ role: 'Backdrop', dimensions: [], styles: [] }] }])),
      http.get(`${prefix}/titles/title1/candidates`, () => HttpResponse.json({ items: [
        { ...candidate, role: 'Backdrop', providerAssetId: 'scene', reference: 'scene-ref', width: 1920, height: 1080 },
        { ...candidate, role: 'Backdrop', providerAssetId: 'banner', reference: 'banner-ref', width: 1920, height: 620 },
      ], nextCursor: null })),
      http.post(`${prefix}/titles/title1/apply`, apply),
    );
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Review backdrop' }));
    await screen.findByRole('button', { name: 'Select artwork scene' });
    expect(screen.queryByRole('button', { name: 'Select artwork banner' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Select artwork scene' }));
    await user.click(screen.getByRole('button', { name: 'Preview backdrop' }));
    await screen.findByRole('dialog', { name: 'Compose backdrop' });
    await user.click(screen.getByRole('button', { name: 'Mobile preview' }));
    expect(screen.getByTestId('artwork-composition-preview')).toHaveAttribute('data-mobile', 'true');
    await user.click(screen.getByRole('button', { name: 'Cancel', exact: true }));
    expect(apply).not.toHaveBeenCalled();
  });

  it('previews a hero upload locally and cancellation never uploads it', async () => {
    const upload = vi.fn(() => HttpResponse.json({}));
    server.use(http.post('http://localhost/api/titles/title1/media', upload));
    const view = renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Choose banner' }));
    await user.click(screen.getByRole('tab', { name: 'Upload' }));
    const input = document.querySelector<HTMLInputElement>('input[type="file"]')!;
    await user.upload(input, new File(['image'], 'hero.png', { type: 'image/png' }));
    await screen.findByRole('dialog', { name: 'Compose banner' });
    expect(screen.queryByLabelText('Image type')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Cancel', exact: true }));
    expect(upload).not.toHaveBeenCalled();
    view.unmount();
    expect(revokeUrl).toHaveBeenCalled();
  });
  it('uploads a logo through the existing workflow and pins the retained snapshot', async () => {
    const upload = vi.fn(({ request }: { request: Request }) => {
      expect(new URL(request.url).searchParams.get('type')).toBe('logo');
      return HttpResponse.json({ id: 'logo1', type: 'Logo', sourceId: 'user', url: '/media/logo1' });
    });
    const pin = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({ mediaId: 'logo1', assetId: null, expectedRevision: '0', focalX: 50, focalY: 50 });
      return HttpResponse.json({ selectionRevision: '1' });
    });
    server.use(http.post('http://localhost/api/titles/title1/media', upload),
      http.post(`${prefix}/titles/title1/roles/Logo/pin`, pin));
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Choose logo' }));
    await user.click(screen.getByRole('tab', { name: 'Upload' }));
    await user.upload(document.querySelector<HTMLInputElement>('input[type="file"]')!,
      new File(['image'], 'logo.png', { type: 'image/png' }));
    await screen.findByRole('dialog', { name: 'Compose logo' });
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(upload).toHaveBeenCalledOnce());
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
  });
  it.each([{ role: 'Hero', label: 'banner' }, { role: 'Backdrop', label: 'backdrop' }])('assigns an existing cover to $label independently of its image type', async ({ role, label }) => {
    const pin = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({ mediaId: 'cover1', assetId: null, expectedRevision: '0', focalX: 0, focalY: 100 });
      return HttpResponse.json({ selectionRevision: '1' });
    });
    server.use(http.post(`${prefix}/titles/title1/roles/${role}/pin`, pin));
    renderPanel({ ...title, media: [{ id: 'cover1', type: 'Cover', sourceId: 'igdb', url: '/media/cover1', isPrimary: true }] });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: `Choose ${label}` }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Actions for Cover cover1' }));
    await user.click(screen.getByRole('menuitem', { name: `Use as ${label}` }));
    await screen.findByRole('dialog', { name: `Compose ${label}` });
    expect(pin).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Mobile preview' }));
    expect(screen.getByRole('button', { name: 'Mobile preview' })).toHaveAttribute('aria-pressed', 'true');
    act(() => screen.getByRole('slider', { name: 'Horizontal focal point' }).focus());
    await user.keyboard('{Home}');
    act(() => screen.getByRole('slider', { name: 'Vertical focal point' }).focus());
    await user.keyboard('{End}');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.queryByText('Primary')).not.toBeInTheDocument();
  });

  it('keeps the chooser open on a stale selection conflict', async () => {
    server.use(http.post(`${prefix}/titles/title1/roles/Hero/pin`, () => HttpResponse.json({ status: 409 }, { status: 409 })));
    renderPanel({ ...title, media: [{ id: 'cover1', type: 'Cover', sourceId: 'user', url: '/media/cover1' }] });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Choose banner' }));
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Actions for Cover cover1' }));
    await user.click(screen.getByRole('menuitem', { name: 'Use as banner' }));
    await user.click(await screen.findByRole('button', { name: 'Save changes' }));
    expect(await within(screen.getByRole('dialog', { name: 'Compose banner' })).findByText('The selection changed. Cancel this preview and choose again to review the latest artwork.')).toBeInTheDocument();
  });

  it('reuses retained artwork while providers are unavailable', async () => {
    const pin = vi.fn(() => HttpResponse.json({ selectionRevision: '2' }));
    server.use(http.get(`${prefix}/providers`, () => HttpResponse.json([])),
      http.get(`${prefix}/titles/title1/saved`, () => HttpResponse.json([{ id: 'saved1', role: 'Poster', sourceId: 'steamgriddb', mediaIds: [], artwork: { role: 'Poster', assetId: 'saved1', url: '/artwork/saved1' } }])),
      http.post(`${prefix}/titles/title1/roles/Poster/pin`, pin));
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Actions for Cover saved1' }));
    await user.click(screen.getByRole('menuitem', { name: 'Use as poster' }));
    await user.click(await screen.findByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
  });
  it('cancels an image preview without changing the assignment', async () => {
    const pin = vi.fn(() => HttpResponse.json({ selectionRevision: '1' }));
    server.use(http.post(`${prefix}/titles/title1/roles/Hero/pin`, pin));
    renderPanel({ ...title, media: [{ id: 'cover1', type: 'Cover', sourceId: 'user', url: '/media/cover1' }] });
    const user = userEvent.setup();
    expect(screen.queryByRole('button', { name: 'Use as banner' })).not.toBeInTheDocument();
    await user.click(await screen.findByRole('button', { name: 'Actions for Cover cover1' }));
    await user.click(screen.getByRole('menuitem', { name: 'Use as banner' }));
    await user.click(await screen.findByRole('radio', { name: 'Current', exact: true }));
    await user.click(screen.getByRole('button', { name: 'Cancel', exact: true }));
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Compose banner' })).not.toBeInTheDocument());
    expect(pin).not.toHaveBeenCalled();
  });
  it('previews without applying and revokes temporary image URLs on close', async () => {
    const apply = vi.fn(); server.use(http.post(`${prefix}/titles/title1/apply`, apply));
    const view = renderPanel();
    const user = await discover();
    await user.click(screen.getByRole('button', { name: 'Select artwork 7' }));
    await user.click(screen.getByRole('button', { name: 'Preview poster' }));
    await waitFor(() => expect(createUrl).toHaveBeenCalled());
    expect(await screen.findByRole('button', { name: 'Save changes' })).toBeInTheDocument();
    expect(apply).not.toHaveBeenCalled();
    view.unmount();
    expect(revokeUrl).toHaveBeenCalledWith('blob:artwork-preview');
  });
  it('works without randomUUID and reuses the selected request UUID on retry', async () => {
    vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) });
    expect(crypto.randomUUID).toBeUndefined();
    const requests: unknown[] = [];
    server.use(http.post(`${prefix}/titles/title1/apply`, async ({ request }) => {
      requests.push(await request.json()); return HttpResponse.json({ detail: 'Temporary failure' }, { status: 503 });
    }));
    renderPanel(); const user = await discover();
    await user.click(screen.getByRole('button', { name: 'Select artwork 7' }));
    await user.click(screen.getByRole('button', { name: 'Preview poster' }));
    await user.click(await screen.findByRole('button', { name: 'Save changes' }));
    await screen.findByText('Temporary failure');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(requests).toHaveLength(2));
    expect(requests[0]).toEqual(requests[1]);
    expect(requests[0]).toEqual({ candidateReference: candidate.reference, focalX: 50, focalY: 50, expectedRevision: '1',
      requestId: expect.stringMatching(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/) });
  });
  it('returns a pin to automatic without contacting the provider', async () => {
    const automatic = vi.fn(() => HttpResponse.json({ selectionRevision: '2' }));
    server.use(http.post(`${prefix}/titles/title1/roles/Poster/automatic`, automatic));
    renderPanel();
    const button = await screen.findByRole('button', { name: 'Return poster to automatic' });
    await waitFor(() => expect(button).toBeEnabled());
    await userEvent.setup().click(button);
    await waitFor(() => expect(automatic).toHaveBeenCalledOnce());
  });
  it('changing capability filters clears the previous candidate choice', async () => {
    const filters: string[] = [];
    server.use(http.get(`${prefix}/titles/title1/candidates`, ({ request }) => {
      filters.push(new URL(request.url).searchParams.get('style') ?? '');
      return HttpResponse.json({ items: [candidate], nextCursor: null });
    }));
    renderPanel(); const user = await discover();
    await user.click(screen.getByRole('button', { name: 'Select artwork 7' }));
    expect(screen.getByRole('button', { name: 'Preview poster' })).toBeInTheDocument();
    await user.click(screen.getByRole('textbox', { name: 'Style' }));
    await user.click(screen.getByRole('option', { name: 'alternate' }));
    await waitFor(() => expect(filters).toContain('alternate'));
    expect(screen.queryByRole('button', { name: 'Preview poster' })).not.toBeInTheDocument();
  });
  it('browses and applies a logo with its own revision after comparison on both backgrounds', async () => {
    const logo = { ...candidate, reference: 'logo-reference', role: 'Logo', width: 1024, height: 400, style: 'official', sourcePageUrl: 'https://www.steamgriddb.com/logo/7' };
    const queries: URLSearchParams[] = [];
    const apply = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({ candidateReference: 'logo-reference', expectedRevision: '0', focalX: 50, focalY: 50, requestId: expect.any(String) });
      return HttpResponse.json({ detail: 'Temporary failure' }, { status: 503 });
    });
    server.use(http.get(`${prefix}/titles/title1/candidates`, ({ request }) => {
      queries.push(new URL(request.url).searchParams);
      return HttpResponse.json({ items: [logo], nextCursor: null });
    }), http.post(`${prefix}/titles/title1/apply`, apply));
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Choose logo' }));
    await user.click(await screen.findByRole('tab', { name: 'SteamGridDB' }));
    await user.click(await screen.findByRole('button', { name: 'Select artwork 7' }));
    expect(screen.queryByRole('textbox', { name: 'Dimensions' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Preview logo' }));
    const composer = within(await screen.findByRole('dialog', { name: 'Compose logo' }));
    expect(composer.getByText('Dark background')).toBeInTheDocument();
    expect(composer.getByText('Light background')).toBeInTheDocument();
    expect(composer.queryByRole('button', { name: 'Mobile preview' })).not.toBeInTheDocument();
    expect(composer.queryByRole('slider')).not.toBeInTheDocument();
    for (const image of composer.getAllByRole('img', { name: 'Test Game logo' })) expect(image).toHaveStyle({ '--image-object-fit': 'contain' });
    expect(queries.every(query => query.get('role') === 'Logo')).toBe(true);
    expect(apply).not.toHaveBeenCalled();
    await user.click(composer.getByRole('button', { name: 'Save changes' }));
    await screen.findByText('Temporary failure');
    expect(apply).toHaveBeenCalledOnce();
  });

  it('keeps retained logos selectable after their gallery originals are removed', async () => {
    const pin = vi.fn(async ({ request }: { request: Request }) => {
      expect(await request.json()).toEqual({ assetId: 'saved-logo', mediaId: null, expectedRevision: '0', focalX: 50, focalY: 50 });
      return HttpResponse.json({ selectionRevision: '1' });
    });
    server.use(http.get(`${prefix}/providers`, () => HttpResponse.json([])),
      http.get(`${prefix}/titles/title1/saved`, () => HttpResponse.json([{ id: 'saved-logo', role: 'Logo', sourceId: 'steamgriddb', mediaIds: [], artwork: { role: 'Logo', url: '/artwork/logo' } }])),
      http.post(`${prefix}/titles/title1/roles/Logo/pin`, pin));
    renderPanel();
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Actions for Logo saved-logo' }));
    expect(await screen.findByRole('menuitem', { name: 'Use as logo' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Use as banner' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('menuitem', { name: 'Use as logo' }));
    await user.click(await screen.findByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(pin).toHaveBeenCalledOnce());
  });

});
