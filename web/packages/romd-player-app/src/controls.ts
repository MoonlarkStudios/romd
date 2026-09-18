import type { PlayerControlAction } from '@romd/player-protocol';
import { enhanceControlPanel } from './controlPanel';
import { PINNED_SHUTDOWN_EMULATORJS_VERSION } from './shutdown';

interface RuntimeControls {
  paused: boolean;
  pause(): void;
  play(): void;
  controlMenu: HTMLElement;
  elements: { bottomBar: { gamepad: HTMLElement[] } };
}

interface ControlTimers {
  setTimeout: typeof window.setTimeout;
  clearTimeout: typeof window.clearTimeout;
}

/** Adapter for the pinned 4.2.3 runtime. Native pause has no lifecycle event.
 * Observe its authoritative flag without replacing emulator methods, so native
 * toolbar, keyboard and portal actions all report the same state. */
export function createSessionControls(
  runtime: unknown,
  version: string,
  timers: ControlTimers,
  onPaused: (paused: boolean) => void,
  onUnavailable: () => void,
): { control(action: PlayerControlAction): void; dispose(): void } | null {
  if (version !== PINNED_SHUTDOWN_EMULATORJS_VERSION || !isRuntimeControls(runtime)) return null;
  const cleanPanel = enhanceControlPanel(runtime.controlMenu, runtime.elements.bottomBar.gamepad[0]);
  let disposed = false;
  let lastPaused = runtime.paused;
  let timer: number | undefined;
  onPaused(lastPaused);
  const report = () => {
    if (runtime.paused !== lastPaused) {
      lastPaused = runtime.paused;
      onPaused(lastPaused);
    }
  };
  const dispose = () => {
    disposed = true;
    cleanPanel();
    if (timer !== undefined) timers.clearTimeout(timer);
  };
  const watch = () => {
    if (disposed) return;
    report();
    timer = timers.setTimeout(watch, 200);
  };
  timer = timers.setTimeout(watch, 200);
  return {
    dispose,
    control(action) {
      if (disposed) return;
      try {
        if (action === 'resume') {
          // Closing the native popup matches its own Close button, without
          // changing mappings or restoring defaults.
          runtime.controlMenu.style.display = 'none';
          runtime.play();
          runtime.controlMenu.ownerDocument.querySelector<HTMLElement>('#game')?.focus();
        } else {
          runtime.pause();
          if (action === 'open-controls') {
            runtime.elements.bottomBar.gamepad[0].click();
            runtime.controlMenu.querySelector<HTMLElement>('button, select, input, [tabindex]')?.focus();
          }
        }
        report();
      } catch {
        dispose();
        onUnavailable();
      }
    },
  };
}

function isRuntimeControls(value: unknown): value is RuntimeControls {
  if (!value || typeof value !== 'object') return false;
  const runtime = value as Partial<RuntimeControls>;
  return typeof runtime.paused === 'boolean' && typeof runtime.pause === 'function'
    && typeof runtime.play === 'function' && runtime.controlMenu instanceof HTMLElement
    && runtime.elements?.bottomBar?.gamepad?.[0] instanceof HTMLElement;
}
