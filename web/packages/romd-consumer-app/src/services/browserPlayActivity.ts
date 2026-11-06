import type { UpsertPlaySessionRequest } from '@romd/consumer-api-client';

interface ActivePlayActivity {
  sessionId: string;
  titleId: string;
  releaseId: string;
  startedAt: string;
  monotonicStartedAt: number;
  pausedAt: number | null;
  pausedDurationMs: number;
}

export interface BrowserPlayActivityEffects {
  recordSnapshot: (sessionId: string, body: UpsertPlaySessionRequest) => Promise<void>;
  utcNow: () => string;
  monotonicNow: () => number;
  createSessionId: () => string;
}

export interface BrowserPlayActivity {
  readonly isActive: boolean;
  start: (titleId: string, releaseId: string) => void;
  pause: () => void;
  resume: () => void;
  finish: () => Promise<void>;
  interrupt: () => void;
}

/** Owns one runtime-confirmed session, its clocks, and retry-safe snapshot identity. */
export function createBrowserPlayActivity(
  effects: BrowserPlayActivityEffects,
): BrowserPlayActivity {
  let active: ActivePlayActivity | null = null;
  return {
    get isActive() {
      return active !== null;
    },
    start(titleId, releaseId) {
      if (active) return;
      active = {
        sessionId: effects.createSessionId(),
        titleId,
        releaseId,
        startedAt: effects.utcNow(),
        monotonicStartedAt: effects.monotonicNow(),
        pausedAt: null,
        pausedDurationMs: 0,
      };
      void effects
        .recordSnapshot(active.sessionId, {
          clientId: 'romd.web',
          titleId: active.titleId,
          releaseId: active.releaseId,
          startedAt: active.startedAt,
        })
        .catch(() => undefined);
    },
    pause() {
      if (active && active.pausedAt === null) active.pausedAt = effects.monotonicNow();
    },
    resume() {
      if (active && active.pausedAt !== null) {
        active.pausedDurationMs += effects.monotonicNow() - active.pausedAt;
        active.pausedAt = null;
      }
    },
    async finish() {
      const finished = active;
      active = null;
      if (!finished) return;
      const endedAt = effects.utcNow();
      const elapsedUntil = finished.pausedAt ?? effects.monotonicNow();
      const activeDurationSeconds = Math.max(
        0,
        Math.floor((elapsedUntil - finished.monotonicStartedAt - finished.pausedDurationMs) / 1000),
      );
      await effects.recordSnapshot(finished.sessionId, {
        clientId: 'romd.web',
        titleId: finished.titleId,
        releaseId: finished.releaseId,
        startedAt: finished.startedAt,
        endedAt,
        activeDurationSeconds,
      });
    },
    interrupt() {
      // Keep the already-sent open snapshot as crash/interruption evidence.
      active = null;
    },
  };
}
