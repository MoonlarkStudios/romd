import { QueryClient } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createStatsInvalidationScheduler, STATS_REFRESH_INTERVAL } from './statsInvalidation';

describe('statistics invalidation scheduler', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('coalesces each query separately and refreshes periodically during a continuous burst', () => {
    const client = new QueryClient();
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const scheduler = createStatsInvalidationScheduler(client);
    for (let index = 0; index < 12; index++) {
      scheduler.invalidate(['coverage']);
      scheduler.invalidate(['storage']);
      vi.advanceTimersByTime(1_000);
    }
    expect(invalidate).toHaveBeenCalledTimes(4);
    vi.advanceTimersByTime(STATS_REFRESH_INTERVAL);
    expect(invalidate).toHaveBeenCalledTimes(6);
    vi.advanceTimersByTime(STATS_REFRESH_INTERVAL);
    expect(invalidate).toHaveBeenCalledTimes(6);
    scheduler.dispose();
  });

  it('retains a trailing refresh while an earlier aggregate is still running', async () => {
    const client = new QueryClient();
    let finish: (value: number) => void = () => {};
    const running = client.fetchQuery({
      queryKey: ['coverage'],
      queryFn: () => new Promise<number>((resolve) => { finish = resolve; }),
    });
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const scheduler = createStatsInvalidationScheduler(client);
    scheduler.invalidate(['coverage']);
    vi.advanceTimersByTime(STATS_REFRESH_INTERVAL * 3);
    expect(invalidate).not.toHaveBeenCalled();
    finish(1);
    await running;
    vi.advanceTimersByTime(STATS_REFRESH_INTERVAL);
    expect(invalidate).toHaveBeenCalledExactlyOnceWith(
      { queryKey: ['coverage'] }, { cancelRefetch: false },
    );
    scheduler.dispose();
  });

  it('cancels timers on disposal but leaves pending queries stale for remount', () => {
    const client = new QueryClient();
    client.setQueryData(['coverage'], 1);
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    const scheduler = createStatsInvalidationScheduler(client);
    scheduler.invalidate(['coverage']);
    scheduler.dispose();
    expect(invalidate).toHaveBeenCalledExactlyOnceWith({ queryKey: ['coverage'], refetchType: 'none' });
    expect(client.getQueryState(['coverage'])?.isInvalidated).toBe(true);
    scheduler.invalidate(['coverage']);
    vi.advanceTimersByTime(STATS_REFRESH_INTERVAL);
    expect(invalidate).toHaveBeenCalledTimes(1);
  });
});
