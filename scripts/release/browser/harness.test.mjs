import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { assetResponseFailure, Diagnostics, requiredResource } from './diagnostics.mjs';
import { cartridge, crc32, fixture } from './fixture.mjs';
import { ensureFixture, installFault, qualifyBrowser } from './run.mjs';
import { hash } from './diagnostics.mjs';
import { assertPersistedState, persistenceKeys } from './persistence.mjs';

const origins = { admin: 'https://admin.example.test', consumer: 'https://console.example.test', player: 'https://play.example.test' };
const request = (type, path) => ({ resourceType: () => type, url: () => origins.admin + path });
const response = (type, path, status, contentType) => ({ request: () => request(type, path),
  status: () => status, headers: () => ({ 'content-type': contentType }) });
const pageResult = (items, nextCursor = null) => ({ items, nextCursor, hasNextPage: nextCursor !== null });

test('missing scripts, SPA fallback scripts and broken runtime/config requests fail', () => {
  assert.equal(assetResponseFailure(response('script', '/assets/app.js', 503, 'text/plain')), 'required-response-failed');
  assert.equal(assetResponseFailure(response('script', '/assets/app.js', 200, 'text/html')), 'script-content-type');
  assert.equal(assetResponseFailure(response('script', '/loader.js', 302, 'text/html')), null);
  assert.equal(assetResponseFailure(response('script', '/loader.js', 200, 'text/javascript')), null);
  for (const path of ['/player-config.json', '/api/playback/config', '/delivery/content/secret-grant', '/cores/fceumm-wasm.data']) {
    assert.equal(requiredResource(request('fetch', path)), true);
    assert.equal(assetResponseFailure(response('fetch', path, 404, 'application/json')), 'required-response-failed');
  }
});

test('handshake and startup events alone cannot claim browser qualification', () => {
  const diagnostic = new Diagnostics(origins);
  for (const event of [{ type: 'hello', protocol: 2 }, { type: 'event', event: 'ready' }, { type: 'event', event: 'game-started' }]) {
    diagnostic.protocol(event, origins.player);
  }
  assert.equal(diagnostic.checks.playerHandshake, true);
  assert.equal(diagnostic.result(), false);
  for (const key of ['adminCallback', 'adminAuthenticatedView', 'consumerCallback', 'consumerAuthenticatedView', 'consumerPlaying', 'playerBodyPlaying']) {
    diagnostic.checks[key] = true;
  }
  assert.equal(diagnostic.result(), true);
  diagnostic.record('unhandled-browser-error', {}, true);
  assert.equal(diagnostic.result(), false);
});

test('a different frame cannot satisfy the isolated player handshake', () => {
  const diagnostic = new Diagnostics(origins);
  diagnostic.protocol({ type: 'hello', protocol: 2 }, origins.consumer);
  assert.equal(diagnostic.checks.playerHandshake, undefined);
  diagnostic.protocol({ type: 'hello', protocol: 99 }, origins.player);
  assert.equal(diagnostic.checks.playerHandshake, undefined);
});

test('broken portal and actual loader fault injection use failing HTTP responses', async () => {
  for (const injectFault of ['portal-asset', 'player-startup']) {
    const routes = [];
    await installFault({ route: async (pattern, callback) => routes.push({ pattern, callback }) }, {
      adminOrigin: origins.admin, injectFault,
    });
    assert.equal(routes.length, 1);
    let fulfilled;
    await routes[0].callback({ fulfill: async (value) => { fulfilled = value; } });
    assert.equal(fulfilled.status, 503);
    if (injectFault === 'player-startup') assert.match('https://cdn.example/data/loader.js', routes[0].pattern);
  }
});

test('failure evidence persists and never includes credential, OAuth or signed-delivery payloads', async (context) => {
  const work = await mkdtemp(join(tmpdir(), 'romd-browser-diagnostics-'));
  context.after(() => rm(work, { recursive: true, force: true }));
  const secrets = ['private-password', 'private-access-token', 'private-oauth-code', 'private-content-grant'];
  const passed = await qualifyBrowser({ adminOrigin: origins.admin, consumerOrigin: origins.consumer,
    playerOrigin: origins.player, evidenceDir: join(work, 'new-run'),
    hostAddress: '127.23.42.8', certificateSpki: 'a'.repeat(43) + '=', password: secrets[0] }, {
    launch: async () => { throw new Error(secrets.join(' ')); },
  });
  assert.equal(passed, false);
  const evidence = await readFile(join(work, 'new-run/browser-diagnostics.json'), 'utf8');
  for (const secret of secrets) assert.ok(!evidence.includes(secret));
  assert.equal(JSON.parse(evidence).records[0].stage, 'launch-browser');
  const diagnostic = new Diagnostics(origins);
  diagnostic.resourceFailure('required-response-failed', `https://user:${secrets[0]}@console.example.test/delivery/content/${secrets[3]}?code=${secrets[2]}`);
  for (const secret of secrets) assert.ok(!JSON.stringify(diagnostic.summary()).includes(secret));
});

test('synthetic fixture is original valid NROM with deterministic hashes and vectors', () => {
  assert.equal(crc32(Buffer.from('123456789')), 'cbf43926');
  const rom = cartridge();
  assert.deepEqual([...rom.subarray(0, 6)], [0x4e, 0x45, 0x53, 0x1a, 1, 1]);
  assert.equal(rom.length, 16 + 16 * 1024 + 8 * 1024);
  assert.equal(rom.readUInt16LE(16 + 0x3ffc), 0x8000);
  assert.match(fixture().dat.toString(), new RegExp(`crc="${crc32(rom)}"`));
  assert.equal(fixture().sha256, fixture().sha256);
});

test('restart/restore refuses missing fixtures and never recreates them', async () => {
  const requests = [];
  const page = { evaluate: async (_evaluate, { path, options }) => {
    requests.push({ path, options });
    return { status: 200, json: new URL(path, origins.admin).pathname === '/api/libraries' ? pageResult([]) : { id: 'persisted-admin' } };
  } };
  await assert.rejects(ensureFixture(page, { allowProvision: false, expectedState: {} }), /Persisted synthetic library is missing/);
  assert.equal(requests.filter(({ options }) => options.method === 'POST' || options.method === 'PUT').length, 0);
});

test('restart/restore refuses a reset account or recreated library despite matching names', async () => {
  const page = { evaluate: async (_evaluate, { path }) => ({ status: 200,
    json: new URL(path, origins.admin).pathname === '/api/libraries'
      ? pageResult([{ id: 'new-library', name: 'ROMD Browser Qualification' }]) : { id: 'new-admin' },
  }) };
  await assert.rejects(ensureFixture(page, { allowProvision: false,
    expectedState: { librarySha256: hash('original-library'), adminUserSha256: hash('original-admin') },
  }), /identity changed/);
});

test('restart/restore finds a persisted fixture on a later Page and reads the canonical self resource', async () => {
  const requests = [];
  const expectedState = { librarySha256: hash('persisted-library'), adminUserSha256: hash('persisted-admin') };
  const page = { evaluate: async (_evaluate, { path, options }) => {
    requests.push({ path, options });
    const url = new URL(path, origins.admin);
    assert.equal(options.method ?? 'GET', 'GET');
    if (url.pathname === '/api/me') return { status: 200, json: { id: 'persisted-admin' } };
    assert.equal(url.pathname, '/api/libraries');
    assert.equal(url.searchParams.get('limit'), '100');
    if (!url.searchParams.has('cursor')) return { status: 200, json: pageResult([], 'opaque+/= cursor') };
    assert.equal(url.searchParams.get('cursor'), 'opaque+/= cursor');
    return { status: 200, json: pageResult([{ id: 'persisted-library', name: 'ROMD Browser Qualification' }]) };
  } };
  assert.deepEqual(await ensureFixture(page, { allowProvision: false, expectedState }), expectedState);
  assert.equal(requests.length, 3);
});

test('restart/restore refuses duplicate fixture names discovered after the first Page', async () => {
  const requests = [];
  const page = { evaluate: async (_evaluate, { path, options }) => {
    requests.push({ path, options });
    const url = new URL(path, origins.admin);
    return { status: 200, json: url.pathname === '/api/libraries'
      ? pageResult([{ id: url.searchParams.has('cursor') ? 'second' : 'first', name: 'ROMD Browser Qualification' }],
        url.searchParams.has('cursor') ? null : 'next') : { id: 'persisted-admin' } };
  } };
  await assert.rejects(ensureFixture(page, { allowProvision: false, expectedState: {} }), /missing or ambiguous/);
  assert.equal(requests.filter(({ path }) => new URL(path, origins.admin).pathname === '/api/libraries').length, 2);
  assert.ok(requests.every(({ options }) => (options.method ?? 'GET') === 'GET'));
});

test('invalid or repeated fixture Pages fail closed before provisioning or accepting partial identity', async () => {
  const target = { id: 'persisted-library', name: 'ROMD Browser Qualification' };
  for (const pages of [
    [{ items: [target], hasNextPage: true, nextCursor: null }],
    [{ items: [target], hasNextPage: false, nextCursor: 'unexpected' }],
    [{ items: [target], hasNextPage: 'false', nextCursor: null }],
    [{ items: null, hasNextPage: false, nextCursor: null }],
    [pageResult([target], 'repeat'), pageResult([], 'repeat')],
  ]) {
    const requests = [];
    const page = { evaluate: async (_evaluate, { path, options }) => {
      requests.push({ path, options });
      assert.equal(new URL(path, origins.admin).pathname, '/api/libraries');
      return { status: 200, json: pages.shift() };
    } };
    await assert.rejects(ensureFixture(page, { allowProvision: true }), /page|cursor|continuation/i);
    assert.ok(requests.every(({ options }) => (options.method ?? 'GET') === 'GET'));
  }
});

test('fresh provisioning finds the reference system on a later Page before importing', async () => {
  const requests = [];
  const page = { evaluate: async (_evaluate, { path, options }) => {
    requests.push({ path, options });
    const url = new URL(path, origins.admin);
    if ((options.method ?? 'GET') !== 'GET') throw new Error('Stop after the reference system lookup');
    if (url.pathname === '/api/me') return { status: 200, json: { id: 'fresh-admin' } };
    if (url.pathname === '/api/libraries') return { status: 200, json: pageResult([]) };
    assert.equal(url.pathname, '/api/systems');
    assert.equal(url.searchParams.get('limit'), '100');
    return { status: 200, json: url.searchParams.has('cursor')
      ? pageResult([{ key: 'nes', name: 'Nintendo Entertainment System' }])
      : pageResult([{ key: 'gc', name: 'Nintendo GameCube' }], 'next') };
  } };
  await assert.rejects(ensureFixture(page, { allowProvision: true }), /Stop after the reference system lookup/);
  const upload = new URL(requests.at(-1).path, origins.admin);
  assert.equal(upload.pathname, '/api/upload/dat');
  assert.equal(upload.searchParams.get('systemKey'), 'nes');
  assert.equal(requests.filter(({ path }) => new URL(path, origins.admin).pathname === '/api/systems').length, 2);
});

test('a failed later fixture Page cannot be mistaken for a fresh deployment', async () => {
  const requests = [];
  const page = { evaluate: async (_evaluate, { path, options }) => {
    requests.push({ path, options });
    const url = new URL(path, origins.admin);
    assert.equal(url.pathname, '/api/libraries');
    return url.searchParams.has('cursor') ? { status: 503, json: { code: 'Unavailable' } }
      : { status: 200, json: pageResult([], 'next') };
  } };
  await assert.rejects(ensureFixture(page, { allowProvision: true }), /Fixture API failed \(503\)/);
  assert.equal(requests.length, 2);
  assert.ok(requests.every(({ options }) => (options.method ?? 'GET') === 'GET'));
});

test('browser sign-in observes canonical admin and consumer self responses before authenticated views', async (context) => {
  const work = await mkdtemp(join(tmpdir(), 'romd-browser-auth-'));
  context.after(() => rm(work, { recursive: true, force: true }));
  const authenticatedPaths = [];
  let count = 0;
  const newPage = () => {
    const origin = count++ === 0 ? origins.admin : origins.consumer;
    let navigated;
    const frame = { url: () => `${origin}/auth/callback?code=private-code` };
    return {
      on: (event, listener) => { if (event === 'framenavigated') navigated = listener; },
      mainFrame: () => frame,
      goto: async () => {}, close: async () => {},
      getByRole: () => ({ click: async () => { navigated(frame); }, waitFor: async () => {} }),
      getByLabel: () => ({ fill: async () => {} }),
      waitForURL: async () => {},
      waitForResponse: async (predicate) => {
        const reply = (path) => ({ url: () => origin + path, status: () => 200 });
        assert.equal(predicate(reply('/api/me')), true);
        assert.equal(predicate(reply('/api/auth/me')), false);
        authenticatedPaths.push(origin + '/api/me');
      },
      evaluate: async (_evaluate, argument) => {
        if (origin === origins.consumer) throw new Error('Stop after actual sign-in orchestration');
        const url = new URL(argument.path, origin);
        return { status: 200, json: url.pathname === '/api/libraries'
          ? pageResult([{ id: 'library', name: 'ROMD Browser Qualification' }]) : { id: 'admin' } };
      },
    };
  };
  const browserContext = { setDefaultTimeout: () => {}, exposeBinding: async () => {}, addInitScript: async () => {},
    on: () => {}, newPage: async () => newPage() };
  const passed = await qualifyBrowser({ adminOrigin: origins.admin, consumerOrigin: origins.consumer,
    playerOrigin: origins.player, evidenceDir: join(work, 'run'), hostAddress: '127.23.42.8',
    certificateSpki: 'a'.repeat(43) + '=', allowProvision: false,
    expectedState: { librarySha256: hash('library'), adminUserSha256: hash('admin') },
  }, { launch: async () => ({ version: () => 'fixture', newContext: async () => browserContext, close: async () => {} }) });
  assert.equal(passed, false, 'the fixture deliberately stops before real playback');
  assert.deepEqual(authenticatedPaths, [origins.admin + '/api/me', origins.consumer + '/api/me']);
  const diagnostics = JSON.parse(await readFile(join(work, 'run/browser-diagnostics.json'), 'utf8'));
  assert.equal(diagnostics.checks.adminAuthenticatedView, true);
  assert.equal(diagnostics.checks.consumerAuthenticatedView, true);
  assert.equal(diagnostics.records.at(-1).stage, 'isolated-player-startup');
});

test('lost server identity, release identity or fixture bytes fail persisted-state comparison', () => {
  const expected = Object.fromEntries(persistenceKeys.map((key) => [key, hash(key)]));
  assertPersistedState({ ...expected }, expected);
  for (const key of persistenceKeys) {
    assert.throws(() => assertPersistedState({ ...expected, [key]: hash('replacement') }, expected), /identity changed/);
  }
  assert.throws(() => assertPersistedState({}, expected), /incomplete/);
});
