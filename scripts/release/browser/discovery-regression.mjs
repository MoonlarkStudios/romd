// Real pinned-Chromium regression for deliberate navigation during playback discovery.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { chromium } from 'playwright';
import { waitForPlaybackDiscovery } from './run.mjs';
import { assetResponseFailure, Diagnostics, requiredResource } from './diagnostics.mjs';

const browser = await chromium.launch({ headless: true });
try {
  for (const mode of ['navigation-before-discovery', 'wait-before-navigation', 'http-503', 'transport-failure']) {
    let release;
    let entered;
    let releasePlayer;
    let enteredPlayer;
    const held = new Promise(resolve => { release = resolve; });
    const requested = new Promise(resolve => { entered = resolve; });
    const playerHeld = new Promise(resolve => { releasePlayer = resolve; });
    const playerRequested = new Promise(resolve => { enteredPlayer = resolve; });
    const player = createServer(async (_request, response) => {
      enteredPlayer();
      await playerHeld;
      if (response.destroyed) return;
      response.writeHead(200, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' });
      response.end('{"cores":["fceumm"]}');
    });
    await new Promise(resolve => player.listen(0, '127.0.0.1', resolve));
    const playerOrigin = `http://127.0.0.1:${player.address().port}`;
    const consumer = createServer(async (request, response) => {
      if (request.url === '/api/playback/config') {
        entered();
        await held;
        if (mode === 'transport-failure') { response.destroy(); return; }
        if (response.destroyed) return;
        response.writeHead(mode === 'http-503' ? 503 : 200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ playerOrigin }));
      } else if (request.url === '/home') {
        response.writeHead(200, { 'Content-Type': 'text/html' });
        response.end(`<body>Library<script>fetch('/api/playback/config').then(r=>r.json()).then(c=>fetch(c.playerOrigin+'/player-config.json')).catch(()=>{})</script>`);
      } else { response.writeHead(200, { 'Content-Type': 'text/html' }); response.end('<body>Title</body>'); }
    });
    await new Promise(resolve => consumer.listen(0, '127.0.0.1', resolve));
    const consumerOrigin = `http://127.0.0.1:${consumer.address().port}`;
    const context = await browser.newContext();
    const diagnostics = new Diagnostics({ consumer: consumerOrigin, player: playerOrigin });
    context.on('response', response => {
      const failure = assetResponseFailure(response);
      if (failure) diagnostics.resourceFailure(failure, response.url(), { status: response.status() });
    });
    context.on('requestfailed', request => {
      if (requiredResource(request)) diagnostics.resourceFailure('required-request-failed', request.url(), {
        cancelled: request.failure()?.errorText === 'net::ERR_ABORTED',
      });
    });
    try {
      const page = await context.newPage();
      await page.goto(consumerOrigin + '/home', { waitUntil: 'domcontentloaded' });
      await requested;
      if (mode === 'navigation-before-discovery') {
        const aborted = page.waitForEvent('requestfailed', request => request.url().endsWith('/api/playback/config'));
        await page.goto(consumerOrigin + '/title');
        await aborted;
        release();
        assert.equal(diagnostics.failures, 1);
        assert.equal(diagnostics.records[0].cancelled, true);
      } else {
        const boundedPage = { waitForFunction: (predicate, endpoints) => page.waitForFunction(predicate, endpoints, { timeout: mode === 'transport-failure' ? 1000 : 15000 }) };
        const ready = waitForPlaybackDiscovery(boundedPage, { consumerOrigin, playerOrigin });
        let completed = false;
        ready.then(() => { completed = true; }, () => {});
        release();
        if (mode === 'transport-failure') {
          await assert.rejects(ready, /Timeout/);
          assert.ok(diagnostics.failures > 0);
        } else {
          await playerRequested;
          // Let Chromium poll the barrier while the second transfer is held.
          // Completing only the portal request must not permit navigation.
          await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
          assert.equal(completed, false);
          releasePlayer();
          await ready;
          await page.goto(consumerOrigin + '/title');
          assert.equal(diagnostics.failures, mode === 'http-503' ? 1 : 0);
        }
      }
      console.log(JSON.stringify({ mode, failures: diagnostics.failures, records: diagnostics.records }));
    } finally {
      release();
      releasePlayer();
      context.removeAllListeners('response');
      context.removeAllListeners('requestfailed');
      await context.close();
      consumer.closeAllConnections(); player.closeAllConnections();
      await Promise.all([new Promise(resolve => consumer.close(resolve)), new Promise(resolve => player.close(resolve))]);
    }
  }
} finally { await browser.close(); }
