// Real Chromium controls: historical timing cannot admit a current refetch.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { chromium } from 'playwright';
import { openFixtureTitle } from './run.mjs';
import { observePlaybackDiscovery } from './playback-discovery.mjs';
import { assetResponseFailure, Diagnostics, requiredResource } from './diagnostics.mjs';
import { fixtureName } from './fixture.mjs';

const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
// The previous predicate is deliberately retained only as a failing control.
const historicalTiming = (page, config) => page.waitForFunction(endpoints => endpoints.every(endpoint =>
  performance.getEntriesByType('resource').some(entry => {
    const url = new URL(entry.name);
    return url.origin + url.pathname === endpoint && entry.responseEnd > 0;
  })), [config.consumerOrigin + '/api/playback/config', config.playerOrigin + '/player-config.json']);
const renderTurn = page => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));

const browser = await chromium.launch({ headless: true });
try {
  for (const mode of ['initial-navigation-abort', 'historical-timing-abort', 'current-transfers-and-spa', 'http-503', 'transport-failure']) {
    const consumerEntered = Array.from({ length: 4 }, deferred);
    const consumerRelease = Array.from({ length: 4 }, deferred);
    const playerEntered = Array.from({ length: 4 }, deferred);
    const playerRelease = Array.from({ length: 4 }, deferred);
    const player = createServer(async (request, response) => {
      const round = Number(new URL(request.url, 'http://fixture.test').searchParams.get('round'));
      playerEntered[round].resolve();
      await playerRelease[round].promise;
      if (response.destroyed) return;
      response.writeHead(200, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' });
      response.end('{"cores":["fceumm"]}');
    });
    await new Promise(resolve => player.listen(0, '127.0.0.1', resolve));
    const playerOrigin = `http://127.0.0.1:${player.address().port}`;
    const consumer = createServer(async (request, response) => {
      const url = new URL(request.url, 'http://fixture.test');
      if (url.pathname === '/api/playback/config') {
        const round = Number(url.searchParams.get('round'));
        consumerEntered[round].resolve();
        await consumerRelease[round].promise;
        if (round === 2 && mode === 'transport-failure') { response.destroy(); return; }
        if (response.destroyed) return;
        response.writeHead(round === 2 && mode === 'http-503' ? 503 : 200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({ playerOrigin }));
      } else if (url.pathname === '/home') {
        response.writeHead(200, { 'Content-Type': 'text/html' });
        response.end(`<body>
          <a id="title" href="/titles/synthetic-title" aria-label="${fixtureName}">Title</a>
          <a id="play" href="/titles/synthetic-title/releases/synthetic-release/play" hidden>Play ${fixtureName}</a>
          <script>
            window.documentIdentity = 'original-library-document';
            async function discover(round) {
              const origin = await fetch('/api/playback/config?round=' + round);
              if (!origin.ok) throw new Error('configuration unavailable');
              const config = await origin.json();
              const player = await fetch(config.playerOrigin + '/player-config.json?round=' + round);
              if (!player.ok) throw new Error('player configuration unavailable');
              await player.json();
              if (round === 1) document.querySelector('#play').hidden = false;
            }
            window.refetch = () => { void discover(2).catch(() => {}); };
            document.querySelector('#title').onclick = event => {
              event.preventDefault();
              history.pushState({}, '', '/titles/synthetic-title');
              document.body.innerHTML = '<a id="launch" href="/titles/synthetic-title/releases/synthetic-release/play">Play Now</a>';
              document.querySelector('#launch').onclick = event => {
                event.preventDefault();
                history.pushState({}, '', '/titles/synthetic-title/releases/synthetic-release/play');
              };
              // A new observer may refetch after the SPA changes route even
              // while the cached capability still renders an available action.
              void discover(3).catch(() => {});
            };
            void discover(1).catch(() => {});
          </script>`);
      } else { response.writeHead(200, { 'Content-Type': 'text/html' }); response.end('<body>New document</body>'); }
    });
    await new Promise(resolve => consumer.listen(0, '127.0.0.1', resolve));
    const config = { consumerOrigin: `http://127.0.0.1:${consumer.address().port}`, playerOrigin };
    const context = await browser.newContext();
    context.setDefaultTimeout(10_000);
    const diagnostics = new Diagnostics({ consumer: config.consumerOrigin, player: playerOrigin });
    const page = await context.newPage();
    const discovery = observePlaybackDiscovery(page, config);
    const documentRequests = [];
    context.on('request', request => { if (request.resourceType() === 'document') documentRequests.push(new URL(request.url()).pathname); });
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
      await page.goto(config.consumerOrigin + '/home', { waitUntil: 'domcontentloaded' });
      await consumerEntered[1].promise;
      if (mode === 'initial-navigation-abort') {
        const aborted = page.waitForEvent('requestfailed', request => new URL(request.url()).pathname === '/api/playback/config');
        await page.goto(config.consumerOrigin + '/titles/synthetic-title');
        await aborted;
        assert.equal(diagnostics.failures, 1);
        assert.equal(diagnostics.records[0].cancelled, true);
      } else {
        consumerRelease[1].resolve();
        await playerEntered[1].promise;
        playerRelease[1].resolve();
        await page.getByRole('link', { name: `Play ${fixtureName}`, exact: true }).waitFor();
        await historicalTiming(page, config);
        await page.evaluate(() => window.refetch());
        await consumerEntered[2].promise;
        if (mode === 'historical-timing-abort') {
          // Both old entries exist, so the old barrier admits an in-flight
          // newer request. Chromium reproduces the exact hosted failure class.
          await historicalTiming(page, config);
          const aborted = page.waitForEvent('requestfailed', request => new URL(request.url()).pathname === '/api/playback/config');
          await page.goto(config.consumerOrigin + '/titles/synthetic-title');
          await aborted;
          assert.equal(diagnostics.failures, 1);
          assert.equal(diagnostics.records[0].cancelled, true);
        } else {
          let complete = false;
          const navigation = openFixtureTitle(page, config, 'synthetic-title', discovery);
          navigation.then(() => { complete = true; }, () => {});
          await renderTurn(page);
          assert.equal(complete, false, 'cached UI must not admit an active consumer transfer');
          assert.equal(new URL(page.url()).pathname, '/home', 'title navigation must wait for the consumer transfer');
          consumerRelease[2].resolve();
          if (mode === 'current-transfers-and-spa') {
            await playerEntered[2].promise;
            await renderTurn(page);
            assert.equal(complete, false, 'the current player transfer must finish too');
            assert.equal(new URL(page.url()).pathname, '/home', 'title navigation must wait for the player transfer');
            playerRelease[2].resolve();
            await consumerEntered[3].promise;
            await renderTurn(page);
            assert.equal(complete, false, 'title-page refetch must finish before Play Now');
            consumerRelease[3].resolve();
            await playerEntered[3].promise;
            await renderTurn(page);
            assert.equal(complete, false, 'title-page player transfer must finish too');
            playerRelease[3].resolve();
            const launch = await navigation;
            await launch.click();
            await page.waitForURL('**/titles/synthetic-title/releases/synthetic-release/play');
            assert.equal(await page.evaluate(() => window.documentIdentity), 'original-library-document');
            assert.deepEqual(documentRequests, ['/home'], 'qualification must use real SPA links, not force a reload');
            assert.equal(diagnostics.failures, 0);
          } else {
            await assert.rejects(navigation, /Playback configuration request failed/);
            assert.equal(diagnostics.failures, 1);
            if (mode === 'http-503') assert.equal(diagnostics.records[0].status, 503);
            if (mode === 'transport-failure') assert.equal(diagnostics.records[0].cancelled, false);
          }
        }
      }
      console.log(JSON.stringify({ mode, failures: diagnostics.failures, records: diagnostics.records }));
    } finally {
      for (const gate of [...consumerRelease, ...playerRelease]) gate.resolve();
      discovery.dispose();
      context.removeAllListeners('response');
      context.removeAllListeners('requestfailed');
      await context.close();
      consumer.closeAllConnections(); player.closeAllConnections();
      await Promise.all([new Promise(resolve => consumer.close(resolve)), new Promise(resolve => player.close(resolve))]);
    }
  }
} finally { await browser.close(); }
