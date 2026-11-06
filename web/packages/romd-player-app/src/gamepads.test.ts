import { describe, expect, it, vi } from 'vitest';
import { repairGamepads } from './gamepads';

interface Event {
  gamepadIndex: number;
  label?: string;
  value?: number;
}
function fixture(
  pads = [
    {
      id: '8BitDo Pro',
      index: 3,
    },
  ],
) {
  const input = vi.fn();
  const runtime = {
    gamepadSelection: [
      '',
      '',
      '',
      '',
    ],
    gamepad: {
      gamepads: pads,
      listeners: {} as Record<string, (event: Event) => void>,
      terminate: vi.fn(),
      loop: vi.fn(),
    },
  };
  // Match 4.2.3: input and connected callbacks index the dense cache directly;
  // disconnect instead searches for the original browser slot.
  runtime.gamepad.listeners.connected = (event) => {
    const pad = runtime.gamepad.gamepads[event.gamepadIndex];
    const seat = runtime.gamepadSelection.indexOf('');
    if (seat !== -1) runtime.gamepadSelection[seat] = `${pad.id}_${pad.index}`;
  };
  for (const name of [
    'buttondown',
    'buttonup',
    'axischanged',
  ]) {
    runtime.gamepad.listeners[name] = (event) => {
      const pad = runtime.gamepad.gamepads[event.gamepadIndex];
      input(runtime.gamepadSelection.indexOf(`${pad.id}_${pad.index}`), event.label, event.value);
    };
  }
  runtime.gamepad.listeners.disconnected = (event) => {
    const pad = runtime.gamepad.gamepads.find((p) => p.index === event.gamepadIndex);
    const seat = runtime.gamepadSelection.indexOf(`${pad?.id}_${pad?.index}`);
    if (seat !== -1) runtime.gamepadSelection[seat] = '';
  };
  return {
    runtime,
    input,
  };
}

describe('EmulatorJS 4.2.3 gamepad repair', () => {
  it('assigns preconnected controllers and restarts a stalled poll loop', () => {
    const { runtime } = fixture();
    runtime.gamepad.loop.mockImplementation(() => {
      expect(runtime.gamepadSelection[0]).toBe('8BitDo Pro_3');
    });
    const dispose = repairGamepads(runtime, '4.2.3');
    expect(runtime.gamepadSelection).toEqual([
      '8BitDo Pro_3',
      '',
      '',
      '',
    ]);
    expect(runtime.gamepad.terminate).toHaveBeenCalledOnce();
    expect(runtime.gamepad.loop).toHaveBeenCalledOnce();
    dispose?.();
    expect(runtime.gamepad.terminate).toHaveBeenCalledTimes(2);
  });

  it('routes presses, releases and axes while preserving assignments and payloads', () => {
    const { runtime, input } = fixture([
      {
        id: 'First',
        index: 1,
      },
      {
        id: '8BitDo Pro',
        index: 5,
      },
    ]);
    runtime.gamepadSelection[2] = '8BitDo Pro_5';
    repairGamepads(runtime, '4.2.3');
    expect(runtime.gamepadSelection).toEqual([
      'First_1',
      '',
      '8BitDo Pro_5',
      '',
    ]);
    runtime.gamepad.listeners.buttondown({
      gamepadIndex: 5,
      label: 'BUTTON_1',
    });
    runtime.gamepad.listeners.buttonup({
      gamepadIndex: 5,
      label: 'BUTTON_1',
    });
    runtime.gamepad.listeners.axischanged({
      gamepadIndex: 1,
      label: 'LEFT_STICK_X:+1',
      value: 0.8,
    });
    runtime.gamepad.listeners.buttondown({
      gamepadIndex: 99,
    });
    expect(input.mock.calls).toEqual([
      [
        2,
        'BUTTON_1',
        undefined,
      ],
      [
        2,
        'BUTTON_1',
        undefined,
      ],
      [
        0,
        'LEFT_STICK_X:+1',
        0.8,
      ],
    ]);
  });

  it('handles reconnects with new slots without moving another player', () => {
    const { runtime, input } = fixture([
      {
        id: 'First',
        index: 0,
      },
      {
        id: 'Second',
        index: 2,
      },
    ]);
    const originalDisconnect = runtime.gamepad.listeners.disconnected;
    repairGamepads(runtime, '4.2.3');
    expect(runtime.gamepad.listeners.disconnected).toBe(originalDisconnect);
    runtime.gamepad.listeners.disconnected({
      gamepadIndex: 0,
    });
    runtime.gamepad.gamepads.shift();
    runtime.gamepad.listeners.buttondown({
      gamepadIndex: 2,
      label: 'START',
    });
    runtime.gamepad.gamepads.push({
      id: 'First',
      index: 4,
    });
    runtime.gamepad.listeners.connected({
      gamepadIndex: 4,
    });
    runtime.gamepad.listeners.buttondown({
      gamepadIndex: 4,
      label: 'SELECT',
    });
    expect(runtime.gamepadSelection).toEqual([
      'First_4',
      'Second_2',
      '',
      '',
    ]);
    expect(input.mock.calls).toEqual([
      [
        1,
        'START',
        undefined,
      ],
      [
        0,
        'SELECT',
        undefined,
      ],
    ]);
  });

  it('preserves manual unassignment and restores listeners on disposal', () => {
    const { runtime } = fixture();
    const original = runtime.gamepad.listeners.buttondown;
    const dispose = repairGamepads(runtime, '4.2.3');
    runtime.gamepadSelection[0] = '';
    runtime.gamepad.listeners.buttondown({
      gamepadIndex: 3,
    });
    expect(runtime.gamepadSelection).toEqual([
      '',
      '',
      '',
      '',
    ]);
    dispose?.();
    expect(runtime.gamepad.listeners.buttondown).toBe(original);
  });

  it('leaves unsupported versions and runtime shapes untouched', () => {
    const { runtime } = fixture();
    expect(repairGamepads(runtime, '4.3.0')).toBeNull();
    expect(repairGamepads({}, '4.2.3')).toBeNull();
    expect(runtime.gamepad.terminate).not.toHaveBeenCalled();
  });
});
