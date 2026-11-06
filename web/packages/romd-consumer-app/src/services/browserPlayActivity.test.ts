import { describe, expect, it, vi } from 'vitest';
import { type BrowserPlayActivityEffects, createBrowserPlayActivity } from './browserPlayActivity';

function createHarness() {
  let monotonicTime = 1000;
  let utc = '2026-11-04T12:00:00.000Z';
  let nextId = 0;
  const record = vi.fn<BrowserPlayActivityEffects['recordSnapshot']>().mockResolvedValue(undefined);
  const activity = createBrowserPlayActivity({
    recordSnapshot: record,
    utcNow: () => utc,
    monotonicNow: () => monotonicTime,
    createSessionId: () => `session-${++nextId}`,
  });
  return {
    record,
    activity,
    setTime: (value: number) => {
      monotonicTime = value;
    },
    setUtc: (value: string) => {
      utc = value;
    },
  };
}

describe('browser play activity lifecycle', () => {
  it('records one identity from runtime start through end despite duplicate events', async () => {
    const { record, activity, setTime, setUtc } = createHarness();
    activity.start('title', 'release');
    activity.start('title', 'release');
    expect(record).toHaveBeenCalledTimes(1);
    expect(activity.isActive).toBe(true);
    setTime(4999);
    setUtc('2026-11-04T12:00:04.000Z');
    await activity.finish();
    await activity.finish();
    expect(record.mock.calls).toEqual([
      [
        'session-1',
        {
          clientId: 'romd.web',
          titleId: 'title',
          releaseId: 'release',
          startedAt: '2026-11-04T12:00:00.000Z',
        },
      ],
      [
        'session-1',
        {
          clientId: 'romd.web',
          titleId: 'title',
          releaseId: 'release',
          startedAt: '2026-11-04T12:00:00.000Z',
          endedAt: '2026-11-04T12:00:04.000Z',
          activeDurationSeconds: 3,
        },
      ],
    ]);
    expect(activity.isActive).toBe(false);
  });

  it('counts only active time across repeated pause/resume notifications and a paused exit', async () => {
    const { record, activity, setTime } = createHarness();
    activity.start('title', 'release');
    setTime(3000);
    activity.pause();
    setTime(4000);
    activity.pause();
    setTime(7000);
    activity.resume();
    setTime(8000);
    activity.resume();
    setTime(10000);
    activity.pause();
    setTime(12000);
    await activity.finish();
    expect(record.mock.calls[1]?.[1].activeDurationSeconds).toBe(5);
  });

  it('preserves an interrupted open snapshot and gives a later real start a fresh identity', async () => {
    const { record, activity } = createHarness();
    await activity.finish();
    expect(record).not.toHaveBeenCalled();
    activity.start('title', 'release');
    activity.interrupt();
    await activity.finish();
    expect(record).toHaveBeenCalledTimes(1);
    expect(activity.isActive).toBe(false);
    activity.start('title', 'release');
    expect(record.mock.calls.map(([id]) => id)).toEqual([
      'session-1',
      'session-2',
    ]);
  });

  it('does not emit duplicate end snapshots when persistence fails', async () => {
    const { record, activity } = createHarness();
    activity.start('title', 'release');
    record.mockRejectedValueOnce(new Error('Offline'));
    await expect(activity.finish()).rejects.toThrow('Offline');
    expect(activity.isActive).toBe(false);
    await activity.finish();
    expect(record).toHaveBeenCalledTimes(2);
  });

  it('cannot clear a newer session when an older end snapshot finishes uploading', async () => {
    const { record, activity } = createHarness();
    activity.start('title', 'release');
    let finishUpload!: () => void;
    record.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finishUpload = resolve;
        }),
    );
    const ending = activity.finish();
    activity.start('another-title', 'another-release');
    finishUpload();
    await ending;
    expect(activity.isActive).toBe(true);
    await activity.finish();
    expect(record.mock.calls.map(([id]) => id)).toEqual([
      'session-1',
      'session-1',
      'session-2',
      'session-2',
    ]);
    expect(record.mock.calls[3]?.[1]).toMatchObject({
      titleId: 'another-title',
      releaseId: 'another-release',
    });
  });
});
