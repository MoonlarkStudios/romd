import { describe, expect, it } from 'vitest';
import { artworkJobStatus } from './artworkJobStatus';

describe('Artwork job status', () => {
  it('reports completed retries as success despite historical errors', () => {
    const status = artworkJobStatus({ phase: 'Completed', isTerminal: true, hasErrors: true });
    expect(status.color).toBe('teal');
    expect(status.title).toBe('Artwork import completed');
    expect(status.message).toContain('selected artwork was imported');
    expect(status.message).not.toContain('previous selection');
  });

  it('keeps a retrying job in progress rather than reporting terminal failure', () => {
    const status = artworkJobStatus({ phase: 'Importing', isTerminal: false, hasErrors: true });
    expect(status.color).toBe('blue');
    expect(status.title).toBe('Artwork import in progress');
  });

  it('uses the failed phase even when error details are absent', () => {
    const status = artworkJobStatus({ phase: 'Failed', isTerminal: true, hasErrors: false });
    expect(status.color).toBe('red');
    expect(status.title).toBe('Artwork import failed');
    expect(status.message).toContain('previous selection was preserved');
  });

  it('explains a superseded completed job without claiming it imported the selection', () => {
    const status = artworkJobStatus({ phase: 'Completed', isTerminal: true, wasSuperseded: true });
    expect(status.title).toBe('Artwork request superseded');
    expect(status.message).toContain('newer artwork choice');
    expect(status.message).not.toContain('was imported');
  });

  it('distinguishes cancellation from failure', () => {
    expect(artworkJobStatus({ phase: 'Cancelled', isTerminal: true }).title).toBe('Artwork import cancelled');
  });
});
