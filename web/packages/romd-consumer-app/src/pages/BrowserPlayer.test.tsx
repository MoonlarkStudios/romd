import { MantineProvider } from '@mantine/core';
import type { ConsumerReleaseManifestDto, ConsumerTitleDetailDto } from '@romd/consumer-api-client';
import { act, fireEvent, render as renderBase, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, Route, RouterProvider, Routes, useBlocker, useLocation } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useIssueReleaseManifest, useTitleDetail } from '../hooks/useConsumerLibrary';
import { render } from '../test/utils/render';
import { BrowserPlayer } from './BrowserPlayer';

const bridgeHarness = vi.hoisted(() => ({
  create: vi.fn(),
  shutdown: vi.fn<() => Promise<void>>(),
  dispose: vi.fn(),
  control: vi.fn(),
}));
const activityHarness = vi.hoisted(() => ({ put: vi.fn() }));

vi.mock('../hooks/useConsumerLibrary', () => ({
  useIssueReleaseManifest: vi.fn(),
  useTitleDetail: vi.fn(),
}));

vi.mock('../hooks/usePlayActivity', () => ({
  putPlaySessionSnapshot: activityHarness.put,
}));

vi.mock('../services/playerBridge', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../services/playerBridge')>();
  return {
    ...actual,
    createPlayerBridge: bridgeHarness.create,
  };
});

vi.mock('react-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router')>();
  return {
    ...actual,
    useBlocker: vi.fn(() => ({
      state: 'unblocked',
      location: undefined,
      proceed: undefined,
      reset: undefined,
    })),
  };
});

const helloSha256 = '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824';

describe('BrowserPlayer', () => {
  beforeEach(() => {
    vi.mocked(useBlocker).mockReturnValue({ state: 'unblocked', location: undefined, proceed: undefined, reset: undefined });
    bridgeHarness.create.mockReset();
    bridgeHarness.shutdown.mockReset();
    bridgeHarness.dispose.mockReset();
    bridgeHarness.control.mockReset();
    bridgeHarness.shutdown.mockResolvedValue();
    activityHarness.put.mockReset();
    activityHarness.put.mockResolvedValue(undefined);
    bridgeHarness.create.mockReturnValue({
      ready: Promise.resolve({
        type: 'hello',
        protocol: 1,
        emulatorJs: { version: '4.2.3', source: 'cdn' },
        cores: ['snes9x'],
      }),
      shutdown: bridgeHarness.shutdown,
      dispose: bridgeHarness.dispose,
      control: bridgeHarness.control,
    });
    vi.stubGlobal('fetch', vi.fn());
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:verified');
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('auto-starts a playable Release through manifest issuance and verified bytes on load', async () => {
    const mutateAsync = vi.fn().mockResolvedValue(createManifest());
    vi.mocked(useTitleDetail).mockReturnValue({
      data: createTitle(),
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync,
      isPending: false,
    } as ReturnType<typeof useIssueReleaseManifest>);
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(
        jsonResponse({
          schemaVersion: 1,
          protocol: 1,
          emulatorJs: {
            version: '4.2.3',
            source: 'cdn',
            dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
          },
          cores: ['snes9x'],
          allowedParents: [window.location.origin],
        }),
      )
      .mockResolvedValueOnce(new Response(new TextEncoder().encode('hello'), {
        status: 200,
        headers: {
          'Content-Length': '5',
          'Content-Type': 'application/octet-stream',
        },
      }));

    renderPlayer();

    await waitFor(() => {
      expect(mutateAsync).toHaveBeenCalledWith('release-1');
    });
    expect(await screen.findByText(/loading your player/i)).toBeInTheDocument();
    const frame = screen.getByTitle(/browser player/i);

    expect(frame).toBeInTheDocument();
    expect(frame).toHaveAttribute('allow', 'autoplay; gamepad; fullscreen; screen-wake-lock');
    expect(frame).toHaveAttribute('sandbox', 'allow-scripts allow-same-origin allow-pointer-lock allow-downloads');
    expect(frame).toHaveAttribute('referrerpolicy', 'no-referrer');
    expect(frame).toHaveAttribute('src', 'http://player.localhost:5175/');
    expect(frame).not.toHaveAttribute('allowfullscreen');
    expect(screen.getByTestId('player-stage')).toContainElement(frame);
    expect(screen.getByRole('heading', { name: 'Super Metroid' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Back to game details' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Quit' })).not.toBeInTheDocument();
    expect(screen.queryByText(/manual & map/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/^notes$/i)).not.toBeInTheDocument();
  });

  it('keeps unsupported platforms Download-only without requesting a manifest', async () => {
    const mutateAsync = vi.fn();
    vi.mocked(useTitleDetail).mockReturnValue({
      data: {
        ...createTitle(),
        system: { key: 'psx', name: 'PlayStation', compactLabel: 'PlayStation' },
      },
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync,
      isPending: false,
    } as ReturnType<typeof useIssueReleaseManifest>);

    renderPlayer();

    expect(await screen.findByText(/this platform is not mapped/i)).toBeInTheDocument();
    expect(mutateAsync).not.toHaveBeenCalled();
  });

  it('shares an in-flight shutdown so rapid exits cannot remove the iframe early', async () => {
    let resolveShutdown = () => undefined;
    bridgeHarness.shutdown.mockReturnValue(
      new Promise<void>((resolve) => {
        resolveShutdown = resolve;
      }),
    );
    vi.mocked(useTitleDetail).mockReturnValue({
      data: createTitle(),
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue(createManifest()),
      isPending: false,
    } as ReturnType<typeof useIssueReleaseManifest>);
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(
        jsonResponse({
          schemaVersion: 1,
          protocol: 1,
          emulatorJs: {
            version: '4.2.3',
            source: 'cdn',
            dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/',
          },
          cores: ['snes9x'],
          allowedParents: [window.location.origin],
        }),
      )
      .mockResolvedValueOnce(
        new Response(new TextEncoder().encode('hello'), {
          status: 200,
          headers: { 'Content-Length': '5', 'Content-Type': 'application/octet-stream' },
        }),
      );

    renderPlayer();
    expect(await screen.findByTitle(/browser player/i)).toBeInTheDocument();
    const back = screen.getByRole('button', { name: /back to game/i });

    fireEvent.click(back);
    fireEvent.click(back);

    expect(bridgeHarness.shutdown).toHaveBeenCalledTimes(1);
    expect(screen.getByTitle(/browser player/i)).toBeInTheDocument();

    resolveShutdown();
    await waitFor(() => {
      expect(screen.queryByTitle(/browser player/i)).not.toBeInTheDocument();
    });
  });

  it.each([false, true])('restores the prior paused state when cancelling Quit (paused: %s)', async (paused) => {
    preparePlayable();
    renderPlayer();
    await screen.findByTitle(/browser player/i);
    const event = bridgeHarness.create.mock.calls[0][3];
    act(() => { event({ event: 'game-started' }); event({ event: 'controls-ready' }); });
    if (paused) act(() => event({ event: 'paused' }));
    bridgeHarness.control.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'Quit' }));
    expect(await screen.findByRole('dialog', { name: 'Leave this game?' })).toBeInTheDocument();
    expect(bridgeHarness.shutdown).not.toHaveBeenCalled();
    if (paused) expect(bridgeHarness.control).not.toHaveBeenCalled();
    else expect(bridgeHarness.control).toHaveBeenLastCalledWith('pause');
    fireEvent.click(screen.getByRole('button', { name: 'Keep playing' }));
    if (paused) expect(bridgeHarness.control).not.toHaveBeenCalled();
    else expect(bridgeHarness.control).toHaveBeenLastCalledWith('resume');
    expect(bridgeHarness.shutdown).not.toHaveBeenCalled();
  });

  it('protects Back navigation and waits for shutdown after confirmation', async () => {
    const actual = await vi.importActual<typeof import('react-router')>('react-router');
    vi.mocked(useBlocker).mockImplementation(actual.useBlocker);
    preparePlayable();
    const router = createMemoryRouter([
      { path: '/library', element: <div>Library destination</div> },
      { path: '/titles/:titleId/releases/:releaseId/play', element: <BrowserPlayer /> },
    ], { initialEntries: ['/library', '/titles/title-1/releases/release-1/play'] });
    renderBase(<MantineProvider><RouterProvider router={router} /></MantineProvider>);
    await screen.findByTitle(/browser player/i);
    const event = bridgeHarness.create.mock.calls[0][3];
    act(() => { event({ event: 'game-started' }); event({ event: 'controls-ready' }); });
    await act(() => router.navigate(-1));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Keep playing' }));
    expect(router.state.location.pathname).toContain('/play');
    expect(bridgeHarness.shutdown).not.toHaveBeenCalled();
    await act(() => router.navigate(-1));
    let finish = () => {};
    bridgeHarness.shutdown.mockReturnValue(new Promise<void>((resolve) => { finish = resolve; }));
    fireEvent.click(screen.getByRole('button', { name: 'Leave game' }));
    expect(bridgeHarness.shutdown).toHaveBeenCalledTimes(1);
    expect(screen.getByTitle(/browser player/i)).toBeInTheDocument();
    await act(async () => finish());
    expect(await screen.findByText('Library destination')).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/library');
  });

  it('keeps the Home return context after confirming Quit', async () => {
    preparePlayable();
    const state = { titleReturn: { href: '/', label: 'Home', section: 'home', scroll: { y: 400, rails: {} } } };
    renderPlayer(state);
    await screen.findByTitle(/browser player/i);
    const event = bridgeHarness.create.mock.calls[0][3];
    act(() => event({ event: 'game-started' }));
    fireEvent.click(screen.getByRole('button', { name: 'Quit' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Leave game' }));
    expect(await screen.findByTestId('title-destination')).toHaveTextContent(JSON.stringify(state));
  });

  it('tracks one retry-safe session from the actual game-started event through exit', async () => {
    vi.mocked(useTitleDetail).mockReturnValue({
      data: createTitle(),
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue(createManifest()),
      isPending: false,
    } as ReturnType<typeof useIssueReleaseManifest>);
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(jsonResponse({
        schemaVersion: 1,
        protocol: 1,
        emulatorJs: { version: '4.2.3', source: 'cdn', dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/' },
        cores: ['snes9x'],
        allowedParents: [window.location.origin],
      }))
      .mockResolvedValueOnce(new Response(new TextEncoder().encode('hello'), {
        status: 200,
        headers: { 'Content-Length': '5', 'Content-Type': 'application/octet-stream' },
      }));

    renderPlayer();
    await screen.findByTitle(/browser player/i);
    const onEvent = bridgeHarness.create.mock.calls[0]?.[3] as
      | ((event: { event: 'ready' | 'game-started' | 'exit' | 'controls-ready' | 'paused' | 'resumed' }) => void)
      | undefined;
    expect(onEvent).toBeTypeOf('function');

    const originalFrame = screen.getByTitle(/browser player/i);
    act(() => onEvent?.({ event: 'ready' }));
    await waitFor(() => expect(screen.queryByText(/Loading your player/)).not.toBeInTheDocument());
    expect(screen.getByTitle(/browser player/i)).toBeInTheDocument();
    expect(screen.getByText('Ready')).toBeInTheDocument();
    expect(activityHarness.put).not.toHaveBeenCalled();

    const clock = vi.spyOn(performance, 'now').mockReturnValue(1000);
    act(() => onEvent?.({ event: 'game-started' }));
    await waitFor(() => expect(screen.getByText('In session')).toBeInTheDocument());
    expect(screen.getByTitle(/browser player/i)).toBe(originalFrame);
    expect(bridgeHarness.create).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('player-launch')).toHaveAttribute('data-playing', 'true');
    expect(screen.queryByRole('button', { name: 'Back to game details' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Quit' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Player help' }));
    expect(await screen.findByText(/Open Control Settings/)).toBeInTheDocument();
    act(() => onEvent?.({ event: 'controls-ready' }));
    fireEvent.click(screen.getByRole('button', { name: 'Pause' }));
    expect(bridgeHarness.control).toHaveBeenLastCalledWith('pause');
    // The portal waits for authoritative state, rather than optimistically
    // changing its button while the runtime may still be running.
    expect(screen.queryByRole('button', { name: 'Resume' })).not.toBeInTheDocument();
    clock.mockReturnValue(6000);
    act(() => onEvent?.({ event: 'paused' }));
    expect(screen.getByRole('button', { name: 'Resume' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Controls' }));
    expect(bridgeHarness.control).toHaveBeenLastCalledWith('open-controls');
    fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
    expect(bridgeHarness.control).toHaveBeenLastCalledWith('resume');
    clock.mockReturnValue(16000);
    act(() => onEvent?.({ event: 'resumed' }));
    expect(screen.getByRole('button', { name: 'Pause' })).toBeInTheDocument();
    clock.mockReturnValue(18000);
    act(() => onEvent?.({ event: 'game-started' }));
    act(() => onEvent?.({ event: 'exit' }));

    await waitFor(() => expect(activityHarness.put).toHaveBeenCalledTimes(2));
    const [sessionId, start] = activityHarness.put.mock.calls[0];
    expect(start).toMatchObject({ clientId: 'romd.web', titleId: 'title-1', releaseId: 'release-1' });
    expect(activityHarness.put.mock.calls[1]?.[0]).toBe(sessionId);
    expect(activityHarness.put.mock.calls[1]?.[1]).toMatchObject({
      ...start,
      endedAt: expect.any(String),
      activeDurationSeconds: 7,
    });
    clock.mockRestore();
  });
});

function TitleDestination() {
  const location = useLocation();
  return <output data-testid="title-destination">{JSON.stringify(location.state)}</output>;
}

function renderPlayer(state?: unknown) {
  render(
    <Routes>
      <Route path="/titles/:titleId" element={<TitleDestination />} />
      <Route
        path="/titles/:titleId/releases/:releaseId/play"
        element={<BrowserPlayer />}
      />
    </Routes>,
    {
      withAuth: false,
      routerOptions: {
        initialEntries: [{ pathname: '/titles/title-1/releases/release-1/play', state }],
      },
    },
  );
}

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function createTitle(): ConsumerTitleDetailDto {
  return {
    id: 'title-1',
    system: { key: 'snes', name: 'Super Nintendo Entertainment System', compactLabel: 'SNES' },
    name: 'Super Metroid',
    description: null,
    publisher: null,
    developer: null,
    genre: null,
    releaseDate: null,
    players: null,
    rating: null,
    media: [],
    releases: [
      {
        id: 'release-1',
        name: 'Super Metroid',
        revision: null,
        regions: ['US'],
        languages: ['en'],
        sizeBytes: '5',
        isComplete: true,
      },
    ],
    defaultReleaseId: 'release-1',
  };
}

function createManifest(): ConsumerReleaseManifestDto {
  return {
    releaseId: 'release-1',
    titleId: 'title-1',

    systemKey: 'snes',
    name: 'Super Metroid',
    revision: null,
    isComplete: true,
    runtime: {
      contentType: 'application/octet-stream',
      launch: {
        type: 'file',
        relativePath: 'SNES/Super Metroid.sfc',
      },
      packaging: 'singleFile',
      minimumInstallBytes: '5',
    },
    items: [
      {
        relativePath: 'SNES/Super Metroid.sfc',
        role: 'rom',
        sizeBytes: '5',
        sha256: helloSha256,
        isAvailable: true,
        contentGrant: {
          downloadUrl: '/delivery/content/signed-token',
          expiresAt: '2026-06-05T12:00:00Z',
        },
      },
    ],
  };
}

function preparePlayable() {
    vi.mocked(useTitleDetail).mockReturnValue({
      data: createTitle(),
      isLoading: false,
      isError: false,
    } as ReturnType<typeof useTitleDetail>);
    vi.mocked(useIssueReleaseManifest).mockReturnValue({
      mutateAsync: vi.fn().mockResolvedValue(createManifest()),
      isPending: false,
    } as ReturnType<typeof useIssueReleaseManifest>);
    vi.mocked(fetch)
      .mockResolvedValueOnce(jsonResponse({ playerOrigin: 'http://player.localhost:5175' }))
      .mockResolvedValueOnce(jsonResponse({
        schemaVersion: 1,
        protocol: 1,
        emulatorJs: { version: '4.2.3', source: 'cdn', dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/' },
        cores: ['snes9x'],
        allowedParents: [window.location.origin],
      }))
      .mockResolvedValueOnce(new Response(new TextEncoder().encode('hello'), {
        status: 200,
        headers: { 'Content-Length': '5', 'Content-Type': 'application/octet-stream' },
      }));

}
