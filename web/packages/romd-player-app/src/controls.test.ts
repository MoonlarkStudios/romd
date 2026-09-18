import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createSessionControls } from './controls';

function runtimeFixture() {
  const controlMenu = document.createElement('div');
  controlMenu.innerHTML = '<button>Close</button>';
  controlMenu.style.display = 'none';
  const open = document.createElement('button');
  open.onclick = () => { controlMenu.style.display = ''; };
  const runtime = {
    paused: false,
    pause() { runtime.paused = true; },
    play() { runtime.paused = false; },
    controlMenu,
    elements: { bottomBar: { gamepad: [open] } },
  };
  document.body.innerHTML = '<div id="game" tabindex="-1"></div>';
  document.body.append(controlMenu);
  return runtime;
}

describe('pinned session controls', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());
  const timers = () => ({ setTimeout: window.setTimeout.bind(window), clearTimeout: window.clearTimeout.bind(window) });

  it('pauses before opening native settings and resumes with game focus', () => {
    const runtime = runtimeFixture();
    const paused = vi.fn();
    const adapter = createSessionControls(runtime, '4.2.3', timers(), paused, vi.fn());
    adapter?.control('open-controls');
    expect(runtime.paused).toBe(true);
    expect(runtime.controlMenu.style.display).toBe('');
    expect(document.activeElement).toBe(runtime.controlMenu.querySelector('button'));
    expect(paused).toHaveBeenLastCalledWith(true);
    adapter?.control('resume');
    expect(runtime.paused).toBe(false);
    expect(runtime.controlMenu.style.display).toBe('none');
    expect(document.activeElement?.id).toBe('game');
    expect(paused).toHaveBeenLastCalledWith(false);
    adapter?.dispose();
  });

  it('observes native state changes, deduplicates them and stops observing on disposal', () => {
    const runtime = runtimeFixture();
    const paused = vi.fn();
    const adapter = createSessionControls(runtime, '4.2.3', timers(), paused, vi.fn());
    runtime.pause();
    vi.advanceTimersByTime(600);
    expect(paused.mock.calls).toEqual([[false], [true]]);
    runtime.play();
    vi.advanceTimersByTime(200);
    expect(paused.mock.calls).toEqual([[false], [true], [false]]);
    adapter?.dispose();
    runtime.pause();
    vi.advanceTimersByTime(1000);
    expect(paused).toHaveBeenCalledTimes(3);
  });

  it('fails closed for incompatible runtimes and disables only controls on command failure', () => {
    expect(createSessionControls({}, '4.2.3', timers(), vi.fn(), vi.fn())).toBeNull();
    expect(createSessionControls(runtimeFixture(), '4.3.0', timers(), vi.fn(), vi.fn())).toBeNull();
    const runtime = runtimeFixture();
    runtime.pause = () => { throw new Error('runtime failure'); };
    const unavailable = vi.fn();
    const adapter = createSessionControls(runtime, '4.2.3', timers(), vi.fn(), unavailable);
    adapter?.control('pause');
    adapter?.control('pause');
    expect(unavailable).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });
});
