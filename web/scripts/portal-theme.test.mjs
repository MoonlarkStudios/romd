import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';

const bootstrap = readFileSync(
  new URL('../packages/romd-foundation/src/portal-theme.js', import.meta.url),
  'utf8',
);

function initialScheme({ portal = 'admin', stored, systemDark = false, storageBlocked = false }) {
  const attributes = {};
  runInNewContext(bootstrap, {
    document: {
      documentElement: {
        dataset: {
          romdPortal: portal,
        },
        setAttribute: (name, value) => {
          attributes[name] = value;
        },
      },
    },
    window: {
      localStorage: {
        getItem: () => {
          if (storageBlocked) throw new Error('Storage unavailable');
          return stored;
        },
      },
      matchMedia: () => ({
        matches: systemDark,
      }),
    },
  });
  return attributes['data-mantine-color-scheme'];
}

test('admin first paint keeps the saved light or dark preference over the system', () => {
  assert.equal(
    initialScheme({
      stored: 'light',
      systemDark: true,
    }),
    'light',
  );
  assert.equal(
    initialScheme({
      stored: 'dark',
      systemDark: false,
    }),
    'dark',
  );
});

test('an explicit auto preference follows the system', () => {
  for (const stored of [
    'auto',
  ]) {
    assert.equal(
      initialScheme({
        stored,
        systemDark: true,
      }),
      'dark',
    );
    assert.equal(
      initialScheme({
        stored,
        systemDark: false,
      }),
      'light',
    );
  }
});

test('missing, invalid and blocked preferences use the dark default', () => {
  for (const stored of [
    null,
    'invalid',
  ]) {
    assert.equal(
      initialScheme({
        stored,
        systemDark: false,
      }),
      'dark',
    );
  }
  assert.equal(
    initialScheme({
      storageBlocked: true,
      systemDark: false,
    }),
    'dark',
  );
});

test('consumer redirects stay dark even when admin has a saved light preference', () => {
  assert.equal(
    initialScheme({
      portal: 'consumer',
      stored: 'light',
    }),
    'dark',
  );
});
