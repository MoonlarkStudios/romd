using System.Collections.Concurrent;

namespace Romd.Infrastructure.Taxonomy;

/// <summary>
///     Thread-safe, keyed alias cache for taxonomy resolution.
///     Each taxonomy type (e.g., "regions", "languages") gets its own cache entry,
///     avoiding per-type duplication of caching infrastructure.
/// </summary>
public sealed class TaxonomyAliasCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    public bool TryGetId(string cacheKey, string normalizedToken, out int id)
    {
        id = 0;
        return _entries.TryGetValue(cacheKey, out var entry) &&
               entry.Aliases.TryGetValue(normalizedToken, out id);
    }

    public async Task EnsureLoadedAsync(
        string cacheKey,
        Func<Task<IReadOnlyDictionary<string, int>>> loader,
        CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry());
        if (entry.IsLoaded) return;

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (entry.IsLoaded) return;

            var aliases = await loader();
            entry.Aliases = new Dictionary<string, int>(aliases, StringComparer.Ordinal);
            entry.IsLoaded = true;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public void AddAlias(string cacheKey, string normalizedToken, int id)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
            lock (entry)
                entry.Aliases[normalizedToken] = id;
    }

    public void Invalidate(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
            lock (entry)
            {
                entry.Aliases.Clear();
                entry.IsLoaded = false;
            }
    }

    public async Task AcquireCreationLockAsync(string cacheKey, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry());
        await entry.CreationLock.WaitAsync(ct);
    }

    public void ReleaseCreationLock(string cacheKey)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
            entry.CreationLock.Release();
    }

    public bool IsLoaded(string cacheKey) =>
        _entries.TryGetValue(cacheKey, out var entry) && entry.IsLoaded;

    private sealed class CacheEntry
    {
        public Dictionary<string, int> Aliases = new(StringComparer.Ordinal);
        public readonly SemaphoreSlim Lock = new(1, 1);
        public readonly SemaphoreSlim CreationLock = new(1, 1);
        public volatile bool IsLoaded;
    }
}
