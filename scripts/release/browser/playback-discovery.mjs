// Observe current transfers passively. Resource Timing retains completed older
// requests and therefore cannot establish that a later refetch has finished.
export function observePlaybackDiscovery(page, config) {
  const consumer = config.consumerOrigin + '/api/playback/config';
  const player = config.playerOrigin + '/player-config.json';
  const endpoint = request => { const url = new URL(request.url()); return url.origin + url.pathname; };
  const pending = new Set();
  const waiters = new Set();
  let closed = false;
  let failed = false;
  let startedConsumers = 0;
  let finishedPlayers = 0;
  const changed = () => { for (const wake of [...waiters]) wake(); };
  const started = (request) => {
    const path = endpoint(request);
    if (path === consumer || path === player) {
      if (path === consumer) startedConsumers += 1;
      pending.add(request);
      changed();
    }
  };
  const finished = (request) => {
    if (pending.delete(request)) {
      if (endpoint(request) === player) finishedPlayers += 1;
      changed();
    }
  };
  const broken = (request) => { if (pending.delete(request)) { failed = true; changed(); } };
  const response = (value) => {
    if (pending.has(value.request()) && value.status() >= 400) { failed = true; changed(); }
  };
  page.on('request', started);
  page.on('requestfinished', finished);
  page.on('requestfailed', broken);
  page.on('response', response);
  return {
    async wait() {
      const deadline = Date.now() + 45_000;
      // A consumer response is followed by a separate player fetch. Do not
      // admit the gap between those transfers or reuse an older pair.
      while (!closed && !failed && (!startedConsumers || pending.size || finishedPlayers < startedConsumers)) {
        const remaining = deadline - Date.now();
        if (remaining <= 0) throw new Error('Current playback discovery did not finish');
        await new Promise((resolve, reject) => {
          const wake = () => { clearTimeout(timer); waiters.delete(wake); resolve(); };
          const timer = setTimeout(() => {
            waiters.delete(wake);
            reject(new Error('Current playback discovery did not finish'));
          }, remaining);
          waiters.add(wake);
        });
      }
      if (closed) throw new Error('Playback discovery observation ended before navigation');
      if (failed) throw new Error('Playback configuration request failed');
    },
    dispose() {
      closed = true;
      page.removeListener('request', started);
      page.removeListener('requestfinished', finished);
      page.removeListener('requestfailed', broken);
      page.removeListener('response', response);
      changed();
      pending.clear();
    },
  };
}
