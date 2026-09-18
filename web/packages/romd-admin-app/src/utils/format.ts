const byteUnits = ['B', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB'];

export function formatBytes(value: number | string | bigint): string {
  if (typeof value === 'number') {
    if (!Number.isFinite(value) || value < 0) return '—';
    if (value === 0) return '0 B';
    const exponent = Math.min(Math.floor(Math.log(value) / Math.log(1024)), byteUnits.length - 1);
    return `${parseFloat((value / 1024 ** exponent).toFixed(1))} ${byteUnits[exponent]}`;
  }

  let bytes: bigint;
  try {
    bytes = typeof value === 'bigint' ? value : BigInt(value);
  } catch {
    return '—';
  }

  if (bytes < 0n) return '—';
  if (bytes === 0n) return '0 B';

  let exponent = 0;
  let divisor = 1n;
  while (exponent < byteUnits.length - 1 && bytes >= divisor * 1024n) {
    exponent++;
    divisor *= 1024n;
  }

  const roundedTenths = (bytes * 10n + divisor / 2n) / divisor;
  const whole = roundedTenths / 10n;
  const tenth = roundedTenths % 10n;
  const amount = tenth === 0n ? whole.toString() : `${whole}.${tenth}`;
  return `${amount} ${byteUnits[exponent]}`;
}

export function compareIntegerValues(a: string | number | bigint, b: string | number | bigint): number {
  const left = BigInt(a);
  const right = BigInt(b);
  return left < right ? -1 : left > right ? 1 : 0;
}

/** Returns a presentation-only percentage while keeping the source integers exact. */
export function integerPercentage(numerator: string | bigint, denominator: string | bigint): number {
  const top = BigInt(numerator);
  const bottom = BigInt(denominator);
  if (top <= 0n || bottom <= 0n) return 0;

  const basisPoints = (top * 10_000n) / bottom;
  return Math.min(100, Number(basisPoints) / 100);
}

/** Formats a transfer rate, e.g. "12.4 MB/s". Returns "—" for non-positive/unknown rates. */
export function formatRate(bytesPerSecond: number): string {
  if (!Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) return '—';
  return `${formatBytes(bytesPerSecond)}/s`;
}

/** Formats a short duration, e.g. "45s", "2m 13s", "1h 4m". Returns "—" when unknown. */
export function formatDuration(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return '—';
  const total = Math.round(seconds);
  if (total < 60) return `${total}s`;
  const minutes = Math.floor(total / 60);
  if (minutes < 60) return `${minutes}m ${total % 60}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}

/**
 * Formats a timestamp as a relative time string (e.g., "2 hours ago", "3 days ago").
 */
export function formatRelativeTime(timestamp: string | Date): string {
  const date = typeof timestamp === 'string' ? new Date(timestamp) : timestamp;
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffSec = Math.floor(diffMs / 1000);
  const diffMin = Math.floor(diffSec / 60);
  const diffHour = Math.floor(diffMin / 60);
  const diffDay = Math.floor(diffHour / 24);

  if (diffSec < 60) {
    return 'just now';
  }
  if (diffMin < 60) {
    return `${diffMin} minute${diffMin === 1 ? '' : 's'} ago`;
  }
  if (diffHour < 24) {
    return `${diffHour} hour${diffHour === 1 ? '' : 's'} ago`;
  }
  if (diffDay < 30) {
    return `${diffDay} day${diffDay === 1 ? '' : 's'} ago`;
  }
  // For older dates, show the actual date
  return date.toLocaleDateString();
}
