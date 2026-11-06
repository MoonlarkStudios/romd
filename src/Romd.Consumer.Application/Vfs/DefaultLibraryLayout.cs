using Romd.Domain.Paths;
using ErrorOr;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Vfs;

namespace Romd.Consumer.Application.Vfs;

/// <summary>
/// Default platform/release namespace, projected from the current authorized catalog.
/// Names follow current DAT-derived data; no historical path assignments are stored.
/// </summary>
public sealed class DefaultLibraryLayout : IVfsLayout
{
    public const string LayoutKey = "library";
    private readonly IConsumerVfsRepository _repository;

    public DefaultLibraryLayout(IConsumerVfsRepository repository) => _repository = repository;

    public string Key => LayoutKey;

    public async Task<ErrorOr<VfsLayoutRead>> ReadAsync(
        ConsumerLibraryScope scope, string path, bool includeChildren,
        int maximumChildren, CancellationToken ct = default, VfsFormat format = VfsFormat.Stored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChildren, 1);
        ct.ThrowIfCancellationRequested();
        if (path.Length == 0)
            return await ReadRootAsync(scope, includeChildren, maximumChildren, ct);

        string[] parts = path.Split('/', 3);
        if (!TryReadId(parts[0], out int platformId))
            return ConsumerVfsErrors.NotFound();
        if (parts.Length == 1)
            return await ReadPlatformAsync(scope, platformId, parts[0], includeChildren, maximumChildren, ct, format);
        if (!TryReadId(parts[1], out int releaseId))
            return ConsumerVfsErrors.NotFound();

        var read = await _repository.GetReleaseAsync(scope, releaseId, ct, format);
        if (read is not ConsumerLibraryReadResult<ConsumerVfsRelease>.Found found)
            return ConsumerVfsErrors.FromRead(read);

        ConsumerVfsRelease release = found.Value;
        string relativePath = parts.Length == 3 ? parts[2] : "";
        if (release.Summary.PlatformId != platformId ||
            parts[0] != Folder(platformId, release.Summary.PlatformName, "Platform") ||
            parts[1] != Folder(releaseId, release.Summary.Name, "Release") ||
            release.Tree.Find(relativePath) is not { } entry)
            return ConsumerVfsErrors.NotFound();

        string prefix = parts[0] + "/" + parts[1];
        IReadOnlyList<ConsumerVfsEntry> children = includeChildren && entry.IsDirectory
            ? release.Tree.List(relativePath)! : [];
        if (children.Count > maximumChildren)
            return ConsumerVfsErrors.EnumerationLimit();
        return new VfsLayoutRead(Entry(prefix, parts[1], entry),
            children.Select(child => Entry(prefix, parts[1], child)).ToArray());
    }

    public ConsumerVfsFileLocator? LocateFile(string path)
    {
        string[] parts = path.Split('/', 3);
        return parts.Length == 3 && TryReadId(parts[0], out _) && TryReadId(parts[1], out int releaseId)
            ? new(releaseId, parts[2])
            : null;
    }

    // Every projected path in the release must be portable.
    public string? FilePath(ConsumerVfsResolvedFile file) => PortablePathSet.Validate(file.ReleasePaths) is null
        ? Folder(file.Summary.PlatformId, file.Summary.PlatformName, "Platform") + "/" +
          Folder(file.Summary.Id, file.Summary.Name, "Release") + "/" + file.File.RelativePath
        : null;

    private async Task<ErrorOr<VfsLayoutRead>> ReadRootAsync(
        ConsumerLibraryScope scope, bool includeChildren, int maximumChildren, CancellationToken ct)
    {
        int cursor = 0;
        int? libraryId = null;
        var children = new List<VfsLayoutEntry>();
        do
        {
            ct.ThrowIfCancellationRequested();
            var read = await _repository.ListPlatformsAsync(scope, cursor, IConsumerVfsRepository.MaximumPageSize, ct);
            if (read is not ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found found)
                return ConsumerVfsErrors.FromRead(read);
            if (libraryId.HasValue && libraryId != found.LibraryId)
                return ConsumerVfsErrors.InconsistentProjection();
            libraryId = found.LibraryId;
            if (!includeChildren)
                break;
            foreach (ConsumerVfsPlatform platform in found.Value.Items)
            {
                if (children.Count >= maximumChildren)
                    return ConsumerVfsErrors.EnumerationLimit();
                string folder = Folder(platform.Id, platform.Name, "Platform");
                children.Add(new(folder, folder));
            }

            if (found.Value.NextPlatformId is not { } next)
                break;
            if (next <= cursor || found.Value.Items.Count == 0)
                return ConsumerVfsErrors.InconsistentProjection();
            cursor = next;
        } while (true);

        return new VfsLayoutRead(new("", "ROMD"), children.AsReadOnly());
    }

    private async Task<ErrorOr<VfsLayoutRead>> ReadPlatformAsync(
        ConsumerLibraryScope scope, int platformId, string path, bool includeChildren,
        int maximumChildren, CancellationToken ct, VfsFormat format)
    {
        var platformRead = await _repository.GetPlatformAsync(scope, platformId, ct);
        if (platformRead is not ConsumerLibraryReadResult<ConsumerVfsPlatform>.Found platform)
            return ConsumerVfsErrors.FromRead(platformRead);
        if (path != Folder(platform.Value.Id, platform.Value.Name, "Platform"))
            return ConsumerVfsErrors.NotFound();
        var children = new List<VfsLayoutEntry>();
        int cursor = 0;
        while (includeChildren)
        {
            ct.ThrowIfCancellationRequested();
            var read = await _repository.ListReleasesAsync(scope, platformId, cursor,
                IConsumerVfsRepository.MaximumPageSize, ct, format);
            if (read is not ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found found)
                return ConsumerVfsErrors.FromRead(read);
            if (found.LibraryId != platform.LibraryId)
                return ConsumerVfsErrors.InconsistentProjection();
            foreach (ConsumerVfsReleaseSummary release in found.Value.Items)
            {
                if (release.PlatformId != platformId || release.PlatformName != platform.Value.Name)
                    return ConsumerVfsErrors.InconsistentProjection();
                // Omit releases whose projected paths this layout cannot expose; the cursor still advances.
                if (PortablePathSet.Validate(found.Value.ReleasePaths.GetValueOrDefault(release.Id, [])) is not null)
                    continue;
                if (children.Count >= maximumChildren)
                    return ConsumerVfsErrors.EnumerationLimit();
                string folder = Folder(release.Id, release.Name, "Release");
                children.Add(new(path + "/" + folder, folder));
            }

            if (found.Value.NextReleaseId is not { } next)
                break;
            if (next <= cursor || found.Value.Items.Count == 0)
                return ConsumerVfsErrors.InconsistentProjection();
            cursor = next;
        }

        return new VfsLayoutRead(new(path, path), children.AsReadOnly());
    }

    private static bool TryReadId(string folder, out int id)
    {
        int separator = folder.IndexOf(' ');
        id = 0;
        return separator > 0 && IdCoder.TryDecode(folder[..separator], out id);
    }

    private static VfsLayoutEntry Entry(string folder, string name, ConsumerVfsEntry entry) =>
        new(folder + (entry.RelativePath.Length == 0 ? "" : "/" + entry.RelativePath),
            entry.RelativePath.Length == 0 ? name : entry.Name, entry.File);

    private static string Folder(int id, string sourceName, string fallback) =>
        IdCoder.Encode(id) + " " + PortablePathLabel.Create(sourceName, fallback);
}
