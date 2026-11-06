using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Paths;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerReleaseManifestRepository(RomdDbContext context, RomDeliveryPolicy deliveryPolicy,
    IRomContentCatalog contentCatalog, IDerivedDescriptorReader derivedDescriptors, IRomChdDeliveryAdmission? chdAdmission) : IConsumerReleaseManifestRepository
{
    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);
    private readonly IRomContentCatalog _contentCatalog = contentCatalog;

    public Task<ConsumerLibraryReadResult<ConsumerReleaseManifest>> GetManifestAsync(
        ConsumerReleaseManifestRequest request,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<ConsumerReleaseManifest>(
            request.Scope,
            (libraryId, token) => GetManifestForLibraryAsync(
                request.Scope.UserId,
                libraryId,
                request.ReleaseId,
                token),
            ct);

    internal async Task<ConsumerLibraryProjectionResult<ConsumerReleaseManifest>> GetManifestForLibraryAsync(
        Guid userId,
        int libraryId,
        int releaseId,
        CancellationToken ct)
    {
        // Materialized release ids are not protected by a database FK. Treat any duplicate,
        // orphan, or title/platform disagreement as unavailable instead of selecting a row.
        var projections = await context.MaterializedLibraryReleases
            .AsNoTracking()
            .Where(release =>
                release.LibraryId == libraryId &&
                release.CatalogReleaseId == releaseId)
            .Select(release => new ProjectionRow(
                release.TitleId,
                release.PlatformId,
                release.IsOwned,
                release.IsExposed,
                release.IsComplete))
            .Take(2)
            .ToListAsync(ct);

        if (projections.Count == 0)
        {
            return new ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.ItemNotFound();
        }

        if (projections.Count != 1)
        {
            return new ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.ProjectionInconsistent();
        }

        var projection = projections[0];
        if (!projection.IsOwned || !projection.IsExposed)
        {
            return new ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.ItemNotFound();
        }

        var catalogRows = await (
                from catalogReleaseEntity in context.CatalogReleases.AsNoTracking()
                where catalogReleaseEntity.Id == releaseId
                join platform in context.Platforms.AsNoTracking()
                    on catalogReleaseEntity.PlatformId equals platform.Id
                select new CatalogReleaseRow(
                    catalogReleaseEntity.CatalogTitleId,
                    catalogReleaseEntity.PlatformId,
                    platform.CanonicalKey!,
                    catalogReleaseEntity.Id,
                    catalogReleaseEntity.Name,
                    catalogReleaseEntity.Revision))
            .Take(2)
            .ToListAsync(ct);

        if (catalogRows.Count != 1 ||
            catalogRows[0].TitleId != projection.TitleId ||
            catalogRows[0].PlatformId != projection.PlatformId)
        {
            return new ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.ProjectionInconsistent();
        }

        var catalogRelease = catalogRows[0];

        var items = await BuildManifestItemsAsync(releaseId, catalogRelease.PlatformShortName, ct);

        return new ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.Found(
            new ConsumerReleaseManifest(
                userId,
                projection.TitleId,
                projection.PlatformId,
                catalogRelease.PlatformShortName,
                catalogRelease.Id,
                catalogRelease.Name,
                catalogRelease.Revision,
                projection.IsComplete && items.All(item => item.Source is ConsumerReleaseManifestSource.Original),
                CreateRuntime(items),
                items));
    }

    private async Task<IReadOnlyList<ConsumerReleaseManifestItem>> BuildManifestItemsAsync(
        int catalogReleaseId,
        string systemKey,
        CancellationToken ct)
    {
        var files = await context.CatalogReleaseFiles
            .AsNoTracking()
            .Where(file => file.CatalogReleaseId == catalogReleaseId)
            .OrderBy(file => file.Name)
            .ThenBy(file => file.Id)
            .Select(file => new FileRow(file.Id, file.Name, file.Size, file.Sha1, file.Md5, file.Crc, file.IsDisk))
            .ToListAsync(ct);

        var requiredSha1s = files
            .Where(file => file.Sha1 is not null)
            .Select(file => file.Sha1!.Value)
            .Distinct()
            .ToList();

        // SHA1 bounds the lookup; each candidate must satisfy the complete available DAT requirement.
        var ownedRows = requiredSha1s.Count == 0 ? []
            : await context.RomContent().Where(row => requiredSha1s.Contains(row.Sha1)).ToListAsync(ct);
        var romIds = ownedRows.Select(row => row.RomId).ToArray();
        var descriptions = new Dictionary<int, RomContentDescriptor>();
        foreach (int[] batch in romIds.Distinct().Chunk(IRomContentCatalog.MaximumBatchSize))
            foreach (var description in await _contentCatalog.FindByRomIdsAsync(batch, ct))
                descriptions.Add(description.Key, description.Value);
        var ownedFiles = new List<OwnedFile>();
        foreach (var row in ownedRows)
        {
            var content = descriptions.GetValueOrDefault(row.RomId);
            if (content is null || !MatchesOwnedRow(content, row))
                continue;
            ownedFiles.Add(new(row.Sha1, row.RomId, row.FileId, row.ContentSha256, row.ContentSize, content));
        }

        var ownedBySha1 = ownedFiles.GroupBy(file => file.Sha1).ToDictionary(group => group.Key, group => group.ToArray());
        var derived = (await derivedDescriptors.ReadForReleaseAsync(catalogReleaseId, ct)).ToDictionary(source => source.CatalogFileId);
        return files.Select(file => ToManifestItem(file, ownedBySha1, derived, systemKey, singleFile: files.Count == 1)).ToList();
    }

    private static bool MatchesOwnedRow(RomContentDescriptor content, RomContentRow row) =>
        content.RomId == row.RomId && content.FileId == row.FileId && content.RepresentationId == row.RepresentationId
        && content.ContentSha256 == row.ContentSha256 && content.ContentSize == row.ContentSize
        && content.ContentMd5 == row.Md5 && content.ContentSha1 == row.Sha1 && content.ContentCrc32 == row.Crc32
        && content.ArtifactSha256 == row.ArtifactSha256 && content.ArtifactSize == row.ArtifactSize && content.Encoding == row.Encoding;

    private ConsumerReleaseManifestItem ToManifestItem(
        FileRow file,
        IReadOnlyDictionary<Sha1, OwnedFile[]> ownedBySha1,
        IReadOnlyDictionary<int, DerivedDescriptorBinding> derived,
        string systemKey,
        bool singleFile)
    {
        var candidates = file.Sha1 is { } sha1 && ownedBySha1.TryGetValue(sha1, out var matches)
            ? matches.Where(owned => owned.ContentSizeBytes == file.Size &&
                (file.Md5 is null || owned.Content.ContentMd5 == file.Md5) &&
                (file.Crc32 is null || owned.Content.ContentCrc32 == file.Crc32)).Take(2).ToArray() : [];
        OwnedFile? owned = candidates is [var exact] ? exact : null;

        string relativePath = ToSafeRelativePath(file.Name);
        var kind = owned is null ? null : MediaKindResolver.Resolve(deliveryPolicy.Kinds, owned.Content, [systemKey]);
        // A lone file whose kind resolves to a whole-file kind describes its own layout in each format.
        bool storedDelivery = singleFile && kind?.Unique is { ManifestType: MediaManifestType.WholeFile };
        var formats = owned is null ? [] : RomDeliveryPlanner.Plan(owned.Content, relativePath, deliveryPolicy, kind, chdAdmission)
            .Select(option => ToManifestFormat(relativePath, option, storedDelivery, deliveryPolicy.Formats)).ToArray();
        ConsumerReleaseManifestSource source = owned is null ? new ConsumerReleaseManifestSource.Missing() : new ConsumerReleaseManifestSource.Original(owned.Content.Identity, formats);
        if ((source is ConsumerReleaseManifestSource.Missing or ConsumerReleaseManifestSource.Original { AvailableTarget: null }) && derived.TryGetValue(file.Id, out var descriptor) && descriptor.RelativePath == relativePath)
            source = new ConsumerReleaseManifestSource.Derived(descriptor);
        return new(relativePath, file.IsDisk ? "disk" : "rom", file.Size, source);
    }

    private static ConsumerReleaseManifestFormat ToManifestFormat(string originalPath, RomDeliveryOption option,
        bool singleFileWithStoredDelivery, RomDeliveryFormatCatalog formats)
    {
        string path = option.Format == RomDeliveryFormat.Original
            ? originalPath
            : Path.ChangeExtension(originalPath, formats.FileExtension(option.Format));
        var runtime = singleFileWithStoredDelivery && option.State == RomDeliveryState.Available && option.Target is { } target
            && RomDeliveryTargets.IsValid(target)
            ? new ConsumerReleaseRuntime("single_rom", new("file", path), "direct_files", target.SizeBytes)
            : null;
        return new(path, option, runtime);
    }

    private static ConsumerReleaseRuntime CreateRuntime(IReadOnlyList<ConsumerReleaseManifestItem> items)
    {
        long minimumInstallBytes = items.Sum(item => item.SizeBytes);
        bool hasSingleItem = items.Count == 1;

        return new ConsumerReleaseRuntime(
            hasSingleItem ? "single_rom" : "unknown",
            SelectLaunchTarget(items),
            Packaging: "direct_files",
            MinimumInstallBytes: minimumInstallBytes);
    }

    /// <summary>
    ///     Launch entry-point extensions in priority order. A playlist (m3u) or index sheet
    ///     (cue/gdi) IS the multi-file association authored by the content itself; ROMD preserves
    ///     co-location and DAT-exact file names, so choosing the entry point is all that is
    ///     needed — deliberately no cue parsing and no bin↔cue modeling.
    /// </summary>
    private static readonly string[] _entryPointExtensionPriority = [".m3u", ".cue", ".gdi", ".chd", ".iso"];

    /// <summary>
    ///     Picks the launch target over the release's declared file set regardless of
    ///     availability — the entry point is a property of the release's content shape, not of
    ///     what is currently owned. The first extension tier with a match wins; ties go to the
    ///     lexicographically first path so multi-disc sets deterministically pick
    ///     "(Disc 1)"-style names. Without a recognized entry point, a single-file release
    ///     launches its only file and a multi-file release has no launch target.
    /// </summary>
    private static ConsumerLaunchTarget? SelectLaunchTarget(IReadOnlyList<ConsumerReleaseManifestItem> items) =>
        SelectEntryPoint(items) is { } entryPoint
            ? new ConsumerLaunchTarget("file", entryPoint.RelativePath)
            : null;

    private static ConsumerReleaseManifestItem? SelectEntryPoint(IReadOnlyList<ConsumerReleaseManifestItem> items) =>
        _entryPointExtensionPriority
            .Select(extension => items
                .Where(item => HasExtension(item.RelativePath, extension))
                .MinBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(match => match is not null)
        ?? (items.Count == 1 ? items[0] : null);

    private static bool HasExtension(string relativePath, string extension) =>
        string.Equals(Path.GetExtension(relativePath), extension, StringComparison.OrdinalIgnoreCase);

    private static string ToSafeRelativePath(string originalFilename)
    {
        string normalized = originalFilename.Replace('\\', '/');
        string[] segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment != ".")
            .Select(segment => segment == ".." ? "_" : SanitizePathSegment(segment))
            .ToArray();

        return segments.Length == 0 ? "content.bin" : string.Join('/', segments);
    }

    private static string SanitizePathSegment(string segment)
    {
        char[] characters = segment
            .Select(character =>
                PortableRelativePath.IsUnsafeCharacter(character)
                    ? '_'
                    : character)
            .ToArray();

        if (characters.Length > 0 && (characters[^1] == '.' || characters[^1] == ' '))
        {
            characters[^1] = '_';
        }

        if (characters.Length == 0)
        {
            return "_";
        }

        string sanitized = new(characters);
        return PortableRelativePath.IsDeviceName(sanitized) ? $"_{sanitized}" : sanitized;
    }

    private sealed record ProjectionRow(
        int TitleId,
        int PlatformId,
        bool IsOwned,
        bool IsExposed,
        bool IsComplete);

    private sealed record CatalogReleaseRow(
        int TitleId,
        int PlatformId,
        string PlatformShortName,
        int Id,
        string Name,
        string? Revision);

    private sealed record FileRow(int Id, string Name, long Size, Sha1? Sha1, Md5? Md5, Crc32? Crc32, bool IsDisk);

    private sealed record OwnedFile(
        Sha1 Sha1,
        int RomFileId,
        int FileId,
        Sha256 Sha256,
        long ContentSizeBytes,
        RomContentDescriptor Content);
}
