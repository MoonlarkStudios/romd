import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { assetResponseFailure, Diagnostics, hash, requiredResource } from './diagnostics.mjs';
import { fixture, fixtureName } from './fixture.mjs';
import { assertPersistedState } from './persistence.mjs';

// These observations do not acknowledge a handshake, supply ROMs to the player,
// replace the runtime, or generate startup events. The native postMessage runs
// unchanged; only the three public protocol facts are copied to diagnostics.
function observeBrowser() {
  const report = (event) => { void window.romdQualificationObservation(event); };
  window.addEventListener('securitypolicyviolation', (event) => {
    report({ kind: 'csp', directive: event.effectiveDirective });
  });
  const post = MessagePort.prototype.postMessage;
  MessagePort.prototype.postMessage = function (message, ...rest) {
    const result = post.call(this, message, ...rest);
    if (message?.type === 'hello') report({ kind: 'protocol', type: 'hello', protocol: message.protocol });
    if (message?.type === 'event' && ['ready', 'game-started', 'error'].includes(message.event)) {
      report({ kind: 'protocol', type: 'event', event: message.event });
    }
    return result;
  };
}

export async function installFault(context, config, diagnostics) {
  if (config.injectFault === 'portal-asset') {
    await context.route(`${config.adminOrigin}/assets/**`, async (route) => {
      diagnostics?.record('injected-fault', { fault: 'portal-asset' });
      await route.fulfill({ status: 503, contentType: 'text/plain', body: 'Disposable qualification asset fault' });
    });
  } else if (config.injectFault === 'player-startup') {
    // Break the real runtime loader, after the real consumer/player handshake.
    await context.route(/\/loader\.js(?:\?.*)?$/, async (route) => {
      diagnostics?.record('injected-fault', { fault: 'player-startup' });
      await route.fulfill({ status: 503, contentType: 'text/plain', body: 'Disposable qualification startup fault' });
    });
  }
}

async function signIn(page, surface, config, diagnostics) {
  const origin = config[`${surface}Origin`];
  const profilePath = '/api/me';
  let callback = false;
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) {
      const url = new URL(frame.url());
      if (url.origin === origin && url.pathname === '/auth/callback' && url.searchParams.has('code')) callback = true;
    }
  });
  await page.goto(`${origin}/login`, { waitUntil: 'networkidle' });
  await page.getByRole('button', { name: /^sign in$/i }).click();
  await page.getByLabel('Email or username').fill(config.email);
  await page.getByLabel('Password', { exact: true }).fill(config.password);
  const authenticated = page.waitForResponse((response) => {
    const url = new URL(response.url());
    return url.origin === origin && url.pathname === profilePath && response.status() === 200;
  });
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await authenticated;
  diagnostics.checks[`${surface}Profile`] = true;
  await page.waitForURL((url) => url.origin === origin && url.pathname !== '/login'
    && !url.pathname.startsWith('/auth/') && !url.pathname.startsWith('/connect/'));
  if (!callback) throw new Error('Browser authorization callback was not observed');
  diagnostics.checks[`${surface}Callback`] = true;
  await page.getByRole('button', { name: surface === 'admin' ? 'User menu' : 'Account', exact: true }).waitFor();
  diagnostics.checks[`${surface}AuthenticatedView`] = true;
}

async function api(page, path, options = {}) {
  const response = await page.evaluate(async ({ path, options }) => {
    const entry = Object.keys(sessionStorage).find((key) => key.startsWith(`oidc.user:${location.origin}:`));
    const token = entry ? JSON.parse(sessionStorage.getItem(entry)).access_token : null;
    if (!token) throw new Error('Browser session missing');
    const headers = { Authorization: `Bearer ${token}` };
    let body;
    if (options.upload) {
      body = new FormData();
      const bytes = Uint8Array.from(atob(options.upload.base64), (character) => character.charCodeAt(0));
      body.append('file', new File([bytes], options.upload.name));
    } else if (options.json !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(options.json);
    }
    const result = await fetch(path, { method: options.method ?? 'GET', headers, body });
    return { status: result.status, json: result.headers.get('content-type')?.includes('json') ? await result.json() : null };
  }, { path, options });
  if (!options.allowStatus && (response.status < 200 || response.status >= 300)) throw new Error(`Fixture API failed (${response.status})`);
  return response;
}

async function poll(action, done, label, timeout = 180_000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const result = await action();
    if (done(result)) return result;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`${label} timed out`);
}

async function pagedItems(page, path) {
  const items = [];
  const seen = new Set();
  let cursor;
  while (true) {
    const query = new URLSearchParams({ limit: '100', ...(cursor === undefined ? {} : { cursor }) });
    const response = await api(page, `${path}?${query}`);
    const result = response.json;
    if (!result || !Array.isArray(result.items) || !result.items.every((item) => item && typeof item === 'object' && !Array.isArray(item))
        || typeof result.hasNextPage !== 'boolean' || !Object.hasOwn(result, 'nextCursor')) {
      throw new Error('Fixture API returned an invalid page');
    }
    const next = result.nextCursor;
    if (result.hasNextPage) {
      if (typeof next !== 'string' || !next || seen.has(next)) throw new Error('Fixture API returned an invalid or repeated next cursor');
      seen.add(next);
    } else if (next !== null) {
      throw new Error('Fixture API returned a cursor after its final page');
    }
    items.push(...result.items);
    if (!result.hasNextPage) return items;
    cursor = next;
  }
}

export async function ensureFixture(page, config) {
  const existing = await pagedItems(page, '/api/libraries');
  const matching = existing.filter((library) => library.name === fixtureName);
  const me = await api(page, '/api/me');
  if (!config.allowProvision) {
    if (matching.length !== 1) throw new Error('Persisted synthetic library is missing or ambiguous');
    const state = { librarySha256: hash(matching[0].id), adminUserSha256: hash(me.json.id) };
    if (!config.expectedState || state.librarySha256 !== config.expectedState.librarySha256
        || state.adminUserSha256 !== config.expectedState.adminUserSha256) {
      throw new Error('Persisted synthetic library or account identity changed');
    }
    return state;
  }
  if (matching.length) throw new Error('Fresh disposable deployment unexpectedly contains the qualification library');
  const systems = await pagedItems(page, '/api/systems');
  const system = systems.find((candidate) => candidate.key === 'nes');
  if (!system) throw new Error('NES reference system is missing');
  const synthetic = fixture();
  for (const upload of [{ name: 'qualification.dat', data: synthetic.dat,
    path: `/api/upload/dat?systemKey=${encodeURIComponent(system.key)}`, counter: 'datsSucceeded' },
  { name: 'qualification.nes', data: synthetic.rom, path: '/api/upload/rom', counter: 'romsIngested' }]) {
    const started = await api(page, upload.path, { method: 'POST', upload: { name: upload.name, base64: upload.data.toString('base64') } });
    if (started.status !== 202 || !started.json.jobId) throw new Error('Fixture import was not queued');
    const finished = await poll(() => api(page, `/api/jobs/${started.json.jobId}`),
      (result) => result.json.isTerminal, 'Fixture import', 300_000);
    if (finished.json.hasErrors || finished.json[upload.counter] !== 1) throw new Error('Fixture import failed');
  }
  const library = await api(page, '/api/libraries', { method: 'POST', json: { name: fixtureName,
    configuration: { includeUntrackedTitles: true, contentRatingPolicy: { unknownRatingPolicy: 'Allow' } } } });
  const id = library.json.id;
  if (!id) throw new Error('Fixture library was not created');
  await api(page, `/api/libraries/${id}/materialize`, { method: 'POST' });
  await poll(() => api(page, `/api/libraries/${id}`), (result) => Boolean(result.json.lastMaterializedAt)
    && result.json.needsMaterialization === false && result.json.itemCount === 1, 'Synthetic library publication');
  // Assignment invalidates this admin session. All admin operations precede it.
  await api(page, `/api/users/${me.json.id}/library`, { method: 'PUT', json: { libraryId: id } });
  return { librarySha256: hash(id), adminUserSha256: hash(me.json.id) };
}

async function play(page, config, diagnostics, state) {
  const identity = await api(page, '/api/server/identity');
  if (typeof identity.json.instanceId !== 'string') throw new Error('Server instance identity is missing');
  const catalog = await poll(() => api(page, `/api/catalog?query=${encodeURIComponent(fixtureName)}`, { allowStatus: true }),
    (response) => response.status === 200 && response.json.items?.length === 1, 'Published synthetic library');
  const title = catalog.json.items[0];
  await page.goto(`${config.consumerOrigin}/titles/${encodeURIComponent(title.id)}`, { waitUntil: 'networkidle' });
  await page.getByRole('link', { name: 'Play Now', exact: true }).click();
  await page.waitForURL(/\/titles\/[^/]+\/releases\/[^/]+\/play$/);
  const releaseId = new URL(page.url()).pathname.split('/')[4];
  const manifest = await api(page, `/api/releases/${encodeURIComponent(releaseId)}/manifest`, { method: 'POST' });
  const item = manifest.json.items?.[0];
  const expectedHash = fixture().sha256;
  if (!manifest.json.isComplete || manifest.json.items.length !== 1 || !item.isAvailable
      || item.sha256?.toLowerCase() !== expectedHash || manifest.json.serverInstanceId !== identity.json.instanceId
      || manifest.json.titleId !== title.id || manifest.json.releaseId !== releaseId || !item.contentGrant?.downloadUrl) {
    throw new Error('Published fixture manifest does not match the original synthetic content');
  }
  // Read the actual granted content too. Never retain its signed URL or response.
  const downloadedHash = await page.evaluate(async (url) => {
    const response = await fetch(url, { credentials: 'omit', referrerPolicy: 'no-referrer' });
    if (!response.ok) throw new Error('Persisted fixture content is unavailable');
    const bytes = await response.arrayBuffer();
    const digest = await crypto.subtle.digest('SHA-256', bytes);
    return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
  }, item.contentGrant.downloadUrl);
  if (downloadedHash !== expectedHash) throw new Error('Persisted fixture bytes changed');
  const persisted = { ...state, serverInstanceSha256: hash(identity.json.instanceId),
    titleSha256: hash(title.id), releaseSha256: hash(releaseId), contentSha256: downloadedHash };
  assertPersistedState(persisted, config.expectedState);
  diagnostics.persistedState = persisted;
  const frame = page.frameLocator('iframe[title$="browser player"]');
  await frame.getByRole('button', { name: 'Start game', exact: true }).click({ timeout: 90_000 });
  await frame.locator('body[data-playing="true"]').waitFor({ timeout: 90_000 });
  diagnostics.checks.playerBodyPlaying = true;
  await page.locator('[data-testid="player-launch"][data-playing="true"]').waitFor();
  await page.getByText('In session', { exact: true }).waitFor();
  diagnostics.checks.consumerPlaying = true;
}

export async function qualifyBrowser(config, chromium) {
  const origins = { admin: config.adminOrigin, consumer: config.consumerOrigin, player: config.playerOrigin };
  const diagnostics = new Diagnostics(origins);
  await mkdir(config.evidenceDir, { recursive: false, mode: 0o700 });
  let browser;
  let browserVersion;
  let stage = 'launch-browser';
  try {
    if (!/^[A-Za-z0-9+/]{43}=$/.test(config.certificateSpki ?? '')) throw new Error('Pinned disposable certificate is required');
    if (!/^127\.\d+\.\d+\.\d+$/.test(config.hostAddress ?? '')) throw new Error('Disposable loopback address is required');
    browser = await chromium.launch({ headless: true, args: [
      `--ignore-certificate-errors-spki-list=${config.certificateSpki}`, '--no-proxy-server',
      '--host-resolver-rules=' + Object.values(origins).map((origin) => `MAP ${new URL(origin).hostname} ${config.hostAddress}`).join(', '),
    ] });
    browserVersion = browser.version();
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    context.setDefaultTimeout(45_000);
    await context.exposeBinding('romdQualificationObservation', (source, event) => {
      if (event?.kind === 'csp') diagnostics.record('csp-violation', {
        directive: /^[a-z-]+$/.test(event.directive) ? event.directive : 'unknown',
      }, true);
      if (event?.kind === 'protocol') diagnostics.protocol(event, source.frame.url());
    });
    await context.addInitScript(observeBrowser);
    await installFault(context, config, diagnostics);
    context.on('page', (page) => {
      page.on('pageerror', (error) => diagnostics.record('unhandled-browser-error', { messageSha256: hash(error.message) }, true));
      page.on('console', (message) => {
        if (message.type() === 'error') diagnostics.record('console-error', { messageSha256: hash(message.text()) }, true);
      });
    });
    context.on('response', (response) => {
      const failure = assetResponseFailure(response);
      if (failure) diagnostics.resourceFailure(failure, response.url(), { status: response.status() });
    });
    context.on('requestfailed', (request) => {
      if (requiredResource(request)) diagnostics.resourceFailure('required-request-failed', request.url());
    });
    const admin = await context.newPage();
    stage = 'admin-browser-sign-in';
    await signIn(admin, 'admin', config, diagnostics);
    stage = 'synthetic-content-provisioning';
    const state = await ensureFixture(admin, config);
    await admin.close();
    const consumer = await context.newPage();
    // Headless Chromium denies permission prompts. Approve only the real
    // player's screen wake lock within this disposable browser context. Keep
    // the CDP session attached: detaching it revokes its permission override.
    const permissions = await context.newCDPSession(consumer);
    const { targetInfo } = await permissions.send('Target.getTargetInfo');
    if (!targetInfo.browserContextId) throw new Error('Disposable browser context is required');
    await permissions.send('Browser.setPermission', {
      permission: { name: 'screen-wake-lock' }, setting: 'granted',
      origin: config.consumerOrigin, embeddedOrigin: config.playerOrigin,
      browserContextId: targetInfo.browserContextId,
    });
    stage = 'consumer-browser-sign-in';
    await signIn(consumer, 'consumer', config, diagnostics);
    stage = 'isolated-player-startup';
    await play(consumer, config, diagnostics, state);
    // Let the actual postMessage observations settle before evaluating checks.
    await consumer.waitForFunction(() => document.querySelector('[data-testid="player-launch"]')?.dataset.playing === 'true');
    // Deliberate browser teardown is outside the tested startup window. It can
    // cancel in-flight requests; those cancellations are not server failures.
    context.removeAllListeners('response');
    context.removeAllListeners('requestfailed');
    for (const page of context.pages()) {
      page.removeAllListeners('pageerror');
      page.removeAllListeners('console');
    }
    await context.close();
  } catch (error) {
    diagnostics.record('qualification-stage-failed', { stage, messageSha256: hash(error.message) }, true);
  } finally {
    if (browser) {
      try { await browser.close(); }
      catch (error) { diagnostics.record('browser-cleanup-failed', { messageSha256: hash(error.message) }, true); }
    }
    const summary = diagnostics.summary();
    summary.nodeVersion = process.version;
    summary.browserVersion = browserVersion;
    summary.playwrightVersion = JSON.parse(await readFile(new URL('./package.json', import.meta.url), 'utf8')).dependencies.playwright;
    summary.injectedFault = config.injectFault ?? 'none';
    summary.persistedState = diagnostics.persistedState;
    await writeFile(`${config.evidenceDir}/browser-diagnostics.json`, JSON.stringify(summary, null, 2) + '\n', { mode: 0o600 });
  }
  return diagnostics.result();
}

async function main() {
  if (process.argv.length !== 4 || process.argv[2] !== '--config') throw new Error('usage: node run.mjs --config <private-input.json>');
  const config = JSON.parse(await readFile(process.argv[3], 'utf8'));
  const { chromium } = await import('playwright');
  const passed = await qualifyBrowser(config, chromium);
  console.log(`Packaged browser qualification: ${passed ? 'passed' : 'failed'} (sanitized diagnostics retained)`);
  process.exitCode = passed ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(() => { console.error('Packaged browser runner failed before qualification'); process.exitCode = 1; });
}
