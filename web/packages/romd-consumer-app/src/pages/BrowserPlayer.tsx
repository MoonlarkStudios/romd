import {
  ActionIcon,
  Badge,
  Box,
  Button,
  Group,
  Modal,
  Popover,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core';
import {
  IconArrowLeft,
  IconDeviceGamepad2,
  IconPlayerPause,
  IconPlayerPlayFilled,
  IconPower,
} from '@tabler/icons-react';
import { type ReactNode, useCallback, useEffect, useRef, useState } from 'react';
import { Link, type Location, useBlocker, useLocation, useNavigate, useParams } from 'react-router';
import { ArtworkImage } from '../components/library/ArtworkImage';
import { StatusState } from '../components/status/StatusState';
import { useAuth } from '../contexts/AuthContext';
import { useIssueReleaseManifest, useTitleDetail } from '../hooks/useConsumerLibrary';
import { putPlaySessionSnapshot } from '../hooks/usePlayActivity';
import { createBrowserPlayActivity } from '../services/browserPlayActivity';
import {
  type BrowserPlaybackCandidate,
  getBrowserPlaybackFileName,
  getBrowserPlaybackGameName,
  getBrowserPlaybackPreflight,
  getBrowserPlaybackSupport,
} from '../services/browserPlayback';
import { createBrowserSaveId } from '../services/browserSaveIdentity';
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
  | {
      status: 'idle';
    }
  | {
      status: 'discovering-player';
    }
  | {
      status: 'requesting-manifest';
    }
  | {
      status: 'verifying';
      candidate: BrowserPlaybackCandidate;
    }
  | {
      status: 'loading-emulator';
      candidate: BrowserPlaybackCandidate;
      fileName: string;
    }
  | {
      status: 'awaiting-start' | 'recovering-saves';
      candidate: BrowserPlaybackCandidate;
      fileName: string;
    }
  | {
      status: 'playing';
      candidate: BrowserPlaybackCandidate;
      fileName: string;
    }
  | {
      status: 'stopped';
    }
  | {
      status: 'failed';
      message: string;
    };

interface PlayerSession {
  capability: PlayerCapability;
  candidate: BrowserPlaybackCandidate;
  displayName: string;
  file: VerifiedDownload;
  fileName: string;
  saveId: string;
  recoverOlderSaves: boolean;
}

export function BrowserPlayer() {
  const { user, isAuthenticated, isLoading } = useAuth();
  if (isLoading)
    return (
      <StatusState
        kind="loading"
        label="Loading your account…"
      />
    );
  if (!isAuthenticated || !user?.id)
    return (
      <StatusState
        kind="error"
        message="Sign in to use your browser saves."
      />
    );
  return (
    <AccountBrowserPlayer
      key={user.id}
      userId={user.id}
    />
  );
}

function AccountBrowserPlayer({ userId }: { userId: string }) {
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
  const shutdownPromiseRef = useRef<Promise<boolean> | null>(null);
  const romdShutdownRef = useRef(false);
  const navigationShutdownRef = useRef(false);
  const [playActivity] = useState(() =>
    createBrowserPlayActivity({
      recordSnapshot: putPlaySessionSnapshot,
      utcNow: () => new Date().toISOString(),
      monotonicNow: () => performance.now(),
      createSessionId: () => crypto.randomUUID(),
    }),
  );
  const [playerStatus, setPlayerStatus] = useState<PlayerStatus>({
    status: 'idle',
  });
  const [controlsAvailable, setControlsAvailable] = useState(false);
  const [isPaused, setIsPaused] = useState(false);
  const leaveIntentRef = useRef<{
    resume: boolean;
  } | null>(null);
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
    [
      clearVerifiedFile,
    ],
  );

  const stopPlayback = useCallback((): Promise<boolean> => {
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
        try {
          await bridge.shutdown();
        } catch (error) {
          clearVerifiedFile();
          setPlayerSession(null);
          setPlayerStatus({
            status: 'failed',
            message:
              error instanceof Error ? error.message : 'Browser saves could not be persisted.',
          });
          setIsLeaving(false);
          return false;
        }
      }
      clearVerifiedFile();
      setPlayerSession(null);
      setPlayerStatus({
        status: 'stopped',
      });
      return true;
    })();
    shutdownPromiseRef.current = shutdown;
    const clearShutdown = () => {
      if (shutdownPromiseRef.current === shutdown) {
        shutdownPromiseRef.current = null;
      }
    };
    void shutdown.then(clearShutdown, clearShutdown);
    return shutdown;
  }, [
    clearVerifiedFile,
  ]);

  const navigationBlocker = useBlocker(
    useCallback(
      ({ currentLocation, nextLocation }: { currentLocation: Location; nextLocation: Location }) =>
        playerSession !== null &&
        `${currentLocation.pathname}${currentLocation.search}${currentLocation.hash}` !==
          `${nextLocation.pathname}${nextLocation.search}${nextLocation.hash}`,
      [
        playerSession,
      ],
    ),
  );

  const requestLeave = useCallback(() => {
    if (leaveIntentRef.current) {
      return;
    }
    const resume = controlsAvailable && !isPaused;
    leaveIntentRef.current = {
      resume,
    };
    if (resume) playerBridgeRef.current?.control('pause');
    setShowLeaveDialog(true);
  }, [
    controlsAvailable,
    isPaused,
  ]);

  useEffect(() => {
    if (navigationBlocker.state !== 'blocked') {
      navigationShutdownRef.current = false;
      return;
    }
    if (navigationShutdownRef.current) {
      return;
    }

    if (playerStatus.status === 'playing' && playActivity.isActive && !romdShutdownRef.current) {
      requestLeave();
      return;
    }

    navigationShutdownRef.current = true;
    const proceed = navigationBlocker.proceed;
    const reset = navigationBlocker.reset;
    void stopPlayback().then((stopped) => {
      if (stopped) proceed();
      else reset();
    });
  }, [
    navigationBlocker,
    playActivity,
    playerStatus.status,
    requestLeave,
    stopPlayback,
  ]);

  const startPlayback = useCallback(
    async (recoverOlderSaves = false) => {
      if (!title || !release) {
        return;
      }

      abortRef.current?.abort();
      const abortController = new AbortController();
      abortRef.current = abortController;
      romdShutdownRef.current = false;
      playActivity.interrupt();
      setControlsAvailable(false);
      setIsPaused(false);
      clearVerifiedFile();
      playerBridgeRef.current?.dispose();
      playerBridgeRef.current = null;
      setPlayerSession(null);

      const verificationUnavailableReason = getDownloadVerificationUnavailableReason();
      if (verificationUnavailableReason) {
        setPlayerStatus({
          status: 'failed',
          message: verificationUnavailableReason,
        });
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

      setPlayerStatus({
        status: 'discovering-player',
      });

      try {
        const capabilityResult = await fetchPlayerCapability(abortController.signal);
        if (abortController.signal.aborted) {
          return;
        }
        if (capabilityResult.status === 'unavailable') {
          setPlayerStatus({
            status: 'failed',
            message: capabilityResult.reason,
          });
          return;
        }

        const capability = capabilityResult.capability;
        preflight = getBrowserPlaybackPreflight(title, release, capability.cores);
        if (preflight.status === 'download-only') {
          setPlayerStatus({
            status: 'failed',
            message: preflight.reason,
          });
          return;
        }

        setPlayerStatus({
          status: 'requesting-manifest',
        });
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

        const verification = await verifyManifestItemDownload(
          support.candidate.item,
          abortController.signal,
        );
        if (abortController.signal.aborted) {
          if (verification.status === 'verified') revokeVerifiedDownload(verification.file);
          return;
        }

        if (verification.status !== 'verified') {
          setPlayerStatus({
            status: 'failed',
            message: verification.failure.message,
          });
          return;
        }

        let saveId: string;
        try {
          saveId = await createBrowserSaveId(
            manifest.serverInstanceId,
            userId,
            verification.file.sha256,
          );
        } catch (error) {
          revokeVerifiedDownload(verification.file);
          throw error;
        }
        if (abortController.signal.aborted) {
          revokeVerifiedDownload(verification.file);
          return;
        }
        replaceVerifiedFile(verification.file);
        setPlayerSession({
          capability,
          candidate: support.candidate,
          displayName: getBrowserPlaybackGameName(title, release),
          file: verification.file,
          fileName: getBrowserPlaybackFileName(verification.file),
          saveId,
          recoverOlderSaves,
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
    },
    [
      clearVerifiedFile,
      issueManifest,
      playActivity,
      release,
      replaceVerifiedFile,
      title,
      userId,
    ],
  );

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
  }, [
    release?.id,
  ]);

  useEffect(
    () => () => {
      abortRef.current?.abort();
      // Unmount cannot await a save acknowledgement; normal exits await
      // stopPlayback. Account replacement must still discard the old frame.
      void playerBridgeRef.current?.shutdown().catch(() => undefined);
      playerBridgeRef.current = null;
      clearVerifiedFile();
    },
    [
      clearVerifiedFile,
    ],
  );

  useEffect(() => {
    const frame = playerFrameRef.current;
    if (!frame || !playerSession) {
      return;
    }

    let active = true;
    const bridge = createPlayerBridge(
      frame,
      playerSession.capability,
      {
        core: playerSession.candidate.core,
        displayName: playerSession.displayName,
        verifiedFile: playerSession.file,
        saveId: playerSession.saveId,
        recoverOlderSaves: playerSession.recoverOlderSaves,
      },
      (event) => {
        if (!active) return;
        if (event.event === 'save-recovery-required')
          setPlayerStatus({
            status: 'recovering-saves',
            candidate: playerSession.candidate,
            fileName: playerSession.fileName,
          });
        if (event.event === 'save-recovery-complete')
          setPlayerStatus({
            status: 'loading-emulator',
            candidate: playerSession.candidate,
            fileName: playerSession.fileName,
          });
        if (event.event === 'controls-ready') setControlsAvailable(true);
        if (event.event === 'controls-unavailable') setControlsAvailable(false);
        if (event.event === 'paused') {
          setIsPaused(true);
          playActivity.pause();
        }
        if (event.event === 'resumed') {
          setIsPaused(false);
          playActivity.resume();
        }
        if (event.event === 'exit') {
          if (playActivity.isActive) {
            void playActivity
              .finish()
              .catch(() => undefined)
              .finally(() => {
                if (active && !romdShutdownRef.current) {
                  navigate(titleId ? `/titles/${titleId}` : '/library', {
                    state: navigationState,
                  });
                }
              });
          } else if (!romdShutdownRef.current) {
            navigate(titleId ? `/titles/${titleId}` : '/library', {
              state: navigationState,
            });
          }
          return;
        }
        if (event.event === 'ready') {
          setPlayerStatus((current) =>
            current.status === 'loading-emulator'
              ? {
                  ...current,
                  status: 'awaiting-start',
                }
              : current,
          );
        }
        if (event.event === 'game-started') {
          if (titleId && releaseId) playActivity.start(titleId, releaseId);
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
          playActivity.interrupt();
          setPlayerStatus({
            status: 'failed',
            message: playerErrorMessage(event.code, event.message),
          });
          playerBridgeRef.current = null;
          setPlayerSession(null);
          clearVerifiedFile();
        }
      },
    );
    playerBridgeRef.current = bridge;
    void bridge.ready.catch((error: unknown) => {
      if (!active || playerBridgeRef.current !== bridge) {
        return;
      }
      playerBridgeRef.current = null;
      setPlayerSession(null);
      clearVerifiedFile();
      setPlayerStatus({
        status: 'failed',
        message:
          error instanceof Error ? error.message : 'The browser player failed to initialize.',
      });
    });

    return () => {
      active = false;
      if (playerBridgeRef.current === bridge) {
        bridge.dispose();
        playerBridgeRef.current = null;
      }
    };
  }, [
    clearVerifiedFile,
    navigate,
    navigationState,
    playActivity,
    playerSession,
    releaseId,
    titleId,
  ]);

  const quitGame = async () => {
    if (playerStatus.status === 'playing' && !isLeaving) {
      requestLeave();
      return;
    }
    if (!(await stopPlayback())) return;
    navigate(titleId ? `/titles/${titleId}` : '/library', {
      state: navigationState,
    });
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
    if (!(await stopPlayback())) {
      leaveIntentRef.current = null;
      setShowLeaveDialog(false);
      if (navigationBlocker.state === 'blocked') navigationBlocker.reset();
      return;
    }
    leaveIntentRef.current = null;
    setShowLeaveDialog(false);
    if (proceed) proceed();
    else
      navigate(titleId ? `/titles/${titleId}` : '/library', {
        state: navigationState,
      });
  };

  if (titleQuery.isLoading || titleQuery.isError || !title || !release) {
    return (
      <PlayShell>
        {titleQuery.isLoading ? (
          <StatusState
            kind="loading"
            label="Loading your game…"
          />
        ) : titleQuery.isError || !title ? (
          <StatusState
            kind="error"
            message="This title could not be loaded."
            onRetry={() => void titleQuery.refetch()}
          />
        ) : (
          <StatusState
            kind="empty"
            title="Version unavailable"
            message="This version could not be found."
          />
        )}
        <Button
          component={Link}
          state={navigationState}
          to={titleId ? `/titles/${titleId}` : '/library'}
          variant="subtle"
          leftSection={<IconArrowLeft size={16} />}
        >
          Back to game
        </Button>
      </PlayShell>
    );
  }

  const region = release.regions[0];
  const isPlaying = playerStatus.status === 'playing';
  const isReady = playerStatus.status === 'awaiting-start';
  const isRecovering = playerStatus.status === 'recovering-saves';
  const isStarting = [
    'discovering-player',
    'requesting-manifest',
    'verifying',
    'loading-emulator',
  ].includes(playerStatus.status);

  return (
    <Box className={classes.page}>
      <Modal
        opened={showLeaveDialog}
        onClose={keepPlaying}
        returnFocus={false}
        onExitTransitionEnd={() => {
          if (!isLeaving) playerFrameRef.current?.focus();
        }}
        title="Leave this game?"
        centered
        size="sm"
        closeOnEscape={!isLeaving}
        closeOnClickOutside={!isLeaving}
        withCloseButton={!isLeaving}
      >
        <Stack gap="lg">
          <Text>Unsaved progress may be lost when you leave.</Text>
          <Group justify="flex-end">
            <Button
              variant="subtle"
              color="gray"
              loading={isLeaving}
              onClick={() => void confirmLeave()}
            >
              Leave game
            </Button>
            <Button
              data-autofocus
              disabled={isLeaving}
              onClick={keepPlaying}
            >
              Keep playing
            </Button>
          </Group>
        </Stack>
      </Modal>
      <section
        className={classes.player}
        aria-label={`${title.name} browser player`}
      >
        <div>
          <header className={classes.header}>
            <Group
              gap="sm"
              wrap="nowrap"
              style={{
                minWidth: 0,
              }}
            >
              {!isPlaying && (
                <Tooltip label="Back to game details">
                  <ActionIcon
                    variant="subtle"
                    color="gray"
                    size="lg"
                    aria-label="Back to game details"
                    disabled={isLeaving}
                    onClick={quitGame}
                  >
                    <IconArrowLeft size={18} />
                  </ActionIcon>
                </Tooltip>
              )}
              <Box
                style={{
                  minWidth: 0,
                }}
              >
                <Text
                  component="h1"
                  className="romd-card-title"
                  truncate
                >
                  {title.name}
                </Text>
                <Text
                  className="romd-metadata"
                  truncate
                >
                  {[
                    title.system.name,
                    region,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </Text>
              </Box>
            </Group>
            <Group
              gap="xs"
              wrap="nowrap"
            >
              {isReady && (
                <Button
                  variant="subtle"
                  color="gray"
                  onClick={() => void startPlayback(true)}
                >
                  Recover older saves
                </Button>
              )}
              <Badge
                visibleFrom="sm"
                color={isPlaying ? 'mint' : 'gray'}
                variant="light"
              >
                {isLeaving
                  ? 'Closing'
                  : isPlaying
                    ? controlsAvailable && isPaused
                      ? 'Paused'
                      : 'In session'
                    : isReady
                      ? 'Ready'
                      : playerStatus.status === 'failed'
                        ? 'Unavailable'
                        : 'Preparing'}
              </Badge>
              {isPlaying && (
                <>
                  {controlsAvailable && (
                    <>
                      <Button
                        variant="light"
                        disabled={isLeaving}
                        leftSection={
                          isPaused ? (
                            <IconPlayerPlayFilled size={18} />
                          ) : (
                            <IconPlayerPause size={18} />
                          )
                        }
                        onClick={() => {
                          playerBridgeRef.current?.control(isPaused ? 'resume' : 'pause');
                          if (isPaused) playerFrameRef.current?.focus();
                        }}
                      >
                        {isPaused ? 'Resume' : 'Pause'}
                      </Button>
                      <Button
                        variant="subtle"
                        color="gray"
                        disabled={isLeaving}
                        leftSection={<IconDeviceGamepad2 size={18} />}
                        onClick={() => playerBridgeRef.current?.control('open-controls')}
                      >
                        Controls
                      </Button>
                    </>
                  )}
                  {!controlsAvailable && (
                    <Popover
                      width={300}
                      position="bottom-end"
                      withArrow
                      trapFocus
                      returnFocus
                    >
                      <Popover.Target>
                        <Button
                          variant="subtle"
                          color="gray"
                          leftSection={<IconDeviceGamepad2 size={18} />}
                        >
                          Player help
                        </Button>
                      </Popover.Target>
                      <Popover.Dropdown>
                        <Stack
                          gap="sm"
                          tabIndex={-1}
                          data-autofocus
                        >
                          <Text className="romd-card-title">Make it yours</Text>
                          <Text size="sm">
                            Move your pointer over the game or tap it to reveal the player toolbar.
                            Use Tab to navigate its controls.
                          </Text>
                          <Text size="sm">
                            Open Control Settings to view keyboard mappings and configure your
                            controller.
                          </Text>
                          <Text size="sm">
                            Pause, sound, fullscreen, and save tools are also in the toolbar.
                          </Text>
                        </Stack>
                      </Popover.Dropdown>
                    </Popover>
                  )}
                  <Button
                    variant="subtle"
                    color="gray"
                    leftSection={<IconPower size={16} />}
                    onClick={quitGame}
                    loading={isLeaving}
                  >
                    Quit
                  </Button>
                </>
              )}
            </Group>
          </header>
          {playerSession?.capability.saveStorageWarning && (
            <Text
              role="status"
              size="sm"
              px="md"
              py="xs"
            >
              {playerSession.capability.saveStorageWarning}
            </Text>
          )}
        </div>
        <div
          className={classes.stage}
          data-testid="player-stage"
        >
          {!isPlaying && (
            <div
              className={classes.backdrop}
              aria-hidden="true"
            >
              <ArtworkImage
                artwork={title.artwork}
                artworkRole="Backdrop"
                priority
                style={{
                  height: '100%',
                  aspectRatio: 'auto',
                }}
              />
            </div>
          )}
          <div
            className={classes.launch}
            data-playing={isPlaying || undefined}
            data-testid="player-launch"
          >
            {!isPlaying && !isRecovering && (
              <div className={classes.identity}>
                <div className={classes.logo}>
                  <ArtworkImage
                    artwork={title.artwork}
                    artworkRole="Logo"
                    priority
                    alt=""
                    style={{
                      aspectRatio: 'auto',
                      minHeight: 'clamp(64px, 16vh, 144px)',
                    }}
                    fallback={
                      <Text
                        className="romd-page-heading"
                        ta="center"
                      >
                        {title.name}
                      </Text>
                    }
                  />
                </div>
                <Text
                  className="romd-metadata"
                  ta="center"
                >
                  {[
                    title.system.name,
                    region,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </Text>
              </div>
            )}
            {playerSession && (
              <iframe
                key={playerSession.file.objectUrl}
                ref={playerFrameRef}
                className={classes.frame}
                data-ready={isReady || isPlaying || isRecovering || undefined}
                data-recovering={isRecovering || undefined}
                title={`${title.name} browser player`}
                src={`${playerSession.capability.playerOrigin}/`}
                sandbox="allow-scripts allow-same-origin allow-pointer-lock allow-downloads"
                allow="autoplay; gamepad; fullscreen; screen-wake-lock"
                referrerPolicy="no-referrer"
              />
            )}
            {!isReady && !isPlaying && !isRecovering && (
              <div className={classes.launchState}>
                {playerStatus.status === 'failed' ? (
                  <StatusState
                    kind="error"
                    message={playerStatus.message}
                    onRetry={() => void startPlayback()}
                  />
                ) : isStarting ? (
                  <StatusState
                    kind="loading"
                    label={statusText(playerStatus)}
                  />
                ) : (
                  <Button
                    size="lg"
                    leftSection={<IconPlayerPlayFilled size={18} />}
                    onClick={() => void startPlayback()}
                  >
                    Start game
                  </Button>
                )}
              </div>
            )}
          </div>
          {isLeaving && (
            <div className={classes.leaving}>
              <StatusState
                kind="loading"
                label="Closing your game…"
              />
            </div>
          )}
        </div>
      </section>
    </Box>
  );
}

function PlayShell({ children }: { children: ReactNode }) {
  return (
    <div className={classes.page}>
      <Stack align="center">{children}</Stack>
    </div>
  );
}

function statusText(status: PlayerStatus): string {
  switch (status.status) {
    case 'discovering-player':
      return 'Connecting to your player…';
    case 'requesting-manifest':
      return 'Preparing your game…';
    case 'verifying':
      return 'Checking your game…';
    case 'loading-emulator':
      return 'Loading your player…';
    default:
      return 'Preparing your game…';
  }
}

function playerErrorMessage(code: string, message: string): string {
  if (code.includes('asset') || code.includes('loader') || code.includes('cdn')) {
    return 'The player could not load. Check your connection and try again.';
  }
  return message;
}
