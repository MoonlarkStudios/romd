import { ActionIcon, Badge, Box, Button, Group, Modal, Popover, Stack, Text, Tooltip } from '@mantine/core';
import { IconArrowLeft, IconDeviceGamepad2, IconPlayerPause, IconPlayerPlayFilled, IconPower } from '@tabler/icons-react';
import { type ReactNode, useCallback, useEffect, useRef, useState } from 'react';
import { Link, type Location, useBlocker, useLocation, useNavigate, useParams } from 'react-router';
import { ArtworkImage } from '../components/library/ArtworkImage';
import { StatusState } from '../components/status/StatusState';
import { useIssueReleaseManifest, useTitleDetail } from '../hooks/useConsumerLibrary';
import { putPlaySessionSnapshot } from '../hooks/usePlayActivity';
import {
  type BrowserPlaybackCandidate,
  getBrowserPlaybackFileName,
  getBrowserPlaybackGameName,
  getBrowserPlaybackPreflight,
  getBrowserPlaybackSupport,
} from '../services/browserPlayback';
import {
  getDownloadVerificationUnavailableReason,
  revokeVerifiedDownload,
  type VerifiedDownload,
  verifyManifestItemDownload,
} from '../services/downloadVerification';
import {
  createPlayerBridge,
  fetchPlayerCapability,
  type PlayerBridge,
  type PlayerCapability,
} from '../services/playerBridge';
import classes from './BrowserPlayer.module.css';

type PlayerStatus =
  | { status: 'idle' }
  | { status: 'discovering-player' }
  | { status: 'requesting-manifest' }
  | { status: 'verifying'; candidate: BrowserPlaybackCandidate }
  | { status: 'loading-emulator'; candidate: BrowserPlaybackCandidate; fileName: string }
  | { status: 'awaiting-start'; candidate: BrowserPlaybackCandidate; fileName: string }
  | { status: 'playing'; candidate: BrowserPlaybackCandidate; fileName: string }
  | { status: 'stopped' }
  | { status: 'failed'; message: string };

interface PlayerSession {
  capability: PlayerCapability;
  candidate: BrowserPlaybackCandidate;
  displayName: string;
  file: VerifiedDownload;
  fileName: string;
}

interface ActivePlayActivity {
  sessionId: string;
  titleId: string;
  releaseId: string;
  startedAt: string;
  monotonicStartedAt: number;
  pausedAt: number | null;
  pausedDurationMs: number;
}

export function BrowserPlayer() {
  const { titleId, releaseId } = useParams();
  const navigate = useNavigate();
  const { state: navigationState } = useLocation();
  const titleQuery = useTitleDetail(titleId);
  const issueManifest = useIssueReleaseManifest();
  const title = titleQuery.data;
  const release = title?.releases.find((candidate) => candidate.id === releaseId) ?? null;
  const abortRef = useRef<AbortController | null>(null);
  const verifiedFileRef = useRef<VerifiedDownload | null>(null);
  const playerFrameRef = useRef<HTMLIFrameElement | null>(null);
  const playerBridgeRef = useRef<PlayerBridge | null>(null);
  const shutdownPromiseRef = useRef<Promise<void> | null>(null);
  const romdShutdownRef = useRef(false);
  const navigationShutdownRef = useRef(false);
  const activityRef = useRef<ActivePlayActivity | null>(null);
  const [playerStatus, setPlayerStatus] = useState<PlayerStatus>({ status: 'idle' });
  const [controlsAvailable, setControlsAvailable] = useState(false);
  const [isPaused, setIsPaused] = useState(false);
  const leaveIntentRef = useRef<{ resume: boolean } | null>(null);
  const [showLeaveDialog, setShowLeaveDialog] = useState(false);
  const [isLeaving, setIsLeaving] = useState(false);
  const [playerSession, setPlayerSession] = useState<PlayerSession | null>(null);

  const clearVerifiedFile = useCallback(() => {
    if (verifiedFileRef.current) {
      revokeVerifiedDownload(verifiedFileRef.current);
      verifiedFileRef.current = null;
    }
  }, []);

  const replaceVerifiedFile = useCallback(
    (file: VerifiedDownload) => {
      clearVerifiedFile();
      verifiedFileRef.current = file;
    },
    [clearVerifiedFile],
  );

  const stopPlayback = useCallback((): Promise<void> => {
    if (shutdownPromiseRef.current) {
      return shutdownPromiseRef.current;
    }

    setIsLeaving(true);
    romdShutdownRef.current = true;
    abortRef.current?.abort();
    abortRef.current = null;
    const bridge = playerBridgeRef.current;
    playerBridgeRef.current = null;
    const shutdown = (async () => {
      if (bridge) {
        await bridge.shutdown();
      }
      clearVerifiedFile();
      setPlayerSession(null);
      setPlayerStatus({ status: 'stopped' });
    })();
    shutdownPromiseRef.current = shutdown;
    const clearShutdown = () => {
      if (shutdownPromiseRef.current === shutdown) {
        shutdownPromiseRef.current = null;
      }
    };
    void shutdown.then(clearShutdown, clearShutdown);
    return shutdown;
  }, [clearVerifiedFile]);

  const navigationBlocker = useBlocker(
    useCallback(
      ({ currentLocation, nextLocation }: { currentLocation: Location; nextLocation: Location }) =>
        playerSession !== null &&
        `${currentLocation.pathname}${currentLocation.search}${currentLocation.hash}` !==
          `${nextLocation.pathname}${nextLocation.search}${nextLocation.hash}`,
      [playerSession],
    ),
  );

  const requestLeave = useCallback(() => {
    if (leaveIntentRef.current) {
      return;
    }
    const resume = controlsAvailable && !isPaused;
    leaveIntentRef.current = { resume };
    if (resume) playerBridgeRef.current?.control('pause');
    setShowLeaveDialog(true);
  }, [controlsAvailable, isPaused]);

  useEffect(() => {
    if (navigationBlocker.state !== 'blocked') {
      navigationShutdownRef.current = false;
      return;
    }
    if (navigationShutdownRef.current) {
      return;
    }

    if (playerStatus.status === 'playing' && activityRef.current && !romdShutdownRef.current) {
      requestLeave();
      return;
    }

    navigationShutdownRef.current = true;
    const proceed = navigationBlocker.proceed;
    void stopPlayback().finally(() => {
      proceed();
    });
  }, [navigationBlocker, playerStatus.status, requestLeave, stopPlayback]);

  const startPlayback = useCallback(async () => {
    if (!title || !release) {
      return;
    }

    abortRef.current?.abort();
    const abortController = new AbortController();
    abortRef.current = abortController;
    romdShutdownRef.current = false;
    activityRef.current = null;
    setControlsAvailable(false);
    setIsPaused(false);
    clearVerifiedFile();
    playerBridgeRef.current?.dispose();
    playerBridgeRef.current = null;
    setPlayerSession(null);

    const verificationUnavailableReason = getDownloadVerificationUnavailableReason();
    if (verificationUnavailableReason) {
      setPlayerStatus({ status: 'failed', message: verificationUnavailableReason });
      return;
    }

    let preflight = getBrowserPlaybackPreflight(title, release);
    if (preflight.status === 'download-only') {
      setPlayerStatus({
        status: 'failed',
        message: preflight.reason,
      });
      return;
    }

    setPlayerStatus({ status: 'discovering-player' });

    try {
      const capabilityResult = await fetchPlayerCapability(abortController.signal);
      if (abortController.signal.aborted) {
        return;
      }
      if (capabilityResult.status === 'unavailable') {
        setPlayerStatus({ status: 'failed', message: capabilityResult.reason });
        return;
      }

      const capability = capabilityResult.capability;
      preflight = getBrowserPlaybackPreflight(title, release, capability.cores);
      if (preflight.status === 'download-only') {
        setPlayerStatus({ status: 'failed', message: preflight.reason });
        return;
      }

      setPlayerStatus({ status: 'requesting-manifest' });
      const manifest = await issueManifest.mutateAsync(release.id);
      if (abortController.signal.aborted) {
        return;
      }

      const support = getBrowserPlaybackSupport(title, release, manifest, capability.cores);
      if (support.status === 'unsupported') {
        setPlayerStatus({
          status: 'failed',
          message: support.reason,
        });
        return;
      }

      setPlayerStatus({
        status: 'verifying',
        candidate: support.candidate,
      });

      const verification = await verifyManifestItemDownload(support.candidate.item, abortController.signal);
      if (abortController.signal.aborted) {
        return;
      }

      if (verification.status !== 'verified') {
        setPlayerStatus({
          status: 'failed',
          message: verification.failure.message,
        });
        return;
      }

      replaceVerifiedFile(verification.file);
      setPlayerSession({
        capability,
        candidate: support.candidate,
        displayName: getBrowserPlaybackGameName(title, release),
        file: verification.file,
        fileName: getBrowserPlaybackFileName(verification.file),
      });
      setPlayerStatus({
        status: 'loading-emulator',
        candidate: support.candidate,
        fileName: getBrowserPlaybackFileName(verification.file),
      });
    } catch (error) {
      if (abortController.signal.aborted) {
        return;
      }

      setPlayerStatus({
        status: 'failed',
        message: error instanceof Error ? error.message : 'Browser playback failed to start.',
      });
    } finally {
      if (abortRef.current === abortController) {
        abortRef.current = null;
      }
    }
  }, [clearVerifiedFile, issueManifest, release, replaceVerifiedFile, title]);

  // Auto-start once the selected version resolves. Keyed on the release id (not on
  // `startPlayback`) so React StrictMode's remount re-triggers a fresh run instead
  // of getting stuck on an aborted first attempt, and so a flipping mutation
  // identity doesn't restart playback mid-flight.
  const startPlaybackRef = useRef(startPlayback);
  useEffect(() => {
    startPlaybackRef.current = startPlayback;
  });
  // biome-ignore lint/correctness/useExhaustiveDependencies: intentionally keyed on release id only
  useEffect(() => {
    if (!release) {
      return;
    }

    void startPlaybackRef.current();
  }, [release?.id]);

  useEffect(
    () => () => {
      abortRef.current?.abort();
      void playerBridgeRef.current?.shutdown();
      playerBridgeRef.current = null;
      clearVerifiedFile();
    },
    [clearVerifiedFile],
  );

  useEffect(() => {
    const frame = playerFrameRef.current;
    if (!frame || !playerSession) {
      return;
    }

    const bridge = createPlayerBridge(
      frame,
      playerSession.capability,
      {
        core: playerSession.candidate.core,
        displayName: playerSession.displayName,
        verifiedFile: playerSession.file,
      },
      (event) => {
        if (event.event === 'controls-ready') setControlsAvailable(true);
        if (event.event === 'controls-unavailable') setControlsAvailable(false);
        if (event.event === 'paused') {
          setIsPaused(true);
          const activity = activityRef.current;
          if (activity && activity.pausedAt === null) activity.pausedAt = performance.now();
        }
        if (event.event === 'resumed') {
          setIsPaused(false);
          const activity = activityRef.current;
          if (activity && activity.pausedAt !== null) {
            activity.pausedDurationMs += performance.now() - activity.pausedAt;
            activity.pausedAt = null;
          }
        }
        if (event.event === 'exit') {
          const activity = activityRef.current;
          activityRef.current = null;
          if (activity) {
            const endedAt = new Date().toISOString();
            const elapsedUntil = activity.pausedAt ?? performance.now();
            const activeDurationSeconds = Math.max(
              0,
              Math.floor((elapsedUntil - activity.monotonicStartedAt - activity.pausedDurationMs) / 1000),
            );
            void putPlaySessionSnapshot(activity.sessionId, {
              clientId: 'romd.web',
              titleId: activity.titleId,
              releaseId: activity.releaseId,
              startedAt: activity.startedAt,
              endedAt,
              activeDurationSeconds,
            }).catch(() => undefined).finally(() => {
              if (!romdShutdownRef.current) {
                navigate(titleId ? `/titles/${titleId}` : '/library', { state: navigationState });
              }
            });
          } else if (!romdShutdownRef.current) {
            navigate(titleId ? `/titles/${titleId}` : '/library', { state: navigationState });
          }
          return;
        }
        if (event.event === 'ready') {
          setPlayerStatus((current) =>
            current.status === 'loading-emulator'
              ? { ...current, status: 'awaiting-start' }
              : current,
          );
        }
        if (event.event === 'game-started') {
          if (!activityRef.current && titleId && releaseId) {
            const activity = {
              sessionId: crypto.randomUUID(),
              titleId,
              releaseId,
              startedAt: new Date().toISOString(),
              monotonicStartedAt: performance.now(),
              pausedAt: null,
              pausedDurationMs: 0,
            } satisfies ActivePlayActivity;
            activityRef.current = activity;
            void putPlaySessionSnapshot(activity.sessionId, {
              clientId: 'romd.web',
              titleId: activity.titleId,
              releaseId: activity.releaseId,
              startedAt: activity.startedAt,
            }).catch(() => undefined);
          }
          setPlayerStatus((current) =>
            current.status === 'loading-emulator' || current.status === 'awaiting-start'
              ? {
                  status: 'playing',
                  candidate: current.candidate,
                  fileName: current.fileName,
                }
              : current,
          );
        }
        if (event.event === 'error') {
          // Preserve any already-sent open snapshot as crash/interruption evidence,
          // but allow a subsequent real start to receive a fresh session identity.
          activityRef.current = null;
          setPlayerStatus({ status: 'failed', message: playerErrorMessage(event.code, event.message) });
          playerBridgeRef.current = null;
          setPlayerSession(null);
          clearVerifiedFile();
        }
      },
    );
    playerBridgeRef.current = bridge;
    void bridge.ready.catch((error: unknown) => {
      if (playerBridgeRef.current !== bridge) {
        return;
      }
      playerBridgeRef.current = null;
      setPlayerSession(null);
      clearVerifiedFile();
      setPlayerStatus({
        status: 'failed',
        message: error instanceof Error ? error.message : 'The browser player failed to initialize.',
      });
    });

    return () => {
      if (playerBridgeRef.current === bridge) {
        bridge.dispose();
        playerBridgeRef.current = null;
      }
    };
  }, [clearVerifiedFile, navigate, navigationState, playerSession, releaseId, titleId]);

  const quitGame = async () => {
    if (playerStatus.status === 'playing' && !isLeaving) {
      requestLeave();
      return;
    }
    await stopPlayback();
    navigate(titleId ? `/titles/${titleId}` : '/library', { state: navigationState });
  };

  const keepPlaying = () => {
    if (isLeaving) return;
    const intent = leaveIntentRef.current;
    leaveIntentRef.current = null;
    if (navigationBlocker.state === 'blocked') navigationBlocker.reset();
    setShowLeaveDialog(false);
    if (intent?.resume) playerBridgeRef.current?.control('resume');
    playerFrameRef.current?.focus();
  };

  const confirmLeave = async () => {
    if (!leaveIntentRef.current || isLeaving) return;
    const proceed = navigationBlocker.state === 'blocked' ? navigationBlocker.proceed : null;
    navigationShutdownRef.current = true;
    await stopPlayback();
    leaveIntentRef.current = null;
    setShowLeaveDialog(false);
    if (proceed) proceed();
    else navigate(titleId ? `/titles/${titleId}` : '/library', { state: navigationState });
  };

  if (titleQuery.isLoading || titleQuery.isError || !title || !release) {
    return (
      <PlayShell>
        {titleQuery.isLoading ? (
          <StatusState kind="loading" label="Loading your game…" />
        ) : titleQuery.isError || !title ? (
          <StatusState kind="error" message="This title could not be loaded." onRetry={() => void titleQuery.refetch()} />
        ) : (
          <StatusState kind="empty" title="Version unavailable" message="This version could not be found." />
        )}
        <Button component={Link} state={navigationState} to={titleId ? `/titles/${titleId}` : '/library'} variant="subtle" leftSection={<IconArrowLeft size={16} />}>
          Back to game
        </Button>
      </PlayShell>
    );
  }

  const region = release.regions[0];
  const isPlaying = playerStatus.status === 'playing';
  const isReady = playerStatus.status === 'awaiting-start';
  const isStarting = ['discovering-player', 'requesting-manifest', 'verifying', 'loading-emulator'].includes(playerStatus.status);

  return (
    <Box className={classes.page}>
      <Modal opened={showLeaveDialog} onClose={keepPlaying} returnFocus={false} onExitTransitionEnd={() => { if (!isLeaving) playerFrameRef.current?.focus(); }} title="Leave this game?" centered size="sm" closeOnEscape={!isLeaving} closeOnClickOutside={!isLeaving} withCloseButton={!isLeaving}>
        <Stack gap="lg">
          <Text>Unsaved progress may be lost when you leave.</Text>
          <Group justify="flex-end">
            <Button variant="subtle" color="gray" loading={isLeaving} onClick={() => void confirmLeave()}>Leave game</Button>
            <Button data-autofocus disabled={isLeaving} onClick={keepPlaying}>Keep playing</Button>
          </Group>
        </Stack>
      </Modal>
      <section className={classes.player} aria-label={`${title.name} browser player`}>
        <header className={classes.header}>
          <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
            {!isPlaying && (
              <Tooltip label="Back to game details">
                <ActionIcon variant="subtle" color="gray" size="lg" aria-label="Back to game details" disabled={isLeaving} onClick={quitGame}>
                  <IconArrowLeft size={18} />
                </ActionIcon>
              </Tooltip>
            )}
            <Box style={{ minWidth: 0 }}>
              <Text component="h1" className="romd-card-title" truncate>{title.name}</Text>
              <Text className="romd-metadata" truncate>{[title.system.name, region].filter(Boolean).join(' · ')}</Text>
            </Box>
          </Group>
          <Group gap="xs" wrap="nowrap">
            <Badge visibleFrom="sm" color={isPlaying ? 'mint' : 'gray'} variant="light">
              {isLeaving ? 'Closing' : isPlaying ? (controlsAvailable && isPaused ? 'Paused' : 'In session') : isReady ? 'Ready' : playerStatus.status === 'failed' ? 'Unavailable' : 'Preparing'}
            </Badge>
            {isPlaying && (
              <>
                {controlsAvailable && (
                  <>
                    <Button variant="light" disabled={isLeaving} leftSection={isPaused ? <IconPlayerPlayFilled size={18} /> : <IconPlayerPause size={18} />} onClick={() => {
                      playerBridgeRef.current?.control(isPaused ? 'resume' : 'pause');
                      if (isPaused) playerFrameRef.current?.focus();
                    }}>{isPaused ? 'Resume' : 'Pause'}</Button>
                    <Button variant="subtle" color="gray" disabled={isLeaving} leftSection={<IconDeviceGamepad2 size={18} />} onClick={() => playerBridgeRef.current?.control('open-controls')}>Controls</Button>
                  </>
                )}
                {!controlsAvailable && <Popover width={300} position="bottom-end" withArrow trapFocus returnFocus>
                  <Popover.Target>
                    <Button variant="subtle" color="gray" leftSection={<IconDeviceGamepad2 size={18} />}>Player help</Button>
                  </Popover.Target>
                  <Popover.Dropdown>
                    <Stack gap="sm">
                      <Text className="romd-card-title">Make it yours</Text>
                      <Text size="sm">Move your pointer over the game or tap it to reveal the player toolbar. Use Tab to navigate its controls.</Text>
                      <Text size="sm">Open Control Settings to view keyboard mappings and configure your controller.</Text>
                      <Text size="sm">Pause, sound, fullscreen, and save tools are also in the toolbar.</Text>
                    </Stack>
                  </Popover.Dropdown>
                </Popover>}
                <Button variant="subtle" color="gray" leftSection={<IconPower size={16} />} onClick={quitGame} loading={isLeaving}>Quit</Button>
              </>
            )}
          </Group>
        </header>
        <div className={classes.stage} data-testid="player-stage">
          {!isPlaying && (
            <div className={classes.backdrop} aria-hidden="true">
              <ArtworkImage artwork={title.artwork} role="Backdrop" priority style={{ height: '100%', aspectRatio: 'auto' }} />
            </div>
          )}
          <div className={classes.launch} data-playing={isPlaying || undefined} data-testid="player-launch">
            {!isPlaying && (
              <div className={classes.identity}>
                <div className={classes.logo}>
                  <ArtworkImage
                    artwork={title.artwork}
                    role="Logo"
                    priority
                    alt=""
                    style={{ aspectRatio: 'auto', minHeight: 'clamp(64px, 16vh, 144px)' }}
                    fallback={<Text className="romd-page-heading" ta="center">{title.name}</Text>}
                  />
                </div>
                <Text className="romd-metadata" ta="center">{[title.system.name, region].filter(Boolean).join(' · ')}</Text>
              </div>
            )}
            {playerSession && (
              <iframe
                ref={playerFrameRef}
                className={classes.frame}
                data-ready={isReady || isPlaying || undefined}
                title={`${title.name} browser player`}
                src={`${playerSession.capability.playerOrigin}/`}
                sandbox="allow-scripts allow-same-origin allow-pointer-lock allow-downloads"
                allow="autoplay; gamepad; fullscreen; screen-wake-lock"
                referrerPolicy="no-referrer"
              />
            )}
            {!isReady && !isPlaying && (
              <div className={classes.launchState}>
                {playerStatus.status === 'failed' ? (
                  <StatusState kind="error" message={playerStatus.message} onRetry={() => void startPlayback()} />
                ) : isStarting ? (
                  <StatusState kind="loading" label={statusText(playerStatus)} />
                ) : (
                  <Button size="lg" leftSection={<IconPlayerPlayFilled size={18} />} onClick={() => void startPlayback()}>Start game</Button>
                )}
              </div>
            )}
          </div>
          {isLeaving && <div className={classes.leaving}><StatusState kind="loading" label="Closing your game…" /></div>}
        </div>

      </section>
    </Box>
  );
}

function PlayShell({ children }: { children: ReactNode }) {
  return <div className={classes.page}><Stack align="center">{children}</Stack></div>;
}

function statusText(status: PlayerStatus): string {
  switch (status.status) {
    case 'discovering-player': return 'Connecting to your player…';
    case 'requesting-manifest': return 'Preparing your game…';
    case 'verifying': return 'Checking your game…';
    case 'loading-emulator': return 'Loading your player…';
    default: return 'Preparing your game…';
  }
}

function playerErrorMessage(code: string, message: string): string {
  if (code.includes('asset') || code.includes('loader') || code.includes('cdn')) {
    return 'The player could not load. Check your connection and try again.';
  }
  return message;
}
