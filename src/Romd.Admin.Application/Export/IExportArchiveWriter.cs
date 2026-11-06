using Romd.Application.Common.Content;

namespace Romd.Admin.Application.Export;

/// <param name="PlatformKeys">
///     The platforms the member is reached through, which resolve its media kind: the archive's own platform, or the platform
///     of each of the ROM's matches in an export with no single platform. None resolves from evidence alone.
/// </param>
public sealed record ExportArchiveEntry(int FileId, string Path, int? RomId = null, RomDeliveryTarget? Target = null,
    IReadOnlyList<string?>? PlatformKeys = null);

public interface IExportArchiveWriter
{
    Task WriteAsync(Stream destination, IReadOnlyList<ExportArchiveEntry> entries,
        Func<ExportArchiveEntry, bool, Task>? onEntry = null, bool skipMissing = false,
        CancellationToken ct = default);
}

/// <summary>Portable archive paths, independent of the server operating system.</summary>
public static class ExportArchivePaths
{
    public static string Normalize(string path)
    {
        if (!TryNormalize(path, out var normalized))
            throw new ArgumentException("Archive contains an unsafe relative path.", nameof(path));
        return normalized;
    }

    public static bool TryNormalize(string? path, out string normalized)
    {
        if (path is null)
        {
            normalized = string.Empty;
            return false;
        }
        normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
            segment.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c)) ||
            segment.EndsWith('.') || segment.EndsWith(' ')))
            return false;
        return true;
    }

    public static string SafeFilename(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c) ? '_' : c)).TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(safe) ? "download" : safe;
    }

    public static void Validate(IReadOnlyList<ExportArchiveEntry> entries)
    {
        if (!TryValidate(entries, out var error))
            throw new ArgumentException(error, nameof(entries));
    }

    public static bool TryValidate(IReadOnlyList<ExportArchiveEntry> entries, out string? error)
    {
        error = null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!TryNormalize(entry.Path, out var path))
            {
                error = "Archive contains an unsafe relative path.";
                return false;
            }
            if (entry.FileId <= 0 || !paths.Add(path))
            {
                error = "Archive contains duplicate paths or an invalid file.";
                return false;
            }
        }
        foreach (var path in paths)
        {
            var parent = path;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            {
                parent = parent[..slash];
                if (paths.Contains(parent))
                {
                    error = "Archive file conflicts with a directory.";
                    return false;
                }
            }
        }
        return true;
    }
}
