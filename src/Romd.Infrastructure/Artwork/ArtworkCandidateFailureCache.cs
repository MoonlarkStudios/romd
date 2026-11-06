using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Artwork;

/// <summary>
/// Short-lived acquisition failures, separate from durable curator exclusions.
/// Bounded per process; expiry permits a repaired provider asset to be tried again.
/// </summary>
public sealed class ArtworkCandidateFailureCache(TimeProvider timeProvider)
{
    private const int Capacity = 2048;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<(string Provider, string Game, ArtworkRole Role, string Asset, string Url), DateTimeOffset> _entries = [];
    private readonly object _sync = new();

    public bool Contains(string provider, ProviderKey candidate)
    {
        lock (_sync)
        {
            var key = (provider, candidate.GameId, candidate.Role, candidate.AssetId, candidate.Url);
            if (!_entries.TryGetValue(key, out var expires))
                return false;
            if (expires > timeProvider.GetUtcNow())
                return true;
            _entries.Remove(key);
            return false;
        }
    }

    public void Record(string provider, ProviderKey candidate)
    {
        lock (_sync)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var key in _entries.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
                _entries.Remove(key);
            var keyToAdd = (provider, candidate.GameId, candidate.Role, candidate.AssetId, candidate.Url);
            if (!_entries.ContainsKey(keyToAdd) && _entries.Count >= Capacity)
                _entries.Remove(_entries.MinBy(x => x.Value).Key);
            _entries[keyToAdd] = now + Lifetime;
        }
    }

    public readonly record struct ProviderKey(string GameId, ArtworkRole Role, string AssetId, string Url);
}
