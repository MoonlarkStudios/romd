export const localSystemKeyPrefix = 'local-';
export const localSystemKeySuffixMaxLength = 50 - localSystemKeyPrefix.length;

export function validLocalSystemKeySuffix(suffix: string) {
  return (
    suffix.length <= localSystemKeySuffixMaxLength && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(suffix)
  );
}

/** Derives a URL-safe system key from a display name. */
export function suggestSystemKey(name: string): string {
  return `${localSystemKeyPrefix}${name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, localSystemKeySuffixMaxLength)
    .replace(/-+$/g, '')}`;
}
