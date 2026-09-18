using Microsoft.EntityFrameworkCore;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

/// <summary>Reconcile importer-owned relationships only; never reassign a locally-owned alias.</summary>
internal static class CatalogAliasReconciliation
{
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    internal static async Task ValidateAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        var systems = await db.Platforms.Where(x => x.CanonicalKey != null).ToDictionaryAsync(x => x.Id, x => x.CanonicalKey!, ct);
        var systemAliases = await db.PlatformAliases.Where(x => x.Type == PlatformAliasType.Name).ToListAsync(ct);
        ValidateAssignments(catalog.Systems.Select(x => (x.Key, (IEnumerable<string>)x.Value.Aliases)),
            systemAliases.Select(x => (systems.GetValueOrDefault(x.PlatformId), x.NormalizedValue, x.Ownership == ReferenceOwnership.Romd)));
        foreach (var mapping in await db.PlatformAliases.Where(x => x.Type == PlatformAliasType.ProviderMapping && x.Ownership == null).ToListAsync(ct))
            if (systems.TryGetValue(mapping.PlatformId, out var key) && catalog.Systems.TryGetValue(key, out var facts)
                && facts.ProviderMappings.TryGetValue(mapping.Provider!, out var desired) && Normalize(desired) != mapping.NormalizedValue)
                throw new InvalidDataException($"Unresolved alias ownership for systems/{key}/{mapping.Provider}: stored '{mapping.Value}', catalog '{desired}'. Explicitly classify or replace this relationship before upgrading.");
        var regions = await db.Regions.Where(x => x.CanonicalKey != null).ToDictionaryAsync(x => x.Id, x => x.CanonicalKey!, ct);
        var regionAliases = await db.RegionAliases.ToListAsync(ct);
        ValidateAssignments(catalog.Regions.Select(x => (x.Key, x.Value.Aliases.Append(x.Value.Name))),
            regionAliases.Select(x => (regions.GetValueOrDefault(x.RegionId), x.NormalizedAlias, x.Ownership == ReferenceOwnership.Romd)));
        var languages = await db.GameLanguages.Where(x => x.CanonicalKey != null).ToDictionaryAsync(x => x.Id, x => x.CanonicalKey!, ct);
        var languageAliases = await db.GameLanguageAliases.ToListAsync(ct);
        ValidateAssignments(catalog.Languages.Select(x => (x.Key, x.Value.Aliases.Concat([x.Value.Name, x.Key]))),
            languageAliases.Select(x => (languages.GetValueOrDefault(x.GameLanguageId), x.NormalizedAlias, x.Ownership == ReferenceOwnership.Romd)));
    }
    private static void ValidateAssignments(IEnumerable<(string Key, IEnumerable<string> Aliases)> definitions,
        IEnumerable<(string? Key, string Alias, bool Imported)> existing)
    {
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, aliases) in definitions)
            foreach (var alias in aliases.Select(Normalize))
            {
                if (desired.TryGetValue(alias, out var assigned) && assigned != key)
                    throw new InvalidDataException("Catalog assigns one alias to multiple identities: " + alias);
                desired[alias] = key;
            }
        foreach (var (key, alias, imported) in existing)
            if (!imported && desired.TryGetValue(alias, out var assigned) && assigned != key)
                throw new InvalidDataException("Catalog alias conflicts with an existing identity: " + alias);
    }
    // Prune all obsolete imported assignments before additions, so an intentional move
    // between built-in identities is independent of dictionary iteration order.
    internal static async Task PruneAsync(RomdDbContext db, RomdCatalogInput catalog, CancellationToken ct)
    {
        var systems = await db.Platforms.Where(x => x.CanonicalKey != null).ToDictionaryAsync(x => x.Id, x => x.CanonicalKey!, ct);
        foreach (var row in await db.PlatformAliases.AsTracking().Where(x => x.Ownership == ReferenceOwnership.Romd).ToListAsync(ct))
        {
            if (!systems.TryGetValue(row.PlatformId, out var key) || !catalog.Systems.TryGetValue(key, out var facts)) continue;
            var retained = row.Type == PlatformAliasType.Name
                ? facts.Aliases.Any(x => Normalize(x) == row.NormalizedValue)
                : facts.ProviderMappings.Any(x => Normalize(x.Key) == row.Provider && Normalize(x.Value) == row.NormalizedValue);
            if (!retained) db.PlatformAliases.Remove(row);
        }
        var regions = await db.Regions.Where(x => x.CanonicalKey != null).ToDictionaryAsync(x => x.Id, x => x.CanonicalKey!, ct);
        foreach (var row in await db.RegionAliases.AsTracking().Where(x => x.Ownership == ReferenceOwnership.Romd).ToListAsync(ct))
            if (regions.TryGetValue(row.RegionId, out var key) && catalog.Regions.TryGetValue(key, out var facts)
                && !facts.Aliases.Append(facts.Name).Any(x => Normalize(x) == row.NormalizedAlias)) db.RegionAliases.Remove(row);
        var languages = await db.GameLanguages.Where(x => x.CanonicalKey != null).ToDictionaryAsync(x => x.Id, x => x.CanonicalKey!, ct);
        foreach (var row in await db.GameLanguageAliases.AsTracking().Where(x => x.Ownership == ReferenceOwnership.Romd).ToListAsync(ct))
            if (languages.TryGetValue(row.GameLanguageId, out var key) && catalog.Languages.TryGetValue(key, out var facts)
                && !facts.Aliases.Concat([facts.Name, key]).Any(x => Normalize(x) == row.NormalizedAlias)) db.GameLanguageAliases.Remove(row);
        await db.SaveChangesAsync(ct);
    }
    internal static async Task SystemAsync(RomdDbContext db, int id, SystemInput facts, CancellationToken ct)
    {
        var desired = facts.Aliases.Select(x => PlatformAliasEntity.FromDomain(PlatformAlias.CreateName(id, x)))
            .Concat(facts.ProviderMappings.Select(x => PlatformAliasEntity.FromDomain(PlatformAlias.CreateProviderMapping(id, x.Key, x.Value))))
            .DistinctBy(x => (x.Type, x.Provider, x.NormalizedValue)).ToArray();
        var existing = await db.PlatformAliases.AsTracking().Where(x => x.PlatformId == id).ToListAsync(ct);
        db.PlatformAliases.RemoveRange(existing.Where(x => x.Ownership == ReferenceOwnership.Romd && !desired.Any(y => x.Type == y.Type && x.Provider == y.Provider && x.NormalizedValue == y.NormalizedValue)));
        await db.SaveChangesAsync(ct);
        foreach (var alias in desired)
        {
            var current = alias.Type == PlatformAliasType.Name
                ? await db.PlatformAliases.SingleOrDefaultAsync(x => x.Type == PlatformAliasType.Name && x.NormalizedValue == alias.NormalizedValue, ct)
                : await db.PlatformAliases.SingleOrDefaultAsync(x => x.Type == PlatformAliasType.ProviderMapping && x.PlatformId == id && x.Provider == alias.Provider, ct);
            if (current is not null)
            {
                if (current.PlatformId != id || current.Ownership == ReferenceOwnership.Romd && current.NormalizedValue != alias.NormalizedValue)
                    throw new InvalidDataException("Catalog alias conflicts with a local or different system relationship: " + alias.Value);
                continue; // Identical local additions retain their ownership.
            }
            alias.Ownership = ReferenceOwnership.Romd; db.PlatformAliases.Add(alias);
            await db.SaveChangesAsync(ct);
        }
    }
    internal static async Task RegionAsync(RomdDbContext db, int id, IEnumerable<string> aliases, CancellationToken ct)
    {
        var desired = aliases.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.RegionAliases.AsTracking().Where(x => x.RegionId == id).ToListAsync(ct);
        db.RegionAliases.RemoveRange(existing.Where(x => x.Ownership == ReferenceOwnership.Romd && !desired.Contains(x.NormalizedAlias, StringComparer.Ordinal)));
        await db.SaveChangesAsync(ct);
        foreach (var alias in desired)
        {
            var current = await db.RegionAliases.SingleOrDefaultAsync(x => x.NormalizedAlias == alias, ct);
            if (current is not null)
            {
                if (current.RegionId != id) throw new InvalidDataException("Catalog alias conflicts with another region: " + alias);
                continue;
            }
            db.RegionAliases.Add(new RegionAliasEntity { RegionId = id, NormalizedAlias = alias, Ownership = ReferenceOwnership.Romd });
            await db.SaveChangesAsync(ct);
        }
    }
    internal static async Task LanguageAsync(RomdDbContext db, int id, IEnumerable<string> aliases, CancellationToken ct)
    {
        var desired = aliases.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.GameLanguageAliases.AsTracking().Where(x => x.GameLanguageId == id).ToListAsync(ct);
        db.GameLanguageAliases.RemoveRange(existing.Where(x => x.Ownership == ReferenceOwnership.Romd && !desired.Contains(x.NormalizedAlias, StringComparer.Ordinal)));
        await db.SaveChangesAsync(ct);
        foreach (var alias in desired)
        {
            var current = await db.GameLanguageAliases.SingleOrDefaultAsync(x => x.NormalizedAlias == alias, ct);
            if (current is not null)
            {
                if (current.GameLanguageId != id) throw new InvalidDataException("Catalog alias conflicts with another language: " + alias);
                continue;
            }
            db.GameLanguageAliases.Add(new GameLanguageAliasEntity { GameLanguageId = id, NormalizedAlias = alias, Ownership = ReferenceOwnership.Romd });
            await db.SaveChangesAsync(ct);
        }
    }
}
