import { describe, expect, it } from 'vitest';
import { computeTransferStats } from './uploadWithProgress';

describe('computeTransferStats', () => {
  it('reports zero rate and infinite ETA before progress is measurable', () => {
    expect(computeTransferStats(0, 1000, 0)).toEqual({ bytesPerSec: 0, etaSeconds: Infinity });
    expect(computeTransferStats(0, 1000, 5)).toEqual({ bytesPerSec: 0, etaSeconds: Infinity });
  });

  it('computes the running-average rate and ETA', () => {
    // 500 bytes in 2s = 250 B/s; 500 bytes remaining => 2s ETA.
    expect(computeTransferStats(500, 1000, 2)).toEqual({ bytesPerSec: 250, etaSeconds: 2 });
  });

  it('clamps remaining bytes at zero when complete', () => {
    const stats = computeTransferStats(1000, 1000, 4);
    expect(stats.bytesPerSec).toBe(250);
    expect(stats.etaSeconds).toBe(0);
  });
});
