import { describe, expect, it } from 'vitest';
import { type ArtworkCacheStore, artworkCacheKey, type CachedArtwork, RecentArtworkCache } from './artworkCache';

class MemoryStore implements ArtworkCacheStore {
  values = new Map<string, CachedArtwork>();
  failWrites = false;
  async get(key: string) { return this.values.get(key); }
  async entries() { return [...this.values].map(([key, value]) => ({ key, bytes: value.blob.size, accessedAt: value.accessedAt })); }
  async put(key: string, value: CachedArtwork) {
    if (this.failWrites) throw new Error('Quota exceeded');
    this.values.set(key, value);
  }
  async delete(key: string) { this.values.delete(key); }
}
const image = (size: number) => new Blob([new Uint8Array(size)], { type: 'image/webp' });

describe('RecentArtworkCache', () => {
  it('evicts least-recently viewed images under a shared byte and entry budget', async () => {
    const store = new MemoryStore();
    let clock = 0;
    const cache = new RecentArtworkCache(store, { entries: 2, bytes: 10, imageBytes: 8 }, () => ++clock);
    await cache.save('server-a/account-a/a', image(4));
    await cache.save('server-b/account-b/b', image(4));
    await cache.read('server-a/account-a/a');
    await cache.save('server-a/account-a/c', image(4));
    expect([...store.values.keys()]).toEqual(['server-a/account-a/a', 'server-a/account-a/c']);
    await cache.save('large', image(8));
    expect([...store.values.keys()]).toEqual(['large']);
  });
  it('rejects oversized images and serializes concurrent writes within the budget', async () => {
    const store = new MemoryStore();
    const cache = new RecentArtworkCache(store, { entries: 2, bytes: 8, imageBytes: 4 });
    await cache.save('oversized', image(5));
    expect(store.values.size).toBe(0);
    await Promise.all(['a', 'b', 'c', 'd'].map((key) => cache.save(key, image(4))));
    expect(store.values.size).toBe(2);
    expect((await store.entries()).reduce((sum, entry) => sum + entry.bytes, 0)).toBe(8);
  });
  it('retains cached image access when recency writes fail', async () => {
    const store = new MemoryStore();
    const cache = new RecentArtworkCache(store);
    const blob = image(4);
    await cache.save('viewed', blob);
    store.failWrites = true;
    expect(await cache.read('viewed')).toBe(blob);
  });
  it('isolates server, account, version, and selected variant identities', () => {
    const base = artworkCacheKey('https://romd.test', 'account-a', 'v1', '/artwork/a/card/v1');
    const others = [
      artworkCacheKey('https://other.test', 'account-a', 'v1', '/artwork/a/card/v1'),
      artworkCacheKey('https://romd.test', 'account-b', 'v1', '/artwork/a/card/v1'),
      artworkCacheKey('https://romd.test', 'account-a', 'v2', '/artwork/a/card/v2'),
      artworkCacheKey('https://romd.test', 'account-a', 'v1', '/artwork/a/thumb/v1'),
    ];
    expect(others).not.toContain(base);
    expect(new Set(others).size).toBe(4);
  });
});
