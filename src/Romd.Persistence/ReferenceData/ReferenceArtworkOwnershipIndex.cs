using Microsoft.EntityFrameworkCore;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

/// <summary>Derived polymorphic CAS ownership index. Only atomic catalog publication rebuilds it.</summary>
internal static class ReferenceArtworkOwnershipIndex
{
    // A reference-specific reservation, not a retention policy for ROMs, DATs, or game artwork.
    internal static async Task RebuildAsync(RomdDbContext db, CancellationToken ct)
    {
        var oldHashes = await db.ReferenceAssetOwners.Select(x => x.Hash).Distinct().ToListAsync(ct);
        var owners = new List<ReferenceAssetOwnerEntity>();
        foreach (var row in await db.Ratings.ToListAsync(ct))
        {
            if (row.BaseAssetHash is { } baseHash) owners.Add(new() { Kind = "ratings", Key = row.Key, Slot = "base", Hash = baseHash });
        }
        foreach (var row in await db.Platforms.Where(x => x.Ownership != null).ToListAsync(ct))
        {
            if (row.BaseAssetHash is { } baseHash) owners.Add(new() { Kind = "systems", Key = row.CanonicalKey!, Slot = "base", Hash = baseHash });
            if (row.HasArtworkOverride && row.ArtworkOverrideHash is { } overrideHash) owners.Add(new() { Kind = "systems", Key = row.CanonicalKey!, Slot = "override", Hash = overrideHash });
        }
        var released = oldHashes.Except(owners.Select(x => x.Hash)).ToArray();
        if (released.Length > 0)
            await db.ReferenceAssets.Where(x => released.Contains(x.Hash)).ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.ReservedUntil, DateTimeOffset.UtcNow.AddDays(7)), ct);
        foreach (var entry in db.ChangeTracker.Entries<ReferenceAssetOwnerEntity>().ToArray()) entry.State = EntityState.Detached;
        await db.ReferenceAssetOwners.ExecuteDeleteAsync(ct);
        db.ReferenceAssetOwners.AddRange(owners);
    }

}
