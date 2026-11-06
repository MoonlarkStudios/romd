import { createHash } from 'node:crypto';

export const hash = (value) => createHash('sha256').update(String(value)).digest('hex');

// URLs can carry OAuth codes, signed content grants, usernames or credentials.
// Keep only the surface, resource class and a one-way path fingerprint.
export function resourceIdentity(value, origins) {
  try {
    const url = new URL(value);
    const surface = Object.entries(origins).find(([, origin]) => url.origin === origin)?.[0] ?? 'external';
    const extension = url.pathname.match(/\.([a-zA-Z0-9]{1,8})$/)?.[1]?.toLowerCase() ?? 'none';
    return { surface, extension, pathSha256: hash(url.pathname) };
  } catch {
    return { surface: 'invalid', extension: 'none', pathSha256: hash('invalid') };
  }
}

export function requiredResource(request) {
  const type = request.resourceType();
  const path = new URL(request.url()).pathname;
  return ['document', 'script', 'stylesheet', 'font', 'image'].includes(type)
    || path === '/player-config.json' || path === '/api/playback/config'
    || path.startsWith('/delivery/content/') || /\.(wasm|data|mem|bin)$/i.test(path);
}

export function assetResponseFailure(response) {
  const request = response.request();
  if (!requiredResource(request)) return null;
  if (response.status() >= 400) return 'required-response-failed';
  if (response.status() >= 300) return null;
  const contentType = response.headers()['content-type']?.split(';')[0].trim();
  if (request.resourceType() === 'script'
      && !['text/javascript', 'application/javascript', 'application/x-javascript'].includes(contentType)) {
    return 'script-content-type';
  }
  if (request.resourceType() === 'stylesheet' && contentType !== 'text/css') return 'stylesheet-content-type';
  return null;
}

export class Diagnostics {
  constructor(origins) {
    this.origins = origins;
    this.records = [];
    this.failures = 0;
    this.checks = {};
  }

  record(kind, detail = {}, failed = false) {
    if (failed) this.failures += 1;
    if (this.records.length < 1000) this.records.push({ kind, ...detail });
    else this.failures += 1;
  }

  resourceFailure(kind, url, extra = {}) {
    this.record(kind, { ...resourceIdentity(url, this.origins), ...extra }, true);
  }

  protocol(message, frameUrl) {
    if (new URL(frameUrl).origin !== this.origins.player || !message || typeof message !== 'object') return;
    if (message.type === 'hello' && message.protocol === 2) this.checks.playerHandshake = true;
    if (message.type === 'event' && ['ready', 'game-started', 'error'].includes(message.event)) {
      this.record('player-event', { event: message.event }, message.event === 'error');
      if (message.event === 'ready') this.checks.playerReady = true;
      if (message.event === 'game-started') this.checks.playerGameStarted = true;
    }
  }

  result() {
    const required = ['adminCallback', 'adminAuthenticatedView', 'consumerCallback',
      'consumerAuthenticatedView', 'playerHandshake', 'playerReady', 'playerGameStarted',
      'playerBodyPlaying', 'consumerPlaying'];
    return this.failures === 0 && required.every((key) => this.checks[key] === true);
  }

  summary() {
    return { schemaVersion: 1, result: this.result() ? 'passed' : 'failed',
      checks: this.checks, failures: this.failures, records: this.records };
  }
}
