using Romd.Domain.Source.Platform;

namespace Romd.Admin.Application.Source.Platform;

/// <summary>
///     Builds the normalized routing lookup (platform names, short names, name
///     aliases) and delegates matching to <see cref="DatHeaderPlatformMatcher" />.
///     Keys claimed by more than one platform are excluded so an ambiguous
///     header is left unrouted rather than mis-routed.
/// </summary>
public sealed class PlatformHeaderResolver : IPlatformHeaderResolver
{
    private readonly IPlatformAliasRepository _aliasRepository;
    private readonly IPlatformRepository _platformRepository;

    public PlatformHeaderResolver(
        IPlatformRepository platformRepository,
        IPlatformAliasRepository aliasRepository)
    {
        _platformRepository = platformRepository;
        _aliasRepository = aliasRepository;
    }

    public async Task<int?> ResolvePlatformIdAsync(string datHeaderName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(datHeaderName))
        {
            return null;
        }

        var platforms = await _platformRepository.GetAllAsync(ct);
        var nameAliases = await _aliasRepository.GetAllNameAliasesAsync(ct);

        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        void AddKey(string normalizedKey, int platformId)
        {
            if (normalizedKey.Length == 0 || ambiguous.Contains(normalizedKey))
            {
                return;
            }

            if (lookup.TryGetValue(normalizedKey, out var existing) && existing != platformId)
            {
                lookup.Remove(normalizedKey);
                ambiguous.Add(normalizedKey);
                return;
            }

            lookup[normalizedKey] = platformId;
        }

        foreach (var platform in platforms)
        {
            AddKey(PlatformNameNormalizer.Normalize(platform.Name), platform.Id);
            AddKey(PlatformNameNormalizer.Normalize(platform.ShortName), platform.Id);
        }

        foreach (var (platformId, normalizedValue) in nameAliases)
        {
            AddKey(normalizedValue, platformId);
        }

        return DatHeaderPlatformMatcher.Match(datHeaderName, lookup);
    }
}
