using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Systems;
using Romd.Persistence.ReferenceData;

namespace Romd.Persistence.Queries;

/// <summary>Resolves a page of system identities and effective facts without per-title queries.</summary>
public sealed class SystemSummaryReader(RomdDbContext context)
{
    public async Task<SystemKeys> ReadKeysAsync(CancellationToken ct) =>
        new(await context.Platforms.Where(x => x.Ownership != null).ToDictionaryAsync(x => x.Id, x => x.CanonicalKey!, ct));

    public async Task<IReadOnlyDictionary<int, SystemSummaryData>> ReadAsync(IEnumerable<int> platformIds, CancellationToken ct)
    {
        var ids = platformIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<int, SystemSummaryData>();
        var platforms = await context.Platforms.Where(x => ids.Contains(x.Id))
            .ToListAsync(ct);
        var effective = platforms.Where(x => x.Ownership != null).ToDictionary(x => x.CanonicalKey!, x => TypedReferenceMapping.Effective(x));
        var hashes = effective.Values.Select(x => x.AssetHash).Where(x => x != null).ToArray();
        var assets = await context.ReferenceAssets.Where(x => hashes.Contains(x.Hash))
            .Select(x => new { x.Hash, x.ContentType }).ToDictionaryAsync(x => x.Hash, x => x.ContentType, ct);
        return platforms.ToDictionary(x => x.Id, x =>
        {
            // An unmapped relational row is an integrity error, never a guessed identity.
            var key = x.CanonicalKey ?? throw new InvalidDataException($"Unregistered system {x.Id}.");
            var definition = x.CanonicalKey is { } canonical ? effective.GetValueOrDefault(canonical) : null;
            var name = definition?.Name ?? x.Name;
            var icon = definition?.AssetHash is { } hash && assets.TryGetValue(hash, out var type)
                ? new ReferenceAssetData($"/api/assets/{hash}", hash, type, definition.Monochrome) : null;
            return new SystemSummaryData(key, name, definition?.CompactLabel ?? name, icon);
        });
    }
}
