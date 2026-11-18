/** Derives a URL-safe system key from a display name. */
export function suggestSystemKey(name: string): string {
  return `local-${name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 44)
    .replace(/-+$/g, '')}`;
}
