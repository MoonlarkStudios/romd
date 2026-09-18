using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerReleaseManifestRepository(RomdDbContext context) : IConsumerReleaseManifestRepository
{
    private static readonly HashSet<string> PortableReservedPathSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);

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

    private async Task<ConsumerLibraryProjectionResult<ConsumerReleaseManifest>> GetManifestForLibraryAsync(
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

        var items = await BuildManifestItemsAsync(releaseId, ct);

        return new ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.Found(
            new ConsumerReleaseManifest(
                userId,
                projection.TitleId,
                projection.PlatformId,
                catalogRelease.PlatformShortName,
                catalogRelease.Id,
                catalogRelease.Name,
                catalogRelease.Revision,
                projection.IsComplete,
                CreateRuntime(items),
                items));
    }

    private async Task<IReadOnlyList<ConsumerReleaseManifestItem>> BuildManifestItemsAsync(
        int catalogReleaseId,
        CancellationToken ct)
    {
        var files = await context.CatalogReleaseFiles
            .AsNoTracking()
            .Where(file => file.CatalogReleaseId == catalogReleaseId)
            .OrderBy(file => file.Name)
            .ThenBy(file => file.Id)
            .Select(file => new FileRow(file.Name, file.Size, file.Sha1, file.IsDisk))
            .ToListAsync(ct);

        var requiredSha1s = files
            .Where(file => file.Sha1 is not null)
            .Select(file => file.Sha1!.Value)
            .Distinct()
            .ToList();

        // Availability is hash-based: a required file is owned when a stored RomFile shares its SHA-1.
        var ownedBySha1 = requiredSha1s.Count == 0
            ? new Dictionary<Sha1, OwnedFile>()
            : await (
                    from romFile in context.RomFiles.AsNoTracking()
                    where requiredSha1s.Contains(romFile.Sha1)
                    join storedFile in context.Files.AsNoTracking()
                        on romFile.FileId equals storedFile.Id
                    select new OwnedFile(romFile.Sha1, romFile.Id, storedFile.Id, storedFile.Sha256, storedFile.Size))
                .ToDictionaryAsync(owned => owned.Sha1, ct);

        return files.Select(file => ToManifestItem(file, ownedBySha1)).ToList();
    }

    private static ConsumerReleaseManifestItem ToManifestItem(
        FileRow file,
        IReadOnlyDictionary<Sha1, OwnedFile> ownedBySha1)
    {
        OwnedFile? owned = file.Sha1 is { } sha1 && ownedBySha1.TryGetValue(sha1, out var match)
            ? match
            : null;

        return new ConsumerReleaseManifestItem(
            owned?.RomFileId,
            owned?.FileId,
            owned?.Sha256,
            file.Size,
            owned?.ContentSizeBytes,
            ToSafeRelativePath(file.Name),
            file.IsDisk ? "disk" : "rom",
            owned is not null);
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
    private static readonly string[] EntryPointExtensionPriority = [".m3u", ".cue", ".gdi", ".chd", ".iso"];

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
        EntryPointExtensionPriority
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
        const string portableUnsafeCharacters = "<>:\"|?*";
        char[] characters = segment
            .Select(character =>
                char.IsControl(character) || portableUnsafeCharacters.Contains(character)
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
        string deviceName = sanitized.Split('.', 2)[0];
        return PortableReservedPathSegments.Contains(deviceName) ? $"_{sanitized}" : sanitized;
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

    private sealed record FileRow(
        string Name,
        long Size,
        Sha1? Sha1,
        bool IsDisk);

    private sealed record OwnedFile(
        Sha1 Sha1,
        int RomFileId,
        int FileId,
        Sha256 Sha256,
        long ContentSizeBytes);
}
