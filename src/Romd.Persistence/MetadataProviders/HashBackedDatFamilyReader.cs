using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.MetadataProviders;
using Romd.Dat;

namespace Romd.Persistence.MetadataProviders;

internal static class HashBackedDatFamilyReader
{
    public static async Task<DatFamilySearchEvidence?> ReadAsync(RomdDbContext db, int titleId, CancellationToken ct)
    {
        var matches = await db.Database.SqlQuery<Match>($"""
            SELECT DISTINCT "DatFileId", "GameId"
            FROM romd.provider_identity_dat_matches({titleId})
            """).ToArrayAsync(ct);
        if (matches.Length == 0)
            return null;
        if (matches.Any(match => match.DatFileId != matches[0].DatFileId))
            return DatFamilySearchEvidence.Ambiguous;

        int[] gameIds = matches.Select(match => match.GameId).ToArray();
        // Fetch only declarations reachable from the matched releases. Two hits for a
        // reference are enough to establish ambiguity. UNION visits each entry once.
        // The digest keeps unrestricted IDs indexable; full equality resolves references.
        var entries = await db.Database.SqlQuery<Entry>($"""
            WITH RECURSIVE ancestry("Id") AS (
                SELECT unnest({gameIds})
                UNION
                SELECT parent."Id"
                FROM ancestry a
                JOIN romd."DatGames" child ON child."Id" = a."Id"
                CROSS JOIN LATERAL (
                    (SELECT g."Id"
                     FROM romd."DatGames" g
                     WHERE g."DatFileId" = child."DatFileId" AND g."Name" = child."CloneOf"
                     ORDER BY g."Id" LIMIT 2)
                    UNION
                    (SELECT g."Id"
                     FROM romd."DatGames" g
                     WHERE g."DatFileId" = child."DatFileId"
                       AND md5(g."DeclaredId") = md5(child."CloneOfId")
                       AND g."DeclaredId" = child."CloneOfId"
                     ORDER BY g."Id" LIMIT 2)
                ) parent
            )
            SELECT g."Id", g."Name", g."DeclaredId", g."CloneOf", g."CloneOfId", g."IsBios",
                   g."DevelopmentStatus", g."Category"
            FROM ancestry a
            JOIN romd."DatGames" g ON g."Id" = a."Id"
            """).ToArrayAsync(ct);

        if (entries.Any(entry => entry.IsBios)
            || entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            return DatFamilySearchEvidence.Ambiguous;

        var parents = DatParentResolver.Resolve(entries.Select(entry => new DatParentDeclaration(
            entry.Name, entry.DeclaredId, entry.CloneOf, entry.CloneOfId)).ToArray(), ct);
        var matchedIds = gameIds.ToHashSet();
        var matchedNames = entries.Where(entry => matchedIds.Contains(entry.Id)).Select(entry => entry.Name)
            .Order(StringComparer.Ordinal).ToArray();
        string root = matchedNames[0];
        while (parents.TryGetValue(root, out var parent))
        {
            ct.ThrowIfCancellationRequested();
            if (parent.Status != DatParentStatus.Resolved)
                return DatFamilySearchEvidence.Ambiguous;
            root = parent.ParentName!;
        }

        // Remember paths already proven to reach the common root. A different root
        // or a broken link anywhere in a matched release's ancestry requires review.
        var family = new HashSet<string>(StringComparer.Ordinal) { root };
        foreach (string name in matchedNames)
        {
            string ancestor = name;
            while (family.Add(ancestor))
            {
                ct.ThrowIfCancellationRequested();
                if (!parents.TryGetValue(ancestor, out var parent) || parent.Status != DatParentStatus.Resolved)
                    return DatFamilySearchEvidence.Ambiguous;
                ancestor = parent.ParentName!;
            }
        }

        var rootEntry = entries.Single(entry => entry.Name == root);
        bool canInherit = !matchedIds.Contains(rootEntry.Id) && IsRetailRelease(rootEntry) &&
            entries.Where(entry => matchedIds.Contains(entry.Id)).All(IsRetailRelease);
        return new(matchedNames.Where(name => name != root).Prepend(root).ToArray(), false)
        {
            Parent = canInherit ? new(matches[0].DatFileId, rootEntry.Id, root) : null
        };
    }

    private static bool IsRetailRelease(Entry entry) =>
        (entry.Category?.ToUpperInvariant() is null or "GAME" or "GAMES") &&
        (entry.DevelopmentStatus is null || string.Equals(entry.DevelopmentStatus, "Stable", StringComparison.OrdinalIgnoreCase)) &&
        !DatNameParser.HasNonRetailQualifier(entry.Name);

    private sealed record Match(int DatFileId, int GameId);

    private sealed record Entry(int Id, string Name, string? DeclaredId, string? CloneOf, string? CloneOfId, bool IsBios,
        string? DevelopmentStatus, string? Category);
}
