import { EmulatorJsShutdownAdapter, SAVE_FLUSH_GRACE_MS, SHUTDOWN_FALLBACK_MS } from './shutdown';

describe('EmulatorJsShutdownAdapter', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('invokes the pinned exit event once and waits for the save-flush grace period', () => {
    const onComplete = vi.fn();
    const onFailure = vi.fn();
    const adapter = new EmulatorJsShutdownAdapter({
      emulatorJsVersion: '4.2.3',
      setTimeout: window.setTimeout.bind(window),
      clearTimeout: window.clearTimeout.bind(window),
      onComplete,
      onFailure,
    });
    const emulator = {
      callEvent: vi.fn(() => {
        adapter.observeExit();
        return 2;
      }),
    };

    adapter.request(emulator);
    adapter.request(emulator);
    expect(emulator.callEvent).toHaveBeenCalledTimes(1);
    expect(emulator.callEvent).toHaveBeenCalledWith('exit');
    expect(onComplete).not.toHaveBeenCalled();

    vi.advanceTimersByTime(SAVE_FLUSH_GRACE_MS - 1);
    expect(onComplete).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);

    expect(onComplete).toHaveBeenCalledOnce();
    expect(onFailure).not.toHaveBeenCalled();
  });

  it('completes immediately when shutdown occurs before EmulatorJS exists', () => {
    const onComplete = vi.fn();
    const adapter = new EmulatorJsShutdownAdapter({
      emulatorJsVersion: '4.2.3',
      setTimeout: window.setTimeout.bind(window),
      clearTimeout: window.clearTimeout.bind(window),
      onComplete,
      onFailure: vi.fn(),
    });

    adapter.request(undefined);
    adapter.request(undefined);

    expect(onComplete).toHaveBeenCalledOnce();
  });

  it('fails construction when the pinned EmulatorJS version changes', () => {
    expect(
      () =>
        new EmulatorJsShutdownAdapter({
          emulatorJsVersion: '4.2.4',
          setTimeout: window.setTimeout.bind(window),
          clearTimeout: window.clearTimeout.bind(window),
          onComplete: vi.fn(),
          onFailure: vi.fn(),
        }),
    ).toThrow('has not been reviewed for 4.2.4');
  });

  it('reports failure without acknowledging success when no exit listener is registered', () => {
    const onComplete = vi.fn();
    const onFailure = vi.fn();
    const adapter = createAdapter(onComplete, onFailure);

    adapter.request({
      callEvent: vi.fn(() => 0),
    });

    expect(onFailure).toHaveBeenCalledOnce();
    expect(onFailure).toHaveBeenCalledWith('EmulatorJS has no registered pinned exit handler.');
    expect(onComplete).not.toHaveBeenCalled();
  });

  it('reports failure without acknowledging success when the exit call throws', () => {
    const onComplete = vi.fn();
    const onFailure = vi.fn();
    const adapter = createAdapter(onComplete, onFailure);

    adapter.request({
      callEvent: vi.fn(() => {
        throw new Error('exit exploded');
      }),
    });

    expect(onFailure).toHaveBeenCalledWith('exit exploded');
    expect(onComplete).not.toHaveBeenCalled();
  });

  it('uses the bounded fallback when the exit event is never observed', () => {
    const onComplete = vi.fn();
    const onFailure = vi.fn();
    const adapter = createAdapter(onComplete, onFailure);

    adapter.request({
      callEvent: vi.fn(() => 1),
    });
    vi.advanceTimersByTime(SHUTDOWN_FALLBACK_MS - 1);
    expect(onComplete).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);

    expect(onFailure).toHaveBeenCalledWith(
      'EmulatorJS did not finish its pinned exit path in time.',
    );
    expect(onComplete).not.toHaveBeenCalled();
  });

  it('retains the save mount through native exit and acknowledges only after IndexedDB commits', () => {
    const onComplete = vi.fn();
    const onFailure = vi.fn();
    const adapter = createAdapter(onComplete, onFailure);
    let flush!: (error?: unknown) => void;
    const nativeUnmount = vi.fn();
    const fs = {
      unmount: nativeUnmount,
      syncfs: vi.fn((_populate: boolean, callback: (error?: unknown) => void) => {
        flush = callback;
      }),
    };
    const emulator = {
      started: true,
      gameManager: {
        FS: fs,
      },
      callEvent: vi.fn(() => {
        adapter.observeExit(emulator);
        // Exactly the ordering of the pinned GameManager exit handler.
        fs.unmount('/data/saves');
        fs.unmount('/another-mount');
        return 2;
      }),
    };
    adapter.request(emulator);
    expect(nativeUnmount).toHaveBeenCalledExactlyOnceWith('/another-mount');
    vi.advanceTimersByTime(SAVE_FLUSH_GRACE_MS);
    expect(fs.syncfs).toHaveBeenCalledExactlyOnceWith(false, expect.any(Function));
    expect(onComplete).not.toHaveBeenCalled();
    flush();
    expect(onComplete).toHaveBeenCalledOnce();
    expect(onFailure).not.toHaveBeenCalled();
    fs.unmount('/data/saves');
    expect(nativeUnmount).toHaveBeenCalledTimes(1);
  });

  it('never acknowledges a save transaction failure as a successful exit', () => {
    const onComplete = vi.fn();
    const onFailure = vi.fn();
    const adapter = createAdapter(onComplete, onFailure);
    const emulator = {
      started: true,
      gameManager: {
        FS: {
          unmount: vi.fn(),
          syncfs: vi.fn((_populate: boolean, callback: (error?: unknown) => void) =>
            callback(new Error('quota exceeded')),
          ),
        },
      },
      callEvent: vi.fn(() => {
        adapter.observeExit(emulator);
        return 2;
      }),
    };
    adapter.request(emulator);
    vi.advanceTimersByTime(SHUTDOWN_FALLBACK_MS);
    expect(onFailure).toHaveBeenCalledExactlyOnceWith(
      'Browser saves could not be persisted: quota exceeded',
    );
    expect(onComplete).not.toHaveBeenCalled();
  });
});

function createAdapter(
  onComplete: () => void,
  onFailure: (message: string) => void,
): EmulatorJsShutdownAdapter {
  return new EmulatorJsShutdownAdapter({
    emulatorJsVersion: '4.2.3',
    setTimeout: window.setTimeout.bind(window),
    clearTimeout: window.clearTimeout.bind(window),
    onComplete,
    onFailure,
  });
}
