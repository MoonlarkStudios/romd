import type { ResolvedArtworkDto } from '@romd/consumer-api-client';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { loadRecentArtwork } from '../../services/artworkCache';
import { ArtworkImage } from './ArtworkImage';

const authState = vi.hoisted(() => ({
  subject: 'account-a' as string | null,
  loaded: new Set<() => void>(),
  unloaded: new Set<() => void>(),
}));

vi.mock('../../auth/userManager', () => ({
  userManager: {
    getUser: vi.fn(async () => authState.subject ? ({ expired: false, profile: { iss: 'https://romd.test', sub: authState.subject } }) : null),
    events: {
      addUserLoaded: (listener: () => void) => authState.loaded.add(listener),
      removeUserLoaded: (listener: () => void) => authState.loaded.delete(listener),
      addUserUnloaded: (listener: () => void) => authState.unloaded.add(listener),
      removeUserUnloaded: (listener: () => void) => authState.unloaded.delete(listener),
    },
  },
}));
vi.mock('../../services/artworkCache', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../services/artworkCache')>(),
  loadRecentArtwork: vi.fn(async () => undefined),
}));

const artwork: ResolvedArtworkDto = {
  role: 'Poster', assetId: null, contentVersion: 'media-box', url: '/media/box', width: null, height: null,
  originalWidth: null, originalHeight: null, fit: 'Contain', fallbackReason: 'LegacyCover', variants: [],
};

beforeEach(() => {
  authState.subject = 'account-a';
  authState.loaded.clear();
  authState.unloaded.clear();
  vi.mocked(loadRecentArtwork).mockClear().mockResolvedValue(undefined);
  vi.stubGlobal('IntersectionObserver', undefined);
});

describe('ArtworkImage', () => {
  it('labels missing or failed artwork and removes the label when a new source is available', () => {
    const label = <span>Game name</span>;
    const { rerender } = render(<ArtworkImage missingArtworkLabel={label} />);
    expect(screen.getByText('Game name')).toBeVisible();
    rerender(<ArtworkImage artwork={[artwork]} alt="Poster" missingArtworkLabel={label} />);
    expect(screen.queryByText('Game name')).not.toBeInTheDocument();
    fireEvent.error(screen.getByAltText('Poster'));
    expect(screen.getByText('Game name')).toBeVisible();
    rerender(<ArtworkImage artwork={[{ ...artwork, url: '/media/replacement' }]} alt="Poster" missingArtworkLabel={label} />);
    expect(screen.queryByText('Game name')).not.toBeInTheDocument();
  });

  it('replaces text with a transparent logo after loading and restores it on failure', () => {
    render(<ArtworkImage artwork={[{ ...artwork, role: 'Logo' }]} role="Logo" alt="Game logo" fallback={<span>Game name</span>} />);
    const image = screen.getByAltText('Game logo');
    expect(screen.getByText('Game name')).toBeVisible();
    expect(image).not.toBeVisible();
    fireEvent.load(image);
    expect(image).toBeVisible();
    expect(image).toHaveStyle({ objectFit: 'contain', objectPosition: 'var(--romd-logo-position, left center)' });
    expect(screen.queryByText('Game name')).not.toBeInTheDocument();
    fireEvent.error(image);
    expect(screen.getByText('Game name')).toBeVisible();
  });

  it('delivers responsive backdrops without originals, external URLs, or duplicate blob fetching', async () => {
    const onError = vi.fn();
    const variants = [
      { name: 'small', width: 960, height: 540, url: '/artwork/scene/small/v1', contentVersion: 'v1' },
      { name: 'backdrop', width: 1920, height: 1080, url: '/artwork/scene/backdrop/v1', contentVersion: 'v1' },
      { name: 'original', width: 4000, height: 2250, url: '/artwork/scene/original/v1', contentVersion: 'v1' },
      { name: 'external', width: 2880, height: 1620, url: 'https://provider.test/image.jpg', contentVersion: 'v1' },
    ];
    render(<ArtworkImage artwork={[{ ...artwork, role: 'Backdrop', fit: 'Cover', variants }]} role="Backdrop" priority alt="Scene" onError={onError} />);
    const image = screen.getByRole('img', { name: 'Scene' });
    expect(image).toHaveAttribute('sizes', '100vw');
    expect(image).toHaveAttribute('loading', 'eager');
    expect(image.getAttribute('srcset')).toContain('960w');
    expect(image.getAttribute('srcset')).toContain('1920w');
    expect(image.getAttribute('srcset')).not.toMatch(/original|provider/);
    await act(async () => {});
    expect(loadRecentArtwork).not.toHaveBeenCalled();
    fireEvent.error(image);
    expect(onError).toHaveBeenCalledOnce();
  });

  it('keeps the text fallback when logo artwork is unavailable', () => {
    render(<ArtworkImage artwork={[artwork]} role="Logo" fallback={<span>Game name</span>} />);
    expect(screen.getByText('Game name')).toBeVisible();
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('renders contained box art in a 2:3 poster without stretching', async () => {
    const { container } = render(<ArtworkImage artwork={[artwork]} alt="Box art" />);
    const image = screen.getByRole('img', { name: 'Box art' });
    expect(image).toHaveStyle({ objectFit: 'contain' });
    expect(container.firstChild).toHaveStyle({ aspectRatio: '2 / 3' });
    await waitFor(() => expect(loadRecentArtwork).toHaveBeenCalled());
  });
  it('uses only the resolved hero in the hero reference layout', async () => {
    const hero = { ...artwork, role: 'Hero', fit: 'Cover', url: '/artwork/hero/wide/v1', contentVersion: 'v1' };
    const { container } = render(<ArtworkImage artwork={[artwork, hero]} role="Hero" alt="Hero" />);
    expect(screen.getByRole('img')).toHaveAttribute('src', `${window.location.origin}/artwork/hero/wide/v1`);
    expect(screen.getByRole('img')).toHaveStyle({ objectFit: 'cover' });
    expect(container.firstChild).toHaveStyle({ aspectRatio: '96 / 31' });
    await waitFor(() => expect(loadRecentArtwork).toHaveBeenCalled());
  });
  it('shows cached artwork after a network image failure and releases its blob URL', async () => {
    const create = vi.fn(() => 'blob:cached-artwork');
    const revoke = vi.fn();
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = create;
      static revokeObjectURL = revoke;
    });
    vi.mocked(loadRecentArtwork).mockResolvedValue(new Blob(['cached'], { type: 'image/webp' }));
    const { unmount } = render(<ArtworkImage artwork={[artwork]} alt="Cached box art" />);
    fireEvent.error(screen.getByRole('img'));
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:cached-artwork'));
    unmount();
    expect(revoke).toHaveBeenCalledWith('blob:cached-artwork');
  });
  it('does not replace ordinary online rendering when caching is unavailable', async () => {
    render(<ArtworkImage artwork={[artwork]} alt="Box art" />);
    await waitFor(() => expect(loadRecentArtwork).toHaveBeenCalled());
    expect(screen.getByRole('img')).toHaveAttribute('src', `${window.location.origin}/media/box`);
  });
  it('switches cache namespace on account changes and revokes the previous account blob', async () => {
    const create = vi.fn().mockReturnValueOnce('blob:account-a').mockReturnValueOnce('blob:account-b');
    const revoke = vi.fn();
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = create;
      static revokeObjectURL = revoke;
    });
    vi.mocked(loadRecentArtwork).mockResolvedValue(new Blob(['cached'], { type: 'image/webp' }));
    render(<ArtworkImage artwork={[artwork]} alt="Selected artwork" />);
    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:account-a'));

    await act(async () => {
      authState.subject = 'account-b';
      for (const listener of authState.loaded) listener();
    });

    await waitFor(() => expect(screen.getByRole('img')).toHaveAttribute('src', 'blob:account-b'));
    const keys = vi.mocked(loadRecentArtwork).mock.calls.map((call) => call[1]);
    expect(keys[0]).toContain('account-a');
    expect(keys.at(-1)).toContain('account-b');
    expect(revoke).toHaveBeenCalledWith('blob:account-a');
  });

  it('discards a late cache read after sign-out rather than showing the previous account blob', async () => {
    let finishRead: (blob: Blob) => void = () => undefined;
    const create = vi.fn(() => 'blob:old-account');
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = create;
      static revokeObjectURL = vi.fn();
    });
    vi.mocked(loadRecentArtwork).mockImplementationOnce(() => new Promise<Blob>((resolve) => { finishRead = resolve; }));
    render(<ArtworkImage artwork={[artwork]} alt="Selected artwork" />);
    await waitFor(() => expect(loadRecentArtwork).toHaveBeenCalled());

    await act(async () => {
      authState.subject = null;
      for (const listener of authState.unloaded) listener();
    });
    await act(async () => { finishRead(new Blob(['old'], { type: 'image/webp' })); });

    expect(create).not.toHaveBeenCalled();
    expect(screen.getByRole('img')).not.toHaveAttribute('src', 'blob:old-account');
    expect(vi.mocked(loadRecentArtwork).mock.calls[0][2].aborted).toBe(true);
  });

  it('discards a pending cache response when the artwork component unmounts', async () => {
    let finishRead: (blob: Blob) => void = () => undefined;
    const create = vi.fn();
    vi.stubGlobal('URL', class extends URL {
      static createObjectURL = create;
      static revokeObjectURL = vi.fn();
    });
    vi.mocked(loadRecentArtwork).mockImplementationOnce(() => new Promise<Blob>((resolve) => { finishRead = resolve; }));
    const { unmount } = render(<ArtworkImage artwork={[artwork]} alt="Selected artwork" />);
    await waitFor(() => expect(loadRecentArtwork).toHaveBeenCalled());

    unmount();
    await act(async () => { finishRead(new Blob(['cached'], { type: 'image/webp' })); });

    expect(create).not.toHaveBeenCalled();
    expect(vi.mocked(loadRecentArtwork).mock.calls[0][2].aborted).toBe(true);
  });

  it('does not substitute coverUrl or remote provider content for an unresolved hero', () => {
    render(<ArtworkImage artwork={[artwork]} role="Hero" fallback={<span>No hero</span>} />);
    expect(screen.queryByRole('img')).toBeNull();
    expect(screen.getByText('No hero')).toBeInTheDocument();
    expect(loadRecentArtwork).not.toHaveBeenCalled();
  });
});
