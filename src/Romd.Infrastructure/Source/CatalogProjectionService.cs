using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Source;

/// <summary>
///     Rebuilds the canonical catalog projection for a platform from its effective source claims.
///     Releases are upserted by content fingerprint so their identity is stable across DAT
///     replacement; their files, source provenance and taxonomy are rebuilt each pass.
/// </summary>
public sealed class CatalogProjectionService(
    RomdDbContext context,
    ICatalogSourceSnapshotReader sourceSnapshots,
    ITitlePayloadAvailabilityProjection payloadAvailability,
    TimeProvider timeProvider,
    ILogger<CatalogProjectionService> logger) : ICatalogProjectionService
{
    // Bounded like AdminRealtimeOutboxEventEntity.LastError.
    private const int CatalogRebuildErrorMaxLength = 2000;

    public async Task<bool> RebuildPlatformAsync(int platformId, CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.Clear();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Updating the platform acquires its row lock before reading any source
        // claims. Rebuilds serialize here; concurrent dirty marking cannot be lost
        // behind this pass's Clean commit.
        await MarkStateAsync(platformId, CatalogRebuildState.Dirty, rebuiltAt: null, cancellationToken);
        await transaction.CreateSavepointAsync("catalog_projection", cancellationToken);
        try
        {
            var desired = await BuildDesiredReleasesAsync(platformId, cancellationToken);
            await DeletePlatformChildRowsAsync(platformId, cancellationToken);
            await UpsertReleasesAsync(platformId, desired, cancellationToken);
            await ClearInvalidTrackedTitlePinsAsync(cancellationToken);
            await InsertChildRowsAsync(platformId, desired, cancellationToken);
            await UpdateTitleStatesAsync(platformId, cancellationToken);

            // A platform cannot become Clean while its canonical local-payload fact is stale.
            // The refresh participates in this same convergence transaction.
            await payloadAvailability.RefreshPlatformPayloadAssertionsAsync(platformId, cancellationToken);

            await MarkCleanAsync(platformId, timeProvider.GetUtcNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            context.ChangeTracker.Clear();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve the old graph and the retry marker, still under the same
            // row lock. Shutdown cancellation is not a failed catalog.
            await transaction.RollbackToSavepointAsync("catalog_projection", CancellationToken.None);
            context.ChangeTracker.Clear();
            await transaction.CommitAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Catalog projection rebuild failed for platform {PlatformId}; leaving platform in Failed state",
                platformId);
            await transaction.RollbackToSavepointAsync("catalog_projection", cancellationToken);
            context.ChangeTracker.Clear();
            await MarkFailedAsync(platformId, ex.Message, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
    }

    public async Task MarkPlatformDirtyAsync(
        int platformId,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<int>? affectedTitleIds = null)
    {
        // Every source/topology mutation already funnels through durable Dirty marking. Refresh
        // the availability projection first so the base mutation, title fact, and recovery marker
        // are one transaction-owned unit rather than a post-commit invalidation promise.
        if (affectedTitleIds is null)
        {
            await payloadAvailability.RollupPlatformAsync(platformId, cancellationToken);
        }
        else
        {
            await payloadAvailability.RollupTitlesAsync(affectedTitleIds, cancellationToken);
        }
        await MarkStateAsync(platformId, CatalogRebuildState.Dirty, rebuiltAt: null, cancellationToken);
    }

    public async Task MarkCatalogSourceDirtyAsync(
        int platformId,
        int catalogSourceId,
        CancellationToken cancellationToken = default)
    {
        await payloadAvailability.RollupCatalogSourceAsync(catalogSourceId, cancellationToken);
        await MarkStateAsync(platformId, CatalogRebuildState.Dirty, rebuiltAt: null, cancellationToken);
    }

    public Task RefreshCatalogSourcePayloadAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default) =>
        payloadAvailability.RefreshCatalogSourcePayloadAssertionsAsync(catalogSourceId, cancellationToken);

    public Task RefreshPlatformPayloadAsync(
        int platformId,
        CancellationToken cancellationToken = default) =>
        payloadAvailability.RefreshPlatformPayloadAssertionsAsync(platformId, cancellationToken);

    public async Task<IReadOnlyList<int>> GetPlatformIdsNeedingRebuildAsync(
        CancellationToken cancellationToken = default) =>
        await context.Platforms
            .AsNoTracking()
            .Where(p => p.CatalogRebuildState != CatalogRebuildState.Clean)
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

    private async Task MarkStateAsync(
        int platformId,
        CatalogRebuildState state,
        DateTimeOffset? rebuiltAt,
        CancellationToken cancellationToken) =>
        await context.Platforms
            .Where(p => p.Id == platformId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.CatalogRebuildState, state)
                    .SetProperty(p => p.CatalogRebuiltAt, p => rebuiltAt ?? p.CatalogRebuiltAt),
                cancellationToken);

    private async Task MarkCleanAsync(
        int platformId,
        DateTimeOffset rebuiltAt,
        CancellationToken cancellationToken) =>
        await context.Platforms
            .Where(p => p.Id == platformId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.CatalogRebuildState, CatalogRebuildState.Clean)
                    .SetProperty(p => p.CatalogRebuiltAt, rebuiltAt)
                    .SetProperty(p => p.CatalogRebuildError, (string?)null)
                    .SetProperty(p => p.CatalogRebuildFailedAtUtc, (DateTimeOffset?)null),
                cancellationToken);

    private async Task MarkFailedAsync(
        int platformId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        string boundedError = errorMessage.Length <= CatalogRebuildErrorMaxLength
            ? errorMessage
            : errorMessage[..CatalogRebuildErrorMaxLength];
        var failedAtUtc = timeProvider.GetUtcNow();
        await context.Platforms
            .Where(p => p.Id == platformId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.CatalogRebuildState, CatalogRebuildState.Failed)
                    .SetProperty(p => p.CatalogRebuildError, boundedError)
                    .SetProperty(p => p.CatalogRebuildFailedAtUtc, failedAtUtc),
                cancellationToken);
    }

    private async Task<List<DesiredRelease>> BuildDesiredReleasesAsync(
        int platformId,
        CancellationToken cancellationToken)
    {
        var claims = (await sourceSnapshots.ReadPlatformAsync(platformId, cancellationToken)).Entries
            .SelectMany(entry => entry.Claims.Select(claim => new ProjectedClaim(
                claim.ProviderClaimKey,
                entry.SourceEntryId,
                claim.ClaimPrecedence,
                claim.ClaimOrder,
                entry.SourceKind,
                claim.Name,
                claim.Region,
                claim.Language,
                claim.Revision,
                entry.AssertedTitleId,
                claim.Requirements,
                claim.RegionIds,
                claim.LanguageIds)))
            .ToList();

        if (claims.Count == 0)
        {
            return [];
        }

        // An entry asserts exactly one release (unique SourceEntryId). Claims from several
        // versions of one source share an entry — and can land in different content groups
        // when a version changes hashes — so the asserting game is chosen globally per entry:
        // the live version's game (Active > PendingActivation > Superseded), newest as
        // tiebreaker. Same winner rule as the cutover backfill.
        var assertingClaimKeyByEntryId = claims
            .GroupBy(claim => claim.SourceEntryId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(claim => claim.ClaimPrecedence)
                    .ThenByDescending(claim => claim.ClaimOrder)
                    .ThenBy(claim => claim.ProviderClaimKey, StringComparer.Ordinal)
                    .First()
                    .ProviderClaimKey);

        var groups = claims
            .GroupBy(claim => BuildReleaseFingerprint(platformId, claim).Value);

        var desired = new List<DesiredRelease>();

        foreach (var group in groups)
        {
            var members = group.ToList();
            var mappedClaims = members
                .Where(claim => claim.AssertedTitleId.HasValue)
                .OrderBy(claim => claim.ClaimOrder)
                .ThenBy(claim => claim.SourceKind)
                .ThenBy(claim => claim.SourceEntryId)
                .ThenBy(claim => claim.ProviderClaimKey, StringComparer.Ordinal)
                .ToList();

            if (mappedClaims.Count == 0)
            {
                // No source in this content group maps to a title yet — create no release.
                continue;
            }

            var representative = mappedClaims[0];
            int catalogTitleId = representative.AssertedTitleId!.Value;
            bool hasConflict = mappedClaims
                .Select(claim => claim.AssertedTitleId)
                .Distinct()
                .Count() > 1;

            var fingerprint = BuildReleaseFingerprint(platformId, representative);

            var files = BuildDesiredFiles(members);

            var sources = members
                .Where(claim => StringComparer.Ordinal.Equals(
                    assertingClaimKeyByEntryId[claim.SourceEntryId],
                    claim.ProviderClaimKey))
                .Select(claim => new DesiredSource(
                    claim.SourceEntryId,
                    claim.ProviderClaimKey,
                    claim.AssertedTitleId))
                .ToList();

            if (sources.Count == 0)
            {
                // Every member lost its entry to a fresher version's game: this content group
                // is a stale version's hash-set, and no entry asserts it anymore.
                continue;
            }

            var regionIds = members
                .SelectMany(claim => claim.RegionIds)
                .Distinct()
                .ToList();
            var languageIds = members
                .SelectMany(claim => claim.LanguageIds)
                .Distinct()
                .ToList();

            desired.Add(new DesiredRelease(
                fingerprint.Value,
                fingerprint.PrimarySha1,
                catalogTitleId,
                representative.Name,
                representative.Region,
                representative.Language,
                representative.Revision,
                members.Count,
                hasConflict,
                sources,
                files,
                regionIds,
                languageIds));
        }

        return desired;
    }

    private async Task DeletePlatformChildRowsAsync(int platformId, CancellationToken cancellationToken)
    {
        // Deleting CatalogReleaseFiles cascades CatalogReleaseFileSources at the DB level.
        await context.CatalogReleaseFiles
            .Where(file => context.CatalogReleases.Any(r => r.Id == file.CatalogReleaseId && r.PlatformId == platformId))
            .ExecuteDeleteAsync(cancellationToken);

        await context.CatalogReleaseSources
            .Where(src => context.CatalogReleases.Any(r => r.Id == src.CatalogReleaseId && r.PlatformId == platformId))
            .ExecuteDeleteAsync(cancellationToken);

        await context.CatalogReleaseRegions
            .Where(rgn => context.CatalogReleases.Any(r => r.Id == rgn.CatalogReleaseId && r.PlatformId == platformId))
            .ExecuteDeleteAsync(cancellationToken);

        await context.CatalogReleaseLanguages
            .Where(lng => context.CatalogReleases.Any(r => r.Id == lng.CatalogReleaseId && r.PlatformId == platformId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task UpsertReleasesAsync(
        int platformId,
        IReadOnlyList<DesiredRelease> desired,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await context.CatalogReleases
            .AsTracking()
            .Where(r => r.PlatformId == platformId)
            .ToListAsync(cancellationToken);
        var existingByFingerprint = existing.ToDictionary(r => r.Fingerprint, StringComparer.Ordinal);
        var desiredByFingerprint = desired.ToDictionary(r => r.Fingerprint, StringComparer.Ordinal);

        foreach (var release in existing.Where(r => !desiredByFingerprint.ContainsKey(r.Fingerprint)))
        {
            context.CatalogReleases.Remove(release);
        }

        foreach (var release in desired)
        {
            long sizeBytes = release.Files.Sum(file => file.Size);
            int fileCount = release.Files.Count;

            if (existingByFingerprint.TryGetValue(release.Fingerprint, out var entity))
            {
                entity.CatalogTitleId = release.CatalogTitleId;
                entity.PrimarySha1 = release.PrimarySha1;
                entity.Name = release.Name;
                entity.Region = release.Region;
                entity.Language = release.Language;
                entity.Revision = release.Revision;
                entity.SourceCount = release.SourceCount;
                entity.HasTitleConflict = release.HasTitleConflict;
                entity.SizeBytes = sizeBytes;
                entity.FileCount = fileCount;
                entity.UpdatedAt = now;
            }
            else
            {
                context.CatalogReleases.Add(new CatalogReleaseEntity
                {
                    PlatformId = platformId,
                    CatalogTitleId = release.CatalogTitleId,
                    Fingerprint = release.Fingerprint,
                    PrimarySha1 = release.PrimarySha1,
                    Name = release.Name,
                    Region = release.Region,
                    Language = release.Language,
                    Revision = release.Revision,
                    SourceCount = release.SourceCount,
                    HasTitleConflict = release.HasTitleConflict,
                    SizeBytes = sizeBytes,
                    FileCount = fileCount,
                    UpdatedAt = now
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InsertChildRowsAsync(
        int platformId,
        IReadOnlyList<DesiredRelease> desired,
        CancellationToken cancellationToken)
    {
        if (desired.Count == 0)
        {
            return;
        }

        // After upsert+prune the platform's releases are exactly the desired set, so scoping by
        // platform (rather than an IN-list over every fingerprint) yields the same mapping.
        var releaseIdByFingerprint = await context.CatalogReleases
            .AsNoTracking()
            .Where(r => r.PlatformId == platformId)
            .ToDictionaryAsync(r => r.Fingerprint, r => r.Id, StringComparer.Ordinal, cancellationToken);

        // Insert files first to obtain their IDs for file-source provenance.
        var fileEntities = new List<(DesiredRelease Release, DesiredFile File, CatalogReleaseFileEntity Entity)>();
        foreach (var release in desired)
        {
            int releaseId = releaseIdByFingerprint[release.Fingerprint];
            foreach (var file in release.Files)
            {
                var entity = new CatalogReleaseFileEntity
                {
                    CatalogReleaseId = releaseId,
                    FileFingerprint = file.FileFingerprint,
                    Name = file.Name,
                    Size = file.Size,
                    Crc = file.Crc,
                    Md5 = file.Md5,
                    Sha1 = file.Sha1,
                    Status = file.Status,
                    IsDisk = file.IsDisk
                };
                context.CatalogReleaseFiles.Add(entity);
                fileEntities.Add((release, file, entity));
            }

            foreach (var source in release.Sources)
            {
                context.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity
                {
                    CatalogReleaseId = releaseId,
                    SourceEntryId = source.SourceEntryId,
                    ProviderClaimKey = source.ProviderClaimKey,
                    AssertedTitleId = source.AssertedTitleId
                });
            }

            foreach (int regionId in release.RegionIds)
            {
                context.CatalogReleaseRegions.Add(new CatalogReleaseRegionEntity
                {
                    CatalogReleaseId = releaseId,
                    RegionId = regionId
                });
            }

            foreach (int languageId in release.LanguageIds)
            {
                context.CatalogReleaseLanguages.Add(new CatalogReleaseLanguageEntity
                {
                    CatalogReleaseId = releaseId,
                    GameLanguageId = languageId
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        foreach (var (_, file, entity) in fileEntities)
        {
            foreach (var source in file.Sources)
            {
                context.CatalogReleaseFileSources.Add(new CatalogReleaseFileSourceEntity
                {
                    CatalogReleaseFileId = entity.Id,
                    SourceEntryId = source.SourceEntryId,
                    ProviderClaimKey = source.ProviderClaimKey,
                    RequirementKind = source.RequirementKind.ToString(),
                    ProviderRequirementKey = source.ProviderRequirementKey
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private Task ClearInvalidTrackedTitlePinsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return context.TrackedTitles
            .Where(tracked => tracked.PinnedCatalogReleaseId != null
                              && context.CatalogReleases.Any(release =>
                                  release.Id == tracked.PinnedCatalogReleaseId
                                  && release.CatalogTitleId != tracked.TitleId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(tracked => tracked.PinnedCatalogReleaseId, (int?)null)
                    .SetProperty(tracked => tracked.UpdatedAt, now),
                cancellationToken);
    }

    private async Task UpdateTitleStatesAsync(int platformId, CancellationToken cancellationToken)
    {
        await context.Titles
            .Where(t => t.PlatformId == platformId && context.CatalogReleases.Any(r => r.CatalogTitleId == t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CatalogState, TitleCatalogState.Active), cancellationToken);

        await context.Titles
            .Where(t => t.PlatformId == platformId && !context.CatalogReleases.Any(r => r.CatalogTitleId == t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CatalogState, TitleCatalogState.UserOnly), cancellationToken);
    }

    private static List<DesiredFile> BuildDesiredFiles(
        IEnumerable<ProjectedClaim> claims)
    {
        var byFingerprint = new Dictionary<string, DesiredFileBuilder>(StringComparer.Ordinal);
        var orderedClaims = claims
            .OrderBy(claim => claim.ClaimOrder)
            .ThenBy(claim => claim.SourceKind)
            .ThenBy(claim => claim.SourceEntryId)
            .ThenBy(claim => claim.ProviderClaimKey, StringComparer.Ordinal)
            .ToList();

        foreach (var kind in new[] { CatalogSourceRequirementKind.Rom, CatalogSourceRequirementKind.Disk })
        {
            foreach (var requirement in orderedClaims
                         .SelectMany(claim => claim.Requirements
                             .Where(requirement => requirement.Kind == kind)
                             .OrderBy(requirement => requirement.RequirementOrder)
                             .ThenBy(requirement => requirement.ProviderRequirementKey, StringComparer.Ordinal)
                             .Select(requirement => new { Claim = claim, Requirement = requirement })))
            {
                var fact = requirement.Requirement;
                string fingerprint = BuildFileFingerprint(
                    fact.Sha1,
                    fact.Md5,
                    fact.Name,
                    fact.Size,
                    fact.Status);
                if (!byFingerprint.TryGetValue(fingerprint, out var builder))
                {
                    builder = new DesiredFileBuilder(
                        fingerprint,
                        fact.Name,
                        fact.Size,
                        fact.Crc,
                        fact.Md5,
                        fact.Sha1,
                        fact.Status,
                        fact.Kind == CatalogSourceRequirementKind.Disk);
                    byFingerprint[fingerprint] = builder;
                }

                builder.Sources.Add(new DesiredFileSource(
                    requirement.Claim.SourceEntryId,
                    requirement.Claim.ProviderClaimKey,
                    fact.Kind,
                    fact.ProviderRequirementKey));
            }
        }

        return byFingerprint.Values
            .Select(b => new DesiredFile(
                b.FileFingerprint,
                b.Name,
                b.Size,
                b.Crc,
                b.Md5,
                b.Sha1,
                b.Status,
                b.IsDisk,
                b.Sources))
            .ToList();
    }

    private static ReleaseFingerprint BuildReleaseFingerprint(
        int platformId,
        ProjectedClaim claim)
    {
        var sha1Parts = claim.Requirements
            .Where(requirement => requirement.Sha1 is not null)
            .Select(requirement =>
                $"{(requirement.Kind == CatalogSourceRequirementKind.Rom ? "r" : "d")}:{requirement.Sha1}")
            .Order(StringComparer.Ordinal)
            .ToList();

        if (sha1Parts.Count == 1)
        {
            string sha1 = sha1Parts[0][2..];
            return new ReleaseFingerprint($"sha1-v1:{sha1}", Sha1.Parse(sha1));
        }

        if (sha1Parts.Count > 1)
        {
            return new ReleaseFingerprint($"sha1-set-v1:{HashText(string.Join('|', sha1Parts))}", PrimarySha1: null);
        }

        // No SHA available — source-scoped fallback. Provider claim identity is intentionally
        // excluded so an identical replacement claim reconnects to the same fingerprint.
        var fallbackRequirements = claim.Requirements
            .Select(requirement => requirement.Kind == CatalogSourceRequirementKind.Rom
                ? $"r:{Normalize(requirement.Name)}:{requirement.Size}:{Normalize(requirement.Status)}"
                : $"d:{Normalize(requirement.Name)}:{requirement.Md5?.ToString() ?? ""}:{Normalize(requirement.Status)}")
            .Order(StringComparer.Ordinal);
        string fallbackSource = string.Join(
            '',
            platformId.ToString(),
            Normalize(claim.Name),
            Normalize(claim.Region),
            Normalize(claim.Language),
            Normalize(claim.Revision),
            string.Join('|', fallbackRequirements));

        return new ReleaseFingerprint($"datgame-fallback-v1:{HashText(fallbackSource)}", PrimarySha1: null);
    }

    private static string BuildFileFingerprint(Sha1? sha1, Md5? md5, string name, long size, string? status)
    {
        if (sha1 is not null)
        {
            return $"sha1:{sha1}";
        }

        if (md5 is not null)
        {
            return $"md5:{md5}";
        }

        return $"req:{Normalize(name)}:{size}:{Normalize(status)}";
    }

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string HashText(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ReleaseFingerprint(string Value, Sha1? PrimarySha1);

    private sealed record DesiredFile(
        string FileFingerprint,
        string Name,
        long Size,
        Crc32? Crc,
        Md5? Md5,
        Sha1? Sha1,
        string? Status,
        bool IsDisk,
        IReadOnlyList<DesiredFileSource> Sources);

    private sealed record DesiredSource(int SourceEntryId, string ProviderClaimKey, int? AssertedTitleId);

    private sealed record DesiredFileSource(
        int SourceEntryId,
        string ProviderClaimKey,
        CatalogSourceRequirementKind RequirementKind,
        string ProviderRequirementKey);

    private sealed record DesiredRelease(
        string Fingerprint,
        Sha1? PrimarySha1,
        int CatalogTitleId,
        string Name,
        string? Region,
        string? Language,
        string? Revision,
        int SourceCount,
        bool HasTitleConflict,
        IReadOnlyList<DesiredSource> Sources,
        IReadOnlyList<DesiredFile> Files,
        IReadOnlyList<int> RegionIds,
        IReadOnlyList<int> LanguageIds);

    private sealed record DesiredFileBuilder(
        string FileFingerprint,
        string Name,
        long Size,
        Crc32? Crc,
        Md5? Md5,
        Sha1? Sha1,
        string? Status,
        bool IsDisk)
    {
        public List<DesiredFileSource> Sources { get; } = [];
    }

    private sealed record ProjectedClaim(
        string ProviderClaimKey,
        int SourceEntryId,
        int ClaimPrecedence,
        long ClaimOrder,
        CatalogSourceKind SourceKind,
        string Name,
        string? Region,
        string? Language,
        string? Revision,
        int? AssertedTitleId,
        IReadOnlyList<CatalogSourceRequirementSnapshot> Requirements,
        IReadOnlyList<int> RegionIds,
        IReadOnlyList<int> LanguageIds);
}
