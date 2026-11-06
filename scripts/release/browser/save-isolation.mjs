// Opt-in real-browser regression. Uses production bridges, pinned CDN cores,
// an original synthetic ROM and a disposable browser profile; no server needed.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, rm } from 'node:fs/promises';
import { createServer as httpServer } from 'node:http';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { chromium } from 'playwright';
import { cartridge } from './fixture.mjs';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const requirePlayer = createRequire(resolve(root, 'web/packages/romd-player-app/package.json'));
const { createServer } = await import(pathToFileURL(requirePlayer.resolve('vite')));
const { PLAYER_PROTOCOL_VERSION } = await import(pathToFileURL(resolve(root, 'web/packages/romd-player-protocol/dist/index.js')));
const rom = cartridge();
rom[6] = 2; // Battery-backed, original NROM; increment SRAM byte zero on boot.
Buffer.from([0x78, 0xd8, 0xa2, 0xff, 0x9a, 0xad, 0x00, 0x60, 0x18, 0x69, 0x01,
  0x8d, 0x00, 0x60, 0x4c, 0x0e, 0x80]).copy(rom, 16);
const contentSha256 = createHash('sha256').update(rom).digest('hex');
const serverInstanceId = '11111111-1111-4111-8111-111111111111';
const profile = await mkdtemp(join(tmpdir(), 'romd-save-isolation-'));
let playerOrigin;
let context;
let vite;
const parent = httpServer((_request, response) => {
  response.setHeader('Content-Type', 'text/html');
  response.end(`<!doctype html><body><iframe title="Synthetic browser player"
    allow="autoplay; gamepad; fullscreen; screen-wake-lock" style="width:900px;height:600px"></iframe>
    <script type="module">
      import {createPlayerBridge} from '${playerOrigin}/@fs${root}web/packages/romd-consumer-app/src/services/playerBridge.ts';
      import {createBrowserSaveId} from '${playerOrigin}/@fs${root}web/packages/romd-consumer-app/src/services/browserSaveIdentity.ts';
      window.start = async (owner, recoverOlderSaves = false) => {
        const frame = document.querySelector('iframe');
        const saveId = await createBrowserSaveId('${serverInstanceId}', owner, '${contentSha256}');
        const blob = new Blob([Uint8Array.from(atob('${rom.toString('base64')}'), c => c.charCodeAt(0))]);
        const capability = {playerOrigin:'${playerOrigin}', emulatorJs:{version:'4.2.3',source:'cdn',dataPath:'https://cdn.emulatorjs.org/4.2.3/data/'},cores:['fceumm']};
        window.bridge = createPlayerBridge(frame, capability,
          {core:'fceumm',displayName:'Synthetic save isolation',saveId,recoverOlderSaves,
            verifiedFile:{blob,sha256:'${contentSha256}',relativePath:'fixture.nes'}},
          event => {window.lastEvent = event;});
        // Surface bridge rejection as a test failure, including before readiness.
        window.bridge.ready.catch(error => {window.bridgeError = error.message;});
        frame.src = '${playerOrigin}/';
        return 'romd-v2-' + saveId;
      };
    </script>`);
});

try {
  await new Promise((resolve, reject) => { parent.once('error', reject); parent.listen(0, '127.0.0.1', resolve); });
  const parentOrigin = `http://localhost:${parent.address().port}`;
  vite = await createServer({ configFile: false, root: resolve(root, 'web/packages/romd-player-app'),
    resolve: { alias: { '@romd/player-protocol': resolve(root, 'web/packages/romd-player-protocol/src/index.ts') } },
    server: { host: '127.0.0.1', port: 0, hmr: false, watch: null, fs: { allow: [root] } },
    plugins: [{ name: 'save-isolation-config', configureServer(server) {
      server.middlewares.use((request, response, next) => {
        if (request.url !== '/player-config.json') return next();
        response.setHeader('Content-Type', 'application/json');
        response.end(JSON.stringify({ schemaVersion: 1, protocol: PLAYER_PROTOCOL_VERSION,
          emulatorJs: { version: '4.2.3', source: 'cdn', dataPath: 'https://cdn.emulatorjs.org/4.2.3/data/' },
          cores: ['fceumm'], allowedParents: [parentOrigin] }));
      });
    } }] });
  await vite.listen();
  playerOrigin = `http://localhost:${vite.httpServer.address().port}`;
  const launch = () => chromium.launchPersistentContext(profile, { headless: true,
    ...(process.env.ROMD_TEST_BROWSER_CHANNEL ? { channel: process.env.ROMD_TEST_BROWSER_CHANNEL } : {}),
    args: ['--autoplay-policy=no-user-gesture-required'] });
  context = await launch();
  const page = await context.newPage();
  page.setDefaultTimeout(45_000);
  const browserErrors = [];
  const observe = page => page.on('pageerror', error => browserErrors.push(error.message));
  observe(page);

  async function prepare(page, owner, recovery = false) {
    await page.goto(parentOrigin);
    await page.waitForFunction(() => typeof window.start === 'function');
    const name = await page.evaluate(([owner, recovery]) => window.start(owner, recovery), [owner, recovery]);
    return { name, frame: page.frameLocator('iframe'), child: () => page.frames().find(frame => frame.url().startsWith(playerOrigin)) };
  }
  async function play(page, session) {
    await session.frame.getByRole('button', { name: 'Start game', exact: true }).click();
    await page.waitForFunction(() => window.lastEvent?.event === 'game-started' || window.bridgeError);
    assert.equal(await page.evaluate(() => window.bridgeError), undefined);
    await session.child().waitForFunction(() => window.EJS_emulator?.gameManager?.getSaveFile()?.length > 0);
  }
  async function state(session, name = session.name) {
    return session.child().evaluate(async name => {
      const request = indexedDB.open('EmulatorJS-states', 1);
      request.onupgradeneeded = () => request.result.createObjectStore('states');
      const db = await new Promise(resolve => { request.onsuccess = () => resolve(request.result); });
      try {
        const get = db.transaction('states').objectStore('states').get(name + '.state');
        const value = await new Promise(resolve => { get.onsuccess = () => resolve(get.result); });
        return value ? Array.from(value) : null;
      } finally { db.close(); }
    }, name);
  }
  async function saveFile(session, path, legacy = false) {
    return session.child().evaluate(async ([path, legacy]) => {
      const request = legacy
        ? IDBFactory.prototype.open.call(indexedDB, '/data/saves')
        : indexedDB.open('/data/saves');
      const db = await new Promise(resolve => { request.onsuccess = () => resolve(request.result); });
      try {
        const get = db.transaction('FILE_DATA').objectStore('FILE_DATA').get(path);
        const value = await new Promise(resolve => { get.onsuccess = () => resolve(get.result); });
        return value?.contents ? Array.from(value.contents) : null;
      } finally { db.close(); }
    }, [path, legacy]);
  }

  // Obtain real emulator-produced state and save bytes, then seed an unowned
  // pre-fix record. All writes are confined to this disposable browser profile.
  const bootstrap = await prepare(page, 'fixture-bootstrap');
  await play(page, bootstrap);
  const legacy = await bootstrap.child().evaluate(async hash => {
    const emulator = window.EJS_emulator;
    const bytes = emulator.gameManager.getSaveFile();
    bytes[0] = 42;
    const snapshot = emulator.gameManager.getState();
    const open = name => new Promise(resolve => {
      const request = IDBFactory.prototype.open.call(indexedDB, name, 21);
      request.onupgradeneeded = () => request.result.createObjectStore('FILE_DATA').createIndex('timestamp', 'timestamp');
      request.onsuccess = () => resolve(request.result);
    });
    const db = await open('/data/saves');
    const oldPath = `/data/saves/FCEUmm/${hash}.srm`;
    const transaction = db.transaction('FILE_DATA', 'readwrite');
    transaction.objectStore('FILE_DATA').put({ timestamp: new Date(), mode: 33206, contents: bytes }, oldPath);
    await new Promise(resolve => { transaction.oncomplete = resolve; });
    db.close();
    await emulator.storage.states.put(hash + '.state', snapshot);
    return { state: Array.from(snapshot), oldPath };
  }, contentSha256);
  await page.evaluate(() => window.bridge.shutdown());

  const accountA = await prepare(page, 'account-a');
  await accountA.frame.getByRole('heading', { name: 'Older shared saves found' }).waitFor();
  assert.equal(await accountA.child().evaluate(() => window.EJS_emulator), undefined);
  const downloadPromise = page.waitForEvent('download');
  await accountA.frame.getByRole('link', { name: 'Download older save state', exact: true }).click();
  const download = await downloadPromise;
  const { readFile } = await import('node:fs/promises');
  assert.deepEqual(Array.from(await readFile(await download.path())), legacy.state);
  await accountA.frame.getByRole('button', { name: 'Copy older saves into my account' }).click();
  await play(page, accountA);
  assert.equal(await accountA.child().evaluate(() => window.EJS_emulator.gameManager.getSaveFile()[0]), 43,
    'The synthetic ROM must resume the recovered SRAM counter, then increment it on boot');
  assert.deepEqual(await state(accountA), legacy.state);
  assert.deepEqual(await state(accountA, contentSha256), legacy.state);
  const pathA = await accountA.child().evaluate(() => window.EJS_emulator.gameManager.getSaveFilePath());
  assert.ok(pathA.includes(accountA.name));
  const secondPage = await context.newPage();
  observe(secondPage);
  secondPage.setDefaultTimeout(45_000);
  const accountB = await prepare(secondPage, 'account-b');
  await accountB.frame.getByRole('heading', { name: 'Older shared saves found' }).waitFor();
  await accountB.frame.getByRole('button', { name: 'Start with separate saves' }).click();
  await play(secondPage, accountB);
  assert.notEqual(accountB.name, accountA.name);
  assert.equal(await state(accountB), null);
  const pathB = await accountB.child().evaluate(() => window.EJS_emulator.gameManager.getSaveFilePath());
  assert.notEqual(pathB, pathA);
  assert.deepEqual(await state(accountA), legacy.state);
  assert.equal(await accountB.child().evaluate(name => {
    const fs = window.EJS_emulator.gameManager.FS;
    return fs.analyzePath(`/data/saves/FCEUmm/${name}.srm`).exists;
  }, accountA.name), false);
  await accountB.child().evaluate(async () => {
    const emulator = window.EJS_emulator;
    await emulator.storage.states.put(emulator.getBaseFileName() + '.state', new Uint8Array([99, 98, 97]));
  });
  assert.deepEqual(await state(accountB), [99, 98, 97]);
  const firstSave = await saveFile(accountA, pathA);
  await accountB.child().evaluate(() => new Promise((resolve, reject) => {
    window.EJS_emulator.gameManager.FS.syncfs(false, error => error ? reject(error) : resolve());
  }));
  assert.deepEqual(await saveFile(accountA, pathA), firstSave);
  await secondPage.evaluate(() => window.bridge.shutdown());
  await page.evaluate(() => window.bridge.shutdown());
  const retainedSave = await saveFile(accountA, pathA);
  assert.ok(retainedSave);
  assert.equal((await saveFile(accountA, legacy.oldPath, true))[0], 42);
  await context.close();
  context = await launch();
  const restored = await context.newPage();
  observe(restored);
  restored.setDefaultTimeout(45_000);
  const returnedA = await prepare(restored, 'account-a');
  await play(restored, returnedA); // Existing account data does not prompt again.
  assert.equal(returnedA.name, accountA.name);
  assert.deepEqual(await state(returnedA), legacy.state);
  assert.deepEqual(await state(returnedA, accountB.name), [99, 98, 97]);
  assert.equal(await returnedA.child().evaluate(() => window.EJS_emulator.gameManager.getSaveFile()[0]), (retainedSave[0] + 1) & 255);
  assert.deepEqual(await state(returnedA, contentSha256), legacy.state);
  await restored.evaluate(() => window.bridge.shutdown());
  const recovery = await prepare(restored, 'account-a', true);
  await recovery.frame.getByRole('heading', { name: 'Older shared saves found' }).waitFor();
  assert.equal(await recovery.frame.getByRole('button', { name: 'Copy older saves into my account' }).isDisabled(), true);
  await recovery.frame.getByRole('button', { name: 'Continue with my saves' }).click();
  await play(restored, recovery);
  assert.deepEqual(await state(recovery), legacy.state);
  assert.deepEqual(browserErrors, [], 'Unexpected browser errors during save isolation');
  console.log('Save isolation passed: separate accounts and paths, explicit recovery, exact backup download, preserved originals, conflict protection and browser restart.');
} finally {
  if (context) await context.close();
  if (vite) await vite.close();
  await new Promise(resolve => parent.close(resolve));
  await rm(profile, { recursive: true, force: true });
}
