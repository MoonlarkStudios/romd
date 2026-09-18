export function formatBytes(value: string | number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB'];
  const bytes = typeof value === 'number' ? BigInt(value) : BigInt(value);
  if (bytes < 0n) return '—';
  if (bytes === 0n) return '0 B';

  let exponent = 0;
  let divisor = 1n;
  while (exponent < units.length - 1 && bytes >= divisor * 1024n) {
    exponent++;
    divisor *= 1024n;
  }

  const roundedTenths = (bytes * 10n + divisor / 2n) / divisor;
  const whole = roundedTenths / 10n;
  const tenth = roundedTenths % 10n;
  const amount = tenth === 0n ? whole.toString() : `${whole}.${tenth}`;
  return `${amount} ${units[exponent]}`;
}

export function formatCount(value: number | null | undefined): string {
  if (value === null || value === undefined) {
    return '0';
  }

  return new Intl.NumberFormat().format(value);
}

export function formatRating(value: number | null | undefined): string | null {
  if (value === null || value === undefined) {
    return null;
  }

  if (!Number.isFinite(value)) {
    return null;
  }

  return new Intl.NumberFormat(undefined, {
    maximumFractionDigits: 1,
    minimumFractionDigits: Number.isInteger(value) ? 0 : 1,
  }).format(value);
}
