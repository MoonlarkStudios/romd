/** Canonical complete-catalog digest profile romd-reference-json-v1. */
export function canonicalReferenceJson(value: unknown): string {
  if (value === null) return 'null';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'number') {
    if (!Number.isSafeInteger(value)) throw new Error('Reference JSON requires safe integers.');
    return String(Object.is(value, -0) ? 0 : value);
  }
  if (typeof value === 'string') {
    // JSON.stringify emits the profile's short escapes and lowercase C0 escapes, but would
    // accept lone surrogates. Those are not valid Unicode and must never enter a digest.
    for (let index = 0; index < value.length; index += 1) {
      const unit = value.charCodeAt(index);
      if (unit >= 0xd800 && unit <= 0xdbff) {
        const next = value.charCodeAt(index + 1);
        if (!(next >= 0xdc00 && next <= 0xdfff)) throw new Error('Invalid reference Unicode.');
        index += 1;
      } else if (unit >= 0xdc00 && unit <= 0xdfff) {
        throw new Error('Invalid reference Unicode.');
      }
    }
    return JSON.stringify(value);
  }
  if (Array.isArray(value)) return `[${Array.from(value, canonicalReferenceJson).join(',')}]`;
  if (typeof value === 'object' && Object.getPrototypeOf(value) === Object.prototype) {
    // JavaScript's default string sort compares ordinal UTF-16 code units, including astral keys.
    return `{${Object.keys(value)
      .sort()
      .map(
        (key) =>
          `${canonicalReferenceJson(key)}:${canonicalReferenceJson(Reflect.get(value, key))}`,
      )
      .join(',')}}`;
  }
  throw new Error('Invalid reference JSON value.');
}

export async function referenceContentHash(value: unknown): Promise<string> {
  const bytes = new TextEncoder().encode(canonicalReferenceJson(value));
  const hash = await crypto.subtle.digest('SHA-256', bytes);
  return Array.from(new Uint8Array(hash), (value) => value.toString(16).padStart(2, '0')).join('');
}
