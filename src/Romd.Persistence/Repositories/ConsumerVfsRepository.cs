using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Paths;
using Romd.Domain.Source.Rom;
using Romd.Domain.Vfs;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerVfsRepository : IConsumerVfsRepository
{
    private const int _maximumPageFileFacts = 20_000;
    private readonly RomdDbContext _context;
    private readonly LiveConsumerLibraryQuery _library;
    private readonly IRomContentCatalog _contentCatalog;
    private readonly IRomChdDeliveryAdmission? _chdAdmission;
    private readonly RomDeliveryPolicy _deliveryPolicy;

    public ConsumerVfsRepository(RomdDbContext context, RomDeliveryPolicy deliveryPolicy,
        IRomContentCatalog contentCatalog, IRomChdDeliveryAdmission? chdAdmission = null)
    {
        _context = context;
        _library = new(context);
        _contentCatalog = contentCatalog;
        _chdAdmission = chdAdmission;
        _deliveryPolicy = deliveryPolicy;
    }

    public Task<ConsumerLibraryReadResult<ConsumerVfsPlatformPage>> ListPlatformsAsync(
        ConsumerLibraryScope scope, int afterPlatformId = 0, int limit = 100, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterPlatformId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConsumerVfsRepository.MaximumPageSize);
        return _library.ReadAsync<ConsumerVfsPlatformPage>(scope, async (libraryId, token) =>
        {
            var platforms = await VisiblePlatforms(libraryId).Where(row => row.Id > afterPlatformId)
                .OrderBy(row => row.Id).Select(row => new ConsumerVfsPlatform(row.Id, row.Name))
                .Take(limit + 1).ToListAsync(token);
            int? next = platforms.Count > limit ? platforms[limit - 1].Id : null;
            return new ConsumerLibraryProjectionResult<ConsumerVfsPlatformPage>.Found(
                new(platforms.Take(limit).ToArray(), next));
        }, ct);
    }

    public Task<ConsumerLibraryReadResult<ConsumerVfsPlatform>> GetPlatformAsync(
        ConsumerLibraryScope scope, int platformId, CancellationToken ct = default) =>
        _library.ReadAsync<ConsumerVfsPlatform>(scope, async (libraryId, token) =>
        {
            var platform = await VisiblePlatforms(libraryId).Where(row => row.Id == platformId)
                .Select(row => new ConsumerVfsPlatform(row.Id, row.Name)).SingleOrDefaultAsync(token);
            return platform is null
                ? new ConsumerLibraryProjectionResult<ConsumerVfsPlatform>.ItemNotFound()
                : new ConsumerLibraryProjectionResult<ConsumerVfsPlatform>.Found(platform);
        }, ct);

    private IQueryable<PlatformEntity> VisiblePlatforms(int libraryId) =>
        _context.Platforms.Where(platform => _context.MaterializedLibraryReleases.Any(row =>
            row.LibraryId == libraryId && row.PlatformId == platform.Id && row.CatalogReleaseId != null &&
            row.IsOwned && row.IsExposed));

    public Task<ConsumerLibraryReadResult<ConsumerVfsReleasePage>> ListReleasesAsync(
        ConsumerLibraryScope scope, int platformId, int afterReleaseId = 0, int limit = 100, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterReleaseId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IConsumerVfsRepository.MaximumPageSize);
        return _library.ReadAsync<ConsumerVfsReleasePage>(scope,
            (libraryId, token) => ListForLibraryAsync(libraryId, platformId, afterReleaseId, limit, token, format), ct);
    }

    public Task<ConsumerLibraryReadResult<ConsumerVfsRelease>> GetReleaseAsync(
        ConsumerLibraryScope scope, int releaseId, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored) =>
        _library.ReadAsync<ConsumerVfsRelease>(scope,
            (libraryId, token) => GetForLibraryAsync(libraryId, releaseId, token, format), ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerVfsReleasePage>> ListForLibraryAsync(
        int libraryId, int platformId, int afterReleaseId, int limit, CancellationToken ct, VfsFormat format)
    {
        // Query ids before loading names or files; even an enormous library stays bounded.
        var ids = await _context.MaterializedLibraryReleases
            .Where(row => row.LibraryId == libraryId && row.PlatformId == platformId && row.CatalogReleaseId > afterReleaseId &&
                          row.IsOwned && row.IsExposed)
            .Select(row => row.CatalogReleaseId!.Value).Distinct().Order()
            .Take(limit + 1).ToListAsync(ct);

        var platform = await _context.Platforms.Where(row => row.Id == platformId)
            .Select(row => new { row.Name, row.CanonicalKey }).SingleOrDefaultAsync(ct);
        var summaries = new List<ConsumerVfsReleaseSummary>(limit);
        if (ids.Count > 0)
        {
            // Include hidden duplicate rows when checking projection consistency.
            var projections = await _context.MaterializedLibraryReleases
                .Where(row => row.LibraryId == libraryId && row.CatalogReleaseId != null &&
                              ids.Contains(row.CatalogReleaseId.Value))
                .GroupBy(row => row.CatalogReleaseId!.Value)
                .Select(group => new
                {
                    Id = group.Key,
                    Count = group.Count(),
                    TitleId = group.Min(row => row.TitleId),
                    PlatformId = group.Min(row => row.PlatformId)
                }).ToDictionaryAsync(row => row.Id, ct);
            var catalog = await _context.CatalogReleases.Where(row => ids.Contains(row.Id))
                .Select(row => new { row.Id, row.CatalogTitleId, row.PlatformId, row.Name })
                .ToDictionaryAsync(row => row.Id, ct);
            // Count in SQL before materializing any paths. A release is indivisible for format projection.
            var candidates = ids.Take(limit).ToArray();
            var fileCounts = await _context.CatalogReleases.TagWith("VFS release file counts")
                .Where(release => candidates.Contains(release.Id))
                .Select(release => new
                {
                    release.Id,
                    Count = _context.CatalogReleaseFiles.Where(file => file.CatalogReleaseId == release.Id)
                        .OrderBy(file => file.Name).Take(ConsumerVfsReleaseTree.MaximumFiles + 1).Count()
                })
                .ToDictionaryAsync(row => row.Id, row => row.Count, ct);
            int factCount = 0;
            foreach (int id in ids)
            {
                var projection = projections[id];
                if (platform is null || projection.Count != 1 || !catalog.TryGetValue(id, out var release) ||
                    release.CatalogTitleId != projection.TitleId || release.PlatformId != projection.PlatformId)
                {
                    return new ConsumerLibraryProjectionResult<ConsumerVfsReleasePage>.ProjectionInconsistent();
                }

                if (summaries.Count < limit)
                {
                    int count = fileCounts.GetValueOrDefault(id);
                    bool projectable = count > 0 && count <= ConsumerVfsReleaseTree.MaximumFiles;
                    if (projectable && factCount + count > _maximumPageFileFacts)
                        break;
                    if (projectable)
                        factCount += count;
                    summaries.Add(new(id, release.PlatformId, release.Name, platform!.Name, platform.CanonicalKey));
                }
            }

            // Oversized and empty releases retain their summary with no paths, so layouts omit them
            // and keyset cursors advance even when an entire page is unprojectable.
            var listed = summaries.Where(summary => fileCounts.GetValueOrDefault(summary.Id) is > 0 and <= ConsumerVfsReleaseTree.MaximumFiles)
                .Select(summary => summary.Id).ToArray();
            var facts = await PathFacts().Where(file => listed.Contains(file.ReleaseId)).ToListAsync(ct);
            var chdDescriptors = await DescribePathFactsAsync(facts, ct);
            var factsByRelease = facts.ToLookup(file => file.ReleaseId);
            var paths = new Dictionary<int, IReadOnlyList<string>>();
            foreach (var summary in summaries)
            {
                var projected = VfsFormatProjection.Project(factsByRelease[summary.Id]
                    .Select(file => Member(file, chdDescriptors, summary.SystemKey)).ToArray(), format,
                    _deliveryPolicy, summary.SystemKey);
                if (projected.IsError)
                    return new ConsumerLibraryProjectionResult<ConsumerVfsReleasePage>.Rejected(projected.FirstError);
                paths[summary.Id] = projected.Value.Select(member => member.RelativePath).ToArray();
            }

            return new ConsumerLibraryProjectionResult<ConsumerVfsReleasePage>.Found(
                new(summaries.AsReadOnly(), ids.Count > summaries.Count ? summaries[^1].Id : null, paths));
        }
        return new ConsumerLibraryProjectionResult<ConsumerVfsReleasePage>.Found(
            new([], null, new Dictionary<int, IReadOnlyList<string>>()));
    }

    private async Task<ConsumerLibraryProjectionResult<ConsumerVfsRelease>> GetForLibraryAsync(
        int libraryId, int releaseId, CancellationToken ct, VfsFormat format)
    {
        var summary = await GetSummaryAsync(libraryId, releaseId, ct);
        if (summary is not ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.Found found)
            return Propagate<ConsumerVfsRelease>(summary);
        var files = await _context.CatalogReleaseFiles.Where(row => row.CatalogReleaseId == releaseId)
            .OrderBy(row => row.Id).Select(row => new { row.Name, row.Size, row.Sha1 })
            .Take(ConsumerVfsReleaseTree.MaximumFiles + 1).ToListAsync(ct);
        if (files.Count == 0 || files.Count > ConsumerVfsReleaseTree.MaximumFiles)
        {
            return new ConsumerLibraryProjectionResult<ConsumerVfsRelease>.ProjectionInconsistent();
        }

        var hashes = files.Where(row => row.Sha1 != null).Select(row => row.Sha1!.Value).Distinct().ToArray();
        var owned = await _context.RomContent().Where(rom => hashes.Contains(rom.Sha1)).ToDictionaryAsync(rom => rom.Sha1, ct);
        if (files.Any(file => file.Sha1 is not Sha1 hash || !owned.ContainsKey(hash)))
            return new ConsumerLibraryProjectionResult<ConsumerVfsRelease>.ItemNotFound();
        var descriptors = await DescribeAsync(owned.Values.ToArray(), ct);
        if (owned.Values.Any(row => !descriptors.ContainsKey(row.RomId)))
            return new ConsumerLibraryProjectionResult<ConsumerVfsRelease>.ProjectionInconsistent();
        var projected = VfsFormatProjection.Project(files.Select(file =>
            Member(descriptors[owned[file.Sha1!.Value].RomId], file.Name, found.Value.SystemKey)).ToArray(), format,
            _deliveryPolicy, found.Value.SystemKey);
        if (projected.IsError)
            return new ConsumerLibraryProjectionResult<ConsumerVfsRelease>.Rejected(projected.FirstError);
        var treeFiles = new List<ConsumerVfsFile>(files.Count);
        for (int index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var content = owned[file.Sha1!.Value];
            if (file.Size != content.ContentSize)
                throw new InvalidDataException("VFS catalog length disagrees with stored content metadata.");
            var resolved = ResolveFile(content, descriptors[content.RomId], projected.Value[index], found.Value.SystemKey);
            if (resolved is null)
                return new ConsumerLibraryProjectionResult<ConsumerVfsRelease>.ProjectionInconsistent();
            treeFiles.Add(resolved);
        }

        var tree = ConsumerVfsReleaseTree.Create(treeFiles);
        return tree.IsError
            ? new ConsumerLibraryProjectionResult<ConsumerVfsRelease>.Rejected(tree.FirstError)
            : new ConsumerLibraryProjectionResult<ConsumerVfsRelease>.Found(
                new(found.Value, tree.Value));
    }

    private async Task<ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>> GetSummaryAsync(
        int libraryId, int releaseId, CancellationToken ct)
    {
        var projections = await _context.MaterializedLibraryReleases
            .Where(row => row.LibraryId == libraryId && row.CatalogReleaseId == releaseId)
            .Select(row => new { row.TitleId, row.PlatformId, row.IsOwned, row.IsExposed })
            .Take(2).ToListAsync(ct);
        if (projections.Count == 0)
        {
            return new ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.ItemNotFound();
        }

        if (projections.Count != 1)
        {
            return new ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.ProjectionInconsistent();
        }

        var projection = projections[0];
        if (!projection.IsOwned || !projection.IsExposed)
        {
            return new ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.ItemNotFound();
        }

        var release = await _context.CatalogReleases.Where(row => row.Id == releaseId)
            .Select(row => new { row.CatalogTitleId, row.PlatformId, row.Name }).SingleOrDefaultAsync(ct);
        if (release is null || release.CatalogTitleId != projection.TitleId || release.PlatformId != projection.PlatformId)
        {
            return new ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.ProjectionInconsistent();
        }

        var platform = await _context.Platforms.Where(row => row.Id == release.PlatformId)
            .Select(row => new { row.Name, row.CanonicalKey }).SingleOrDefaultAsync(ct);
        if (platform is null)
            return new ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.ProjectionInconsistent();

        return new ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.Found(
            new(releaseId, release.PlatformId, release.Name, platform.Name, platform.CanonicalKey));
    }

    private static ConsumerLibraryProjectionResult<T> Propagate<T>(
        ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary> result) where T : notnull => result switch
        {
            ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.ItemNotFound => new ConsumerLibraryProjectionResult<T>.ItemNotFound(),
            ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.ProjectionInconsistent => new ConsumerLibraryProjectionResult<T>.ProjectionInconsistent(),
            _ => throw new InvalidOperationException("Expected an unsuccessful release summary.")
        };

    public Task<ConsumerLibraryReadResult<ConsumerVfsResolvedFile>> GetFileAsync(
        ConsumerLibraryScope scope, int releaseId, string relativePath, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored) =>
        _library.ReadAsync<ConsumerVfsResolvedFile>(scope, async (libraryId, token) =>
        {
            if (!PortableRelativePath.TryCreate(relativePath, out _))
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.ItemNotFound();
            var summary = await GetSummaryAsync(libraryId, releaseId, token);
            if (summary is not ConsumerLibraryProjectionResult<ConsumerVfsReleaseSummary>.Found found)
                return Propagate<ConsumerVfsResolvedFile>(summary);
            // One bounded path/fact scan chooses the exact source name; projected extensions are never aliases.
            var facts = await PathFacts().Where(file => file.ReleaseId == releaseId)
                .Take(ConsumerVfsReleaseTree.MaximumFiles + 1).ToListAsync(token);
            if (facts.Any(file => !file.Stored))
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.ItemNotFound();
            if (facts.Count == 0 || facts.Count > ConsumerVfsReleaseTree.MaximumFiles)
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.Rejected(ConsumerVfsErrors.InvalidLayout());
            var chdDescriptors = await DescribePathFactsAsync(facts, token);
            var projected = VfsFormatProjection.Project(facts.Select(file => Member(file, chdDescriptors, found.Value.SystemKey)).ToArray(),
                format, _deliveryPolicy, found.Value.SystemKey);
            if (projected.IsError)
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.Rejected(projected.FirstError);
            var selected = projected.Value.Where(member => member.RelativePath == relativePath).Take(2).ToArray();
            if (selected.Length == 0)
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.ItemNotFound();
            if (selected.Length != 1)
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.Rejected(ConsumerVfsErrors.InvalidLayout());
            string sourceName = selected[0].SourcePath;
            // The indexed content lookup still materializes at most two rows, never the release tree.
            var candidates = await (from file in _context.CatalogReleaseFiles
                                    where file.CatalogReleaseId == releaseId && file.Name == sourceName
                                    join rom in _context.RomContent() on file.Sha1 equals (Sha1?)rom.Sha1
                                    select new { CatalogSize = file.Size, Content = rom }).Take(2).ToListAsync(token);
            if (candidates.Count == 0)
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.ItemNotFound();
            if (candidates.Count != 1)
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.Rejected(ConsumerVfsErrors.InvalidLayout());
            var candidate = candidates[0];
            if (candidate.Content.ContentSize != candidate.CatalogSize)
                throw new InvalidDataException("VFS catalog length disagrees with stored content metadata.");
            var descriptors = await DescribeAsync([candidate.Content], token);
            if (!descriptors.TryGetValue(candidate.Content.RomId, out var descriptor))
                return new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.ProjectionInconsistent();
            var resolved = ResolveFile(candidate.Content, descriptor, selected[0], found.Value.SystemKey);
            return resolved is null ? new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.ProjectionInconsistent()
                : new ConsumerLibraryProjectionResult<ConsumerVfsResolvedFile>.Found(new(found.Value, resolved,
                    projected.Value.Select(member => member.RelativePath).ToArray()));
        }, ct);

    private IQueryable<PathFact> PathFacts() => _context.CatalogReleaseFiles.TagWith("VFS path facts").Select(file => new PathFact
    {
        ReleaseId = file.CatalogReleaseId,
        Name = file.Name,
        Sha1 = file.Sha1,
        Size = file.Size,
        Stored = file.Sha1 != null && _context.RomFiles.Any(rom => (Sha1?)rom.Sha1 == file.Sha1),
        OriginalIsRvz = file.Sha1 != null && _context.RomContent().Any(rom => (Sha1?)rom.Sha1 == file.Sha1 && rom.Encoding == RomRepresentationEncoding.Rvz),
        RvzAvailable = file.Sha1 != null && _context.RomArtifacts().Any(artifact =>
            artifact.Encoding == RomRepresentationEncoding.Rvz && artifact.HasMeasuredEvidence &&
            _context.RomFiles.Any(rom => rom.Id == artifact.RomId && (Sha1?)rom.Sha1 == file.Sha1))
    });

    private async Task<Dictionary<int, RomContentDescriptor>> DescribeAsync(IReadOnlyList<RomContentRow> rows, CancellationToken ct)
    {
        var descriptors = new Dictionary<int, RomContentDescriptor>();
        foreach (int[] batch in rows.Select(row => row.RomId).Distinct().Chunk(IRomContentCatalog.MaximumBatchSize))
            foreach (var content in await _contentCatalog.FindByRomIdsAsync(batch, ct))
                if (rows.Any(row => row.RomId == content.Key && MatchesOwnedRow(content.Value, row)))
                    descriptors.Add(content.Key, content.Value);
        return descriptors;
    }

    private async Task<Dictionary<Sha1, RomContentDescriptor>> DescribePathFactsAsync(IReadOnlyList<PathFact> facts, CancellationToken ct)
    {
        var hashes = facts.Where(fact => fact.Sha1 != null).Select(fact => fact.Sha1!.Value).Distinct().ToArray();
        var rows = await _context.RomContent().Where(row => hashes.Contains(row.Sha1)).ToListAsync(ct);
        var descriptors = await DescribeAsync(rows, ct);
        return rows.Where(row => descriptors.ContainsKey(row.RomId))
            .ToDictionary(row => row.Sha1, row => descriptors[row.RomId]);
    }

    private VfsReleaseMember Member(PathFact fact, IReadOnlyDictionary<Sha1, RomContentDescriptor> descriptors, string? systemKey) =>
        fact.Sha1 is { } hash && descriptors.TryGetValue(hash, out var content) && content.ContentSize == fact.Size
            ? Member(content, fact.Name, systemKey) : fact.Member;

    private VfsReleaseMember Member(RomContentDescriptor content, string path, string? systemKey)
    {
        var kind = MediaKindResolver.Resolve(_deliveryPolicy.Kinds, content, [systemKey]);
        var formats = RomDeliveryPlanner.Plan(content, path, _deliveryPolicy, kind, _chdAdmission);
        var chd = formats.SingleOrDefault(option => option.Format == RomDeliveryFormat.Chd);
        // Only a uniquely resolved whole-file kind whose runtimes read its stored CHD natively projects a stored .chd, and
        // only under the current canonical recipe. Readable CHDs from an earlier recipe (chd-dvd-canonical/v1, zlib)
        // project as their original until re-converged; the planner still delivers them.
        bool chdAvailable = kind.Unique is { ManifestType: MediaManifestType.WholeFile, StoredEncoding: RomRepresentationEncoding.Chd } unique
            && chd?.State == RomDeliveryState.Available && chd.Target is { } target && RomDeliveryTargets.IsValid(target)
            && target.ReadBinding is RomChdDeliveryBinding dvdBinding && chd.RuntimeApplicability.Count > 0
            && content.Artifacts!.Where(artifact => artifact.FileId == target.FileId && artifact.RepresentationId == dvdBinding.RepresentationId)
                .SelectMany(artifact => artifact.ChdEvidence ?? [])
                .Any(evidence => evidence.Bind() == target.ReadBinding &&
                    _deliveryPolicy.IsCanonicalChd(unique.Kind, evidence.Container));
        // Projection eligibility is separate from admitted downloads: fallback retains its original VFS namespace.
        bool rvzProjectable = kind.Outcome != MediaKindOutcome.Fallback &&
            formats.Any(option => option.Format == RomDeliveryFormat.Rvz && option.State == RomDeliveryState.Available);
        return new(path, content.Encoding == RomRepresentationEncoding.Rvz,
            rvzProjectable,
            content.Encoding == RomRepresentationEncoding.Chd && formats[0].State == RomDeliveryState.Available, chdAvailable);
    }

    private static bool MatchesOwnedRow(RomContentDescriptor content, RomContentRow row) =>
        content.RomId == row.RomId && content.FileId == row.FileId && content.RepresentationId == row.RepresentationId
        && content.ContentSha256 == row.ContentSha256 && content.ContentSize == row.ContentSize
        && content.ContentMd5 == row.Md5 && content.ContentSha1 == row.Sha1 && content.ContentCrc32 == row.Crc32
        && content.ArtifactSha256 == row.ArtifactSha256 && content.ArtifactSize == row.ArtifactSize
        && content.Encoding == row.Encoding && content.OutputId == row.OutputId;

    private ConsumerVfsFile? ResolveFile(RomContentRow content, RomContentDescriptor descriptor, VfsProjectedMember member, string? systemKey)
    {
        var target = RomDeliveryPlanner.Plan(descriptor, member.SourcePath, _deliveryPolicy,
                MediaKindResolver.Resolve(_deliveryPolicy.Kinds, descriptor, [systemKey]), _chdAdmission)
            .SingleOrDefault(option => option.Format == member.Format)?.Target;
        return target is null ? null : new(member.RelativePath, target.Sha256, target.SizeBytes,
            target.Md5 ?? default, target.Sha1 ?? default, content.CreatedAt, target);
    }

    private sealed class PathFact
    {
        public int ReleaseId { get; init; }
        public string Name { get; init; } = "";
        public Sha1? Sha1 { get; init; }
        public long Size { get; init; }
        public bool Stored { get; init; }
        public bool OriginalIsRvz { get; init; }
        public bool RvzAvailable { get; init; }
        public VfsReleaseMember Member => new(Name, OriginalIsRvz, RvzAvailable);
    }
}
