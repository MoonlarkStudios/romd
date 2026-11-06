export const SAVE_FLUSH_GRACE_MS = 1_250;
export const SHUTDOWN_FALLBACK_MS = 3_000;
export const PINNED_SHUTDOWN_EMULATORJS_VERSION = '4.2.3';

export interface EmulatorJsExitApi {
  callEvent(event: 'exit'): number;
  started?: boolean;
  gameManager?: {
    FS: {
      unmount(path: string): void;
      syncfs(populate: boolean, callback: (error?: unknown) => void): void;
    };
  };
}

interface ShutdownDependencies {
  emulatorJsVersion: string;
  setTimeout: typeof window.setTimeout;
  clearTimeout: typeof window.clearTimeout;
  onComplete: () => void;
  onFailure: (message: string) => void;
}

export class EmulatorJsShutdownAdapter {
  private requested = false;
  private exitObserved = false;
  private completed = false;
  private graceTimer: number | undefined;
  private fallbackTimer: number | undefined;
  private flushTimer: number | undefined;
  private graceElapsed = false;
  private saveFlushed = false;

  constructor(private readonly dependencies: ShutdownDependencies) {
    if (dependencies.emulatorJsVersion !== PINNED_SHUTDOWN_EMULATORJS_VERSION) {
      throw new Error(
        `The EmulatorJS shutdown adapter has not been reviewed for ${dependencies.emulatorJsVersion}.`,
      );
    }
  }

  request(emulator: EmulatorJsExitApi | undefined): void {
    if (this.requested || this.completed) {
      return;
    }
    this.requested = true;

    if (!emulator) {
      this.complete();
      return;
    }

    this.startFallback();

    try {
      const listenerCount = emulator.callEvent('exit');
      if (listenerCount === 0) {
        this.fail('EmulatorJS has no registered pinned exit handler.');
      }
    } catch (error) {
      this.fail(toErrorMessage(error));
    }
  }

  observeExit(emulator?: EmulatorJsExitApi): void {
    if (this.exitObserved || this.completed) {
      return;
    }
    this.exitObserved = true;
    this.requested = true;
    this.startFallback();

    // Our ready-time exit listener precedes GameManager's listener in 4.2.3.
    // That listener saves twice and immediately unmounts IDBFS. A queued
    // autoPersist then sees an empty mountpoint and deletes persisted saves.
    // Retain the mount for this frame's lifetime, including late autoPersist,
    // and explicitly await a flush after the native exit listener has returned.
    try {
      const fs = emulator?.gameManager?.FS;
      if (fs) {
        const unmount = fs.unmount;
        fs.unmount = (path) => {
          if (path !== '/data/saves') unmount.call(fs, path);
        };
        this.flushTimer = this.dependencies.setTimeout(() => {
          if (this.completed) return;
          try {
            fs.syncfs(false, (error) => {
              if (error)
                this.fail(`Browser saves could not be persisted: ${toErrorMessage(error)}`);
              else {
                this.saveFlushed = true;
                this.completeIfSaved();
              }
            });
          } catch (error) {
            this.fail(toErrorMessage(error));
          }
        }, 0);
      } else if (emulator?.started) {
        this.fail('The running emulator did not expose its save filesystem.');
        return;
      } else {
        this.saveFlushed = true;
      }
    } catch (error) {
      this.fail(toErrorMessage(error));
      return;
    }
    this.graceTimer = this.dependencies.setTimeout(() => {
      this.graceElapsed = true;
      this.completeIfSaved();
    }, SAVE_FLUSH_GRACE_MS);
  }

  private startFallback(): void {
    if (this.fallbackTimer !== undefined) return;
    this.fallbackTimer = this.dependencies.setTimeout(() => {
      this.fail('EmulatorJS did not finish its pinned exit path in time.');
    }, SHUTDOWN_FALLBACK_MS);
  }

  private completeIfSaved(): void {
    if (this.graceElapsed && this.saveFlushed) this.complete();
  }

  private fail(message: string): void {
    if (this.completed) return;
    this.completed = true;
    this.clearTimers();
    this.dependencies.onFailure(message);
  }

  private complete(): void {
    if (this.completed) {
      return;
    }
    this.completed = true;
    this.clearTimers();
    this.dependencies.onComplete();
  }

  private clearTimers(): void {
    if (this.graceTimer !== undefined) {
      this.dependencies.clearTimeout(this.graceTimer);
    }
    if (this.fallbackTimer !== undefined) {
      this.dependencies.clearTimeout(this.fallbackTimer);
    }
    if (this.flushTimer !== undefined) this.dependencies.clearTimeout(this.flushTimer);
  }
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'EmulatorJS exit failed.';
}
