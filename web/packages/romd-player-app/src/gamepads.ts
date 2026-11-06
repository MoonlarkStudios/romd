import { PINNED_SHUTDOWN_EMULATORJS_VERSION } from './shutdown';

interface Pad {
  id: string;
  index: number;
}
interface PadEvent {
  gamepadIndex: number;
}
type Listener = (event: PadEvent) => void;
interface GamepadRuntime {
  gamepad: {
    gamepads: Pad[];
    listeners: Record<string, Listener>;
    terminate(): void;
    loop(): void;
  };
  gamepadSelection: string[];
}

/** 4.2.3 stores pads densely, but its connected/input callbacks use browser
 * slot numbers as array offsets. Translate only at that callback boundary;
 * polling and disconnect handling still need the original browser slots.
 * The constructor also polls before registering listeners, losing initial
 * connections. Replay those once and restart polling in case an early sparse
 * slot already threw before EJS_ready. */
export function repairGamepads(value: unknown, version: string): (() => void) | null {
  if (version !== PINNED_SHUTDOWN_EMULATORJS_VERSION || !isGamepadRuntime(value)) return null;
  const { gamepad, gamepadSelection } = value;
  const originals = new Map<string, Listener>();
  gamepad.terminate();
  for (const name of [
    'connected',
    'buttondown',
    'buttonup',
    'axischanged',
  ]) {
    const original = gamepad.listeners[name];
    originals.set(name, original);
    gamepad.listeners[name] = (event) => {
      const offset = gamepad.gamepads.findIndex((pad) => pad.index === event.gamepadIndex);
      if (offset !== -1)
        original({
          ...event,
          gamepadIndex: offset,
        });
    };
  }
  for (const pad of gamepad.gamepads) {
    if (!gamepadSelection.includes(`${pad.id}_${pad.index}`)) {
      gamepad.listeners.connected({
        gamepadIndex: pad.index,
      });
    }
  }
  gamepad.loop();
  return () => {
    gamepad.terminate();
    for (const [name, original] of originals) gamepad.listeners[name] = original;
  };
}

function isGamepadRuntime(value: unknown): value is GamepadRuntime {
  if (!value || typeof value !== 'object') return false;
  const runtime = value as Partial<GamepadRuntime>;
  return (
    Array.isArray(runtime.gamepadSelection) &&
    runtime.gamepadSelection.every((selection) => typeof selection === 'string') &&
    Array.isArray(runtime.gamepad?.gamepads) &&
    runtime.gamepad.gamepads.every(
      (pad) => pad && typeof pad.id === 'string' && Number.isInteger(pad.index),
    ) &&
    typeof runtime.gamepad.terminate === 'function' &&
    typeof runtime.gamepad.loop === 'function' &&
    [
      'connected',
      'buttondown',
      'buttonup',
      'axischanged',
    ].every((name) => typeof runtime.gamepad?.listeners?.[name] === 'function')
  );
}
