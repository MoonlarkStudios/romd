export const SAVE_FLUSH_GRACE_MS = 1_250;
export const SHUTDOWN_FALLBACK_MS = 3_000;
export const PINNED_SHUTDOWN_EMULATORJS_VERSION = '4.2.3';

export interface EmulatorJsExitApi {
  callEvent(event: 'exit'): number;
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

    this.fallbackTimer = this.dependencies.setTimeout(() => {
      this.dependencies.onFailure('EmulatorJS did not finish its pinned exit path in time.');
      this.complete();
    }, SHUTDOWN_FALLBACK_MS);

    try {
      const listenerCount = emulator.callEvent('exit');
      if (listenerCount === 0) {
        this.dependencies.onFailure('EmulatorJS has no registered pinned exit handler.');
        this.complete();
      }
    } catch (error) {
      this.dependencies.onFailure(toErrorMessage(error));
      this.complete();
    }
  }

  observeExit(): void {
    if (this.exitObserved || this.completed) {
      return;
    }
    this.exitObserved = true;
    this.requested = true;

    // EmulatorJS 4.2.3 starts its saveSaveFiles/restart/saveSaveFiles sequence
    // synchronously from callEvent('exit'), but its IndexedDB writes expose no
    // completion promise. Keep the frame alive through its one-second abort delay.
    this.graceTimer = this.dependencies.setTimeout(() => this.complete(), SAVE_FLUSH_GRACE_MS);
  }

  private complete(): void {
    if (this.completed) {
      return;
    }
    this.completed = true;
    if (this.graceTimer !== undefined) {
      this.dependencies.clearTimeout(this.graceTimer);
    }
    if (this.fallbackTimer !== undefined) {
      this.dependencies.clearTimeout(this.fallbackTimer);
    }
    this.dependencies.onComplete();
  }
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'EmulatorJS exit failed.';
}
