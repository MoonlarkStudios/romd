using Romd.Admin.Application.Artwork.Providers;

namespace Romd.Infrastructure.Artwork;

/// <summary>Dedicated singleton preview cache; never writes originals, CAS files, or database records.</summary>
public sealed class ArtworkPreviewCache(TimeProvider timeProvider)
{
    private const long MaxBytes = 64 * 1024 * 1024;
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly object _lock = new();
    private long _bytes;
    // Bound simultaneous preview fetch/decode and coalesce repeated requests across service scopes.
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal ArtworkPreviewImage? Get(string key)
    {
        lock (_lock)
        {
            Prune();
            return _entries.TryGetValue(key, out var entry) ? entry.Image : null;
        }
    }

    internal void Put(string key, ArtworkPreviewImage image)
    {
        if (image.Bytes.LongLength > MaxBytes) return;
        lock (_lock)
        {
            Prune();
            if (_entries.Remove(key, out var previous)) _bytes -= previous.Image.Bytes.LongLength;
            while (_bytes + image.Bytes.LongLength > MaxBytes || _entries.Count >= 512)
                Remove(_entries.MinBy(entry => entry.Value.ExpiresAt).Key);
            _entries[key] = new Entry(image, timeProvider.GetUtcNow().AddMinutes(5));
            _bytes += image.Bytes.LongLength;
        }
    }

    private void Prune()
    {
        foreach (var key in _entries.Where(entry => entry.Value.ExpiresAt <= timeProvider.GetUtcNow())
                     .Select(entry => entry.Key).ToArray()) Remove(key);
    }
    private void Remove(string key)
    {
        if (_entries.Remove(key, out var entry)) _bytes -= entry.Image.Bytes.LongLength;
    }
    private sealed record Entry(ArtworkPreviewImage Image, DateTimeOffset ExpiresAt);
}
