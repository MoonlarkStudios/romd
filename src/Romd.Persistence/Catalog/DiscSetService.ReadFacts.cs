using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed partial class DiscSetService
{
    private const int ReadBatchSize = 100;
    private sealed record BatchReadFacts(IReadOnlyDictionary<int, DiscSetSnapshot> Rows, IReadOnlyDictionary<int, DiscSetClaims> ClaimsByPlatform);
    private sealed record ScopedClaim(int PlatformId, DiscSetSourceClaim Claim);

    // Facts live for this bounded batch only, including absent members. A long options scan does
    // not accumulate the catalog in memory, and DTO projection never performs database reads.
    private async Task<BatchReadFacts> ReadFactsAsync(IReadOnlyList<DiscSetEntity> rows, bool includeProposal, CancellationToken ct)
    {
        var snapshots = rows.Select(row => Snapshot(row, null)).ToArray();
        var required = snapshots.SelectMany(row =>
            (includeProposal ? row.Facts.Proposal.Members.Concat(row.Facts.Correction?.Members ?? []) : row.Facts.Effective.Members)
            .Select(member => (row.PlatformId, row.Facts.SourceId, member.EntryKey))).Distinct().ToArray();
        List<ScopedClaim> claims = required.Length == 0 ? [] : await ScopedClaimsQuery(db.EffectiveSourceEntries()
            .Where(RequiredClaimsPredicate(required))).ToListAsync(ct);
        var platformIds = snapshots.Select(row => row.PlatformId).Distinct().ToArray();
        Dictionary<int, string?> systemKeys = platformIds.Length == 0 ? [] : await db.Platforms.Where(platform => platformIds.Contains(platform.Id))
            .Select(platform => new { platform.Id, Key = (string?)platform.CanonicalKey }).ToDictionaryAsync(platform => platform.Id, platform => platform.Key, ct);
        var claimsByPlatform = claims.GroupBy(row => row.PlatformId)
            .ToDictionary(group => group.Key, group => new DiscSetClaims(group.Select(row => row.Claim)));
        foreach (int platformId in platformIds)
            claimsByPlatform.TryAdd(platformId, new DiscSetClaims([]));
        return new(snapshots.ToDictionary(row => row.Id, row => row with { SystemKey = systemKeys.GetValueOrDefault(row.PlatformId) }), claimsByPlatform);
    }

    private static Expression<Func<SourceEntryEntity, bool>> RequiredClaimsPredicate(
        IReadOnlyList<(int PlatformId, int SourceId, string EntryKey)> required)
    {
        var entry = Expression.Parameter(typeof(SourceEntryEntity), "entry");
        Expression predicate = Expression.Constant(false);
        foreach (var group in required.GroupBy(key => (key.PlatformId, key.SourceId)))
        {
            var keys = group.Select(key => key.EntryKey).Distinct(StringComparer.Ordinal).ToArray();
            var platform = Expression.Equal(Expression.Property(entry, nameof(SourceEntryEntity.PlatformId)),
                Expression.Convert(Expression.Constant(group.Key.PlatformId), typeof(int?)));
            var source = Expression.Equal(Expression.Property(entry, nameof(SourceEntryEntity.CatalogSourceId)), Expression.Constant(group.Key.SourceId));
            var members = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)],
                Expression.Constant(keys), Expression.Property(entry, nameof(SourceEntryEntity.EntryKey)));
            predicate = Expression.OrElse(predicate, Expression.AndAlso(Expression.AndAlso(platform, source), members));
        }
        return Expression.Lambda<Func<SourceEntryEntity, bool>>(predicate, entry);
    }

    private IQueryable<DiscSetSourceClaim> ClaimsQuery(IQueryable<SourceEntryEntity> entries) =>
        ScopedClaimsQuery(entries).Select(row => row.Claim);

    private IQueryable<ScopedClaim> ScopedClaimsQuery(IQueryable<SourceEntryEntity> entries) =>
        from entry in entries
        join source in db.CatalogSources on entry.CatalogSourceId equals source.Id
        join provenance in db.CatalogReleaseSources on entry.Id equals provenance.SourceEntryId
        join release in db.CatalogReleases on provenance.CatalogReleaseId equals release.Id
        where release.PlatformId == entry.PlatformId && source.Kind == "Dat"
        select new ScopedClaim(release.PlatformId, new DiscSetSourceClaim(entry.CatalogSourceId, entry.EntryKey, entry.Name, release.Fingerprint,
            release.Id, release.CatalogTitleId, release.HasTitleConflict || provenance.AssertedTitleId != release.CatalogTitleId));
}
