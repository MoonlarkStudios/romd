const CACHE_NAME = 'romd-recent-artwork-v1';
export const ARTWORK_CACHE_LIMITS = { entries: 64, bytes: 32 * 1024 * 1024, imageBytes: 4 * 1024 * 1024 };

export interface CachedArtwork {
  blob: Blob;
  accessedAt: number;
}

export interface ArtworkCacheStore {
  get(key: string): Promise<CachedArtwork | undefined>;
  entries(): Promise<Array<{ key: string; bytes: number; accessedAt: number }>>;
  put(key: string, value: CachedArtwork): Promise<void>;
  delete(key: string): Promise<void>;
}

/** One global budget includes every server/account namespace, preventing abandoned namespaces growing forever. */
export class RecentArtworkCache {
  private writes: Promise<unknown> = Promise.resolve();

  constructor(
    private readonly store: ArtworkCacheStore,
    private readonly limits = ARTWORK_CACHE_LIMITS,
    private readonly now = Date.now,
  ) {}

  private serial<T>(action: () => Promise<T>): Promise<T> {
    const next = this.writes.then(action, action);
    this.writes = next.catch(() => undefined);
    return next;
  }

  read(key: string): Promise<Blob | undefined> {
    return this.serial(async () => {
      const entry = await this.store.get(key);
      if (!entry) return undefined;
      try { await this.store.put(key, { ...entry, accessedAt: this.now() }); } catch { /* A failed recency update must not hide an offline image. */ }
      return entry.blob;
    });
  }

  save(key: string, blob: Blob): Promise<void> {
    return this.serial(async () => {
      if (!blob.size || blob.size > this.limits.imageBytes || blob.size > this.limits.bytes) return;
      const entries = (await this.store.entries()).filter((entry) => entry.key !== key)
        .sort((left, right) => left.accessedAt - right.accessedAt || left.key.localeCompare(right.key));
      let bytes = entries.reduce((total, entry) => total + entry.bytes, 0) + blob.size;
      while (entries.length >= this.limits.entries || bytes > this.limits.bytes) {
        const oldest = entries.shift();
        if (!oldest) break;
        bytes -= oldest.bytes;
        await this.store.delete(oldest.key);
      }
      await this.store.put(key, { blob, accessedAt: this.now() });
    });
  }
}

export function artworkCacheKey(origin: string, account: string, version: string, url: string): string {
  return new URL(`/__romd_artwork_cache/v1/${encodeURIComponent(account)}/${encodeURIComponent(version)}/${encodeURIComponent(url)}`, origin).href;
}

class BrowserArtworkStore implements ArtworkCacheStore {
  private cache() { return caches.open(CACHE_NAME); }
  async get(key: string) {
    const response = await (await this.cache()).match(key);
    return response ? { blob: await response.blob(), accessedAt: Number(response.headers.get('X-Viewed-At')) } : undefined;
  }
  async entries() {
    const cache = await this.cache();
    const requests = await cache.keys();
    return Promise.all(requests.map(async (request) => {
      const response = await cache.match(request);
      return {
        key: request.url,
        bytes: Number(response?.headers.get('Content-Length') ?? ARTWORK_CACHE_LIMITS.bytes),
        accessedAt: Number(response?.headers.get('X-Viewed-At') ?? 0),
      };
    }));
  }
  async put(key: string, value: CachedArtwork) {
    await (await this.cache()).put(key, new Response(value.blob, { headers: {
      'Content-Type': value.blob.type,
      'Content-Length': String(value.blob.size),
      'X-Viewed-At': String(value.accessedAt),
    } }));
  }
  async delete(key: string) { await (await this.cache()).delete(key); }
}

const recentArtwork = new RecentArtworkCache(new BrowserArtworkStore());

/** Returns a caller-owned blob; CacheStorage failure never prevents ordinary online rendering. */
export async function loadRecentArtwork(
  url: string,
  key: string,
  signal: AbortSignal,
): Promise<Blob | undefined> {
  if (!('caches' in globalThis)) return undefined;
  let cached: Blob | undefined;
  try {
    cached = await recentArtwork.read(key);
    // Historical media IDs can retain their identity when bytes change. Refresh those
    // online, falling back to the retained copy only when the server is unavailable.
    if (cached && new URL(url).pathname.startsWith('/artwork/')) return cached;
    const response = await fetch(url, { signal, credentials: 'omit', redirect: 'error' });
    const type = response.headers.get('Content-Type')?.split(';')[0];
    if (!response.ok || !type || !['image/jpeg', 'image/png', 'image/webp'].includes(type) || !response.body) return cached;
    if (Number(response.headers.get('Content-Length')) > ARTWORK_CACHE_LIMITS.imageBytes) return cached;
    const reader = response.body.getReader();
    const parts: Array<ArrayBuffer> = [];
    let size = 0;
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      size += chunk.value.byteLength;
      if (size > ARTWORK_CACHE_LIMITS.imageBytes) {
        await reader.cancel();
        return cached;
      }
      parts.push(new Uint8Array(chunk.value).buffer);
    }
    const blob = new Blob(parts, { type });
    await recentArtwork.save(key, blob);
    return blob;
  } catch {
    return cached;
  }
}
