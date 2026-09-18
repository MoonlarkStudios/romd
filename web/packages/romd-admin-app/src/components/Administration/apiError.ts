export function apiError(error: unknown, fallback: string): string {
  if (typeof error === 'object' && error !== null && 'detail' in error && typeof error.detail === 'string') return error.detail;
  if (error instanceof Error) return error.message;
  return fallback;
}
