import { webcrypto } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { canonicalReferenceJson, referenceContentHash } from './referenceCatalogCanonical';

const fixture: {
  profile: string;
  vectors: {
    name: string;
    input: unknown;
    canonical: string;
    sha256: string;
  }[];
  rejections: {
    name: string;
    input: unknown;
  }[];
} = JSON.parse(readFileSync(sharedFixturePath(), 'utf8'));

function sharedFixturePath(): string {
  let directory = process.cwd();
  for (;;) {
    const path = join(directory, 'tests', 'Fixtures', 'ReferenceCatalogCanonicalization.json');
    if (existsSync(path)) return path;
    const parent = dirname(directory);
    if (parent === directory)
      throw new Error('The shared reference canonicalization fixture was not found.');
    directory = parent;
  }
}

describe('romd-reference-json-v1 shared language vectors', () => {
  it.each(fixture.vectors)('encodes and hashes $name identically', async (vector) => {
    Object.defineProperty(globalThis, 'crypto', {
      configurable: true,
      value: webcrypto,
    });
    expect(fixture.profile).toBe('romd-reference-json-v1');
    expect(canonicalReferenceJson(vector.input)).toBe(vector.canonical);
    await expect(referenceContentHash(vector.input)).resolves.toBe(vector.sha256);
  });
  it.each(fixture.rejections)('rejects $name', (vector) => {
    expect(() => canonicalReferenceJson(vector.input)).toThrow();
  });
  it.each([
    NaN,
    Infinity,
    -Infinity,
    undefined,
    new Date(),
    Array(1),
  ])('rejects non-JSON values %s', (value) => {
    expect(() => canonicalReferenceJson(value)).toThrow();
  });
  it('rejects lone surrogate property names as well as values', () => {
    expect(() =>
      canonicalReferenceJson({
        '\ud800': 1,
      }),
    ).toThrow('Unicode');
  });
});
