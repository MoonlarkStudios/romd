import { describe, expect, it } from 'vitest';

// Media formats and their labels, platform keys and stored-format extensions are server policy. App code
// renders what the server sends; these exact allow-lists name the remaining copies, and an entry that no
// longer matches fails.
const guardedLiterals = [
  'rvz',
  'chd',
  'RVZ',
  'CHD',
  'gc',
  'wii',
  'ps2',
  'psx',
  '.rvz',
  '.chd',
];

const allowed: Record<string, number> = {};

const sources = import.meta.glob<string>(
  [
    './**/*.{ts,tsx}',
    '!./**/*.test.{ts,tsx}',
    '!./test/**',
  ],
  {
    query: '?raw',
    import: 'default',
    eager: true,
  },
);

function guardedOccurrences(files: Record<string, string>): Record<string, number> {
  const pattern = new RegExp(
    `(['"\`])(${guardedLiterals.map((literal) => literal.replace('.', '\\.')).join('|')})\\1`,
    'g',
  );
  const found: Record<string, number> = {};
  for (const [path, source] of Object.entries(files)) {
    for (const match of source.matchAll(pattern)) {
      const key = `${path.replace(/^\.\//, '')} '${match[2]}'`;
      found[key] = (found[key] ?? 0) + 1;
    }
  }
  return found;
}

describe('media policy literals', () => {
  it('appear only in their allowed places', () => {
    expect(guardedOccurrences(sources)).toEqual(allowed);
  });

  it('are found in every quote style and only as whole strings', () => {
    expect(
      guardedOccurrences({
        './a.tsx': `format === 'rvz'; key === "ps2"; ext = \`.chd\`; label = 'CHD'; copy = 'RVZ where available'; name = 'chdman'; 'wii-u'`,
      }),
    ).toEqual({
      "a.tsx 'rvz'": 1,
      "a.tsx 'ps2'": 1,
      "a.tsx '.chd'": 1,
      "a.tsx 'CHD'": 1,
    });
  });
});
