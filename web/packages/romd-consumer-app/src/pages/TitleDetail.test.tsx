import type { ConsumerTitleDetailDto } from '@romd/consumer-api-client';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useBrowserPlaybackCapability } from '../hooks/useBrowserPlaybackCapability';
import { useIssueReleaseManifest, useTitleDetail } from '../hooks/useConsumerLibrary';
import { render } from '../test/utils/render';
import { TitleDetail } from './TitleDetail';

// The presentation's nested provider otherwise resets Mantine's JSDOM test environment.
vi.mock('../../../romd-consumer-ui/src/ConsumerPresentation', async () => {
  const { MantineProvider } = await import('@mantine/core');
  return {
    ConsumerPresentation: ({ children }: { children: import('react').ReactNode }) => (
      <MantineProvider env="test">{children}</MantineProvider>
    ),
  };
});

vi.mock('../hooks/useConsumerLibrary', () => ({
  useIssueReleaseManifest: vi.fn(),
  useTitleDetail: vi.fn(),
}));

vi.mock('../hooks/useBrowserPlaybackCapability', () => ({
  useBrowserPlaybackCapability: vi.fn(),
}));

describe('TitleDetail', () => {
  beforeEach(() => {
    vi.mocked(useBrowserPlaybackCapability).mockReturnValue({
      data: {
        status: 'available',
        capability: {
          playerOrigin: 'http://player.localhost:5175',
          emulatorJs: {
            version: '4.2.3',
            source: 'cdn',
            dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
          },
          cores: ['fceumm', 'snes9x', 'gambatte', 'mgba'],
        },
      },
      isLoading: false,
    } as ReturnType<typeof useBrowserPlaybackCapability>);
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync: vi.fn(),
      isPending: false,
      isError: false,
    } as ReturnType<typeof useIssueReleaseManifest>);
  });

  it.each([
    { href: '/', label: 'Home', section: 'home' },
    { href: '/library?q=Metroid&sortBy=rating', label: 'Browse', section: 'browse' },
    { href: '/collections/favorites', label: 'Favorites', section: 'home' },
  ])('uses the explicit return destination after changing tabs: $label', titleReturn => {
    vi.mocked(useTitleDetail).mockReturnValue({ data: createTitle(), isLoading: false, isError: false } as ReturnType<typeof useTitleDetail>);
    render(<TitleDetail />, { withAuth: false, routerOptions: { initialEntries: [{ pathname: '/titles/title-1', state: { titleReturn } }] } });
    expect(screen.getByRole('link', { name: titleReturn.label })).toHaveAttribute('href', titleReturn.href);
    fireEvent.click(screen.getByRole('tab', { name: 'Releases' }));
    expect(screen.getByRole('link', { name: titleReturn.label })).toHaveAttribute('href', titleReturn.href);
  });

  it('renders a playable default Release with Play and Download actions', () => {
    vi.mocked(useTitleDetail).mockReturnValue({
      data: createTitle(),
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);

    render(<TitleDetail />, {
      withAuth: false,
      routerOptions: {
        initialEntries: ['/titles/title-1'],
      },
    });

    expect(screen.getByRole('heading', { level: 1, name: /super metroid/i })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Versions' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /play now/i })).toHaveAttribute(
      'href',
      '/titles/title-1/releases/release-1/play',
    );
    expect(screen.getAllByRole('link', { name: /download/i }).length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByRole('button', { name: /add to shelf/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/your activity/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /version:/i })).not.toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Details' })).toHaveAttribute('aria-selected', 'true');
    fireEvent.click(screen.getByRole('link', { name: 'Download' }));
    expect(screen.getByRole('tab', { name: /Releases/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('link', { name: 'Play', exact: true })).toBeInTheDocument();
    expect(screen.getAllByText(/USA/).length).toBeGreaterThanOrEqual(1);
  });

  it('keeps unsupported platforms Download-only on detail actions', () => {
    vi.mocked(useTitleDetail).mockReturnValue({
      data: {
        ...createTitle(),
        system: { key: 'psx', name: 'PlayStation', compactLabel: 'PlayStation' },
      },
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);

    render(<TitleDetail />, {
      withAuth: false,
      routerOptions: {
        initialEntries: ['/titles/title-1'],
      },
    });

    expect(screen.queryByRole('link', { name: /play now/i })).not.toBeInTheDocument();
    expect(screen.getAllByText(/not mapped to a browser emulator core/i).length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByRole('link', { name: /download/i }).length).toBeGreaterThanOrEqual(1);
  });

  it('hides Play when the dedicated player is unavailable', () => {
    vi.mocked(useBrowserPlaybackCapability).mockReturnValue({
      data: {
        status: 'unavailable',
        reason: 'Browser playback is not configured for this portal origin.',
      },
      isLoading: false,
    } as ReturnType<typeof useBrowserPlaybackCapability>);
    vi.mocked(useTitleDetail).mockReturnValue({
      data: createTitle(),
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);

    render(<TitleDetail />, {
      withAuth: false,
      routerOptions: {
        initialEntries: ['/titles/title-1'],
      },
    });

    expect(screen.queryByRole('link', { name: /play now/i })).not.toBeInTheDocument();
    expect(screen.getAllByText(/not configured for this portal origin/i).length).toBeGreaterThanOrEqual(1);
  });
  it('uses a cinematic backdrop and falls back to poster composition after an image error', () => {
    const backdrop = { role: 'Backdrop', assetId: 'scene', contentVersion: 'v1', url: '/artwork/scene/backdrop/v1',
      width: 1920, height: 1080, originalWidth: 1920, originalHeight: 1080, fit: 'Cover', fallbackReason: 'None', variants: [] };
    vi.mocked(useTitleDetail).mockReturnValue({ data: { ...createTitle(), artwork: [backdrop] }, isLoading: false, isError: false } as ReturnType<typeof useTitleDetail>);
    const { container } = render(<TitleDetail />, { withAuth: false });
    const overview = screen.getByRole('region', { name: /Super Metroid overview/i });
    expect(overview).toHaveAttribute('data-has-backdrop', 'true');
    const image = container.querySelector('img[src*="/artwork/scene/"]')!;
    expect(image).toHaveAttribute('fetchpriority', 'high');
    fireEvent.error(image);
    expect(overview).not.toHaveAttribute('data-has-backdrop');
    expect(screen.getByRole('heading', { level: 1, name: 'Super Metroid' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Play Now' })).toBeInTheDocument();
  });

  it('expands the default release and updates Play when another version is selected', async () => {
    const title = createTitle();
    title.releases.push({ ...title.releases[0], id: 'release-2', regions: ['Europe'] });
    vi.mocked(useTitleDetail).mockReturnValue({ data: title, isLoading: false, isError: false } as ReturnType<typeof useTitleDetail>);
    render(<TitleDetail />, { withAuth: false, routerOptions: { initialEntries: ['/titles/title-1?tab=releases'] } });
    expect(screen.getByRole('link', { name: 'Play', exact: true })).toHaveAttribute('href', '/titles/title-1/releases/release-1/play');
    fireEvent.click(screen.getByRole('button', { name: /Europe/ }));
    expect(await screen.findByRole('link', { name: 'Play', exact: true })).toHaveAttribute('href', '/titles/title-1/releases/release-2/play');
    expect(screen.getByRole('link', { name: 'Play Now' })).toHaveAttribute('href', '/titles/title-1/releases/release-2/play');
    expect(useIssueReleaseManifest().mutateAsync).not.toHaveBeenCalled();
  });

  it('distinguishes same-region revisions and languages in the version menu', async () => {
    const title = createTitle();
    title.releases.push(
      { ...title.releases[0], id: 'release-2', revision: 'Rev 2' },
      { ...title.releases[0], id: 'release-3', revision: 'Rev 2', languages: ['French', 'Japanese'] },
    );
    vi.mocked(useTitleDetail).mockReturnValue({ data: title, isLoading: false, isError: false } as ReturnType<typeof useTitleDetail>);
    render(<TitleDetail />, { withAuth: false, routerOptions: { initialEntries: ['/titles/title-1'] } });
    fireEvent.click(screen.getByRole('button', { name: /Version: USA · Rev 1 English/ }));
    await waitFor(() => expect(screen.getByRole('menuitem', { name: /USA · Rev 1 Default English/ })).toBeVisible());
    expect(screen.getByRole('menuitem', { name: /USA · Rev 2 English/ })).toBeVisible();
    fireEvent.click(screen.getByRole('menuitem', { name: /USA · Rev 2 French, Japanese/ }));
    expect(await screen.findByRole('button', { name: /Version: USA · Rev 2 French, Japanese/ })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Play Now' })).toHaveAttribute('href', '/titles/title-1/releases/release-3/play');
    expect(useIssueReleaseManifest().mutateAsync).not.toHaveBeenCalled();
  });

  it('honors release deep links and safely defaults an unavailable media tab', () => {
    const title = createTitle();
    title.media = [];
    title.releases.push({ ...title.releases[0], id: 'release-2', regions: ['Europe'] });
    vi.mocked(useTitleDetail).mockReturnValue({ data: title, isLoading: false, isError: false } as ReturnType<typeof useTitleDetail>);
    render(<TitleDetail />, { withAuth: false, routerOptions: { initialEntries: ['/titles/title-1?release=release-2&tab=media'] } });
    expect(screen.getByRole('link', { name: 'Play Now' })).toHaveAttribute('href', '/titles/title-1/releases/release-2/play');
    expect(screen.getByRole('tab', { name: 'Details' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByRole('tab', { name: /Media/ })).not.toBeInTheDocument();
  });

  it('does not offer play before the browser capability check completes', () => {
    vi.mocked(useTitleDetail).mockReturnValue({ data: createTitle(), isLoading: false, isError: false } as ReturnType<typeof useTitleDetail>);
    vi.mocked(useBrowserPlaybackCapability).mockReturnValue({ isLoading: true } as ReturnType<typeof useBrowserPlaybackCapability>);
    render(<TitleDetail />, { withAuth: false });
    expect(screen.getByRole('button', { name: 'Checking play availability' })).toBeDisabled();
    expect(screen.queryByRole('link', { name: 'Play Now' })).not.toBeInTheDocument();
  });

});

function createTitle(): ConsumerTitleDetailDto {
  return {
    id: 'title-1',
    system: { key: 'snes', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' },
    name: 'Super Metroid',
    description: 'Explore planet Zebes.',
    publisher: 'Nintendo',
    developer: 'Nintendo R&D1',
    genre: 'Action',
    releaseDate: '1994-03-19',
    players: '1',
    rating: '9.4',
    media: [
      {
        id: 'media-1',
        type: 'Cover',
        url: '/media/cover',
        isPrimary: true,
      },
      {
        id: 'media-2',
        type: 'Screenshot',
        url: '/media/screenshot',
        isPrimary: false,
      },
    ],
    defaultReleaseId: 'release-1',
    releases: [
      {
        id: 'release-1',
        name: 'Super Metroid',
        revision: 'Rev 1',
        regions: ['USA'],
        languages: ['English'],
        sizeBytes: '3145728',
        isComplete: true,
      },
    ],
  };
}
