using ErrorOr;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Import.Queries.ListImportFolders;
using Romd.Application.Common.Configuration;
using Romd.Contracts.Management.Models;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Lists folders for the path-import picker. The requested folder passes the import validator
///     (absolute, canonically inside an allowed root, no links on any component); each child is
///     listed only when it is a real directory, never a link, so following a listed path cannot
///     leave the allowed roots. Hidden folders such as snapshots are omitted.
/// </summary>
public sealed class ImportFolderBrowser(IRomdOptions options, IPathValidator validator) : IImportFolderBrowser
{
    internal const int MaxFolders = 500;
    internal const int MaxCountedFiles = 100_000;

    private static readonly EnumerationOptions Children = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false
    };

    public ErrorOr<ImportFolderListing> List(string? path, CancellationToken ct = default)
    {
        string[] roots = options.AllowedImportPaths;
        if (roots.Length == 0)
            return new ImportFolderListing { Enabled = false, Folders = [], FileCount = 0, Truncated = false };
        return path is null ? ListRoots(roots) : ListFolder(path, ct);
    }

    private ImportFolderListing ListRoots(string[] roots)
    {
        var folders = roots
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(StringComparer.Ordinal)
            .Where(root => !validator.ValidateImportRoot(root).IsError)
            .Select(root => new ImportFolder { Name = root, Path = root })
            .ToArray();
        return new ImportFolderListing { Enabled = true, Folders = folders, FileCount = 0, Truncated = false };
    }

    private ErrorOr<ImportFolderListing> ListFolder(string path, CancellationToken ct)
    {
        var validated = validator.ValidateImportRoot(path);
        if (validated.IsError)
            return validated.Errors;
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = validated.Value;

        var folders = new List<ImportFolder>();
        bool truncated = false;
        foreach (var child in new DirectoryInfo(folder).EnumerateDirectories("*", Children))
        {
            ct.ThrowIfCancellationRequested();
            if (child.Name.StartsWith('.') || child.LinkTarget is not null)
                continue;
            if (folders.Count == MaxFolders)
            {
                truncated = true;
                break;
            }
            folders.Add(new ImportFolder { Name = child.Name, Path = Path.Combine(folder, child.Name) });
        }

        int files = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Children))
        {
            ct.ThrowIfCancellationRequested();
            if (file.LinkTarget is null && ++files == MaxCountedFiles)
                break;
        }

        // A component swapped for a link while listing would have exposed names outside the roots;
        // refuse rather than return them. Imports of listed paths are revalidated independently.
        var revalidated = validator.ValidateImportRoot(path);
        if (revalidated.IsError)
            return revalidated.Errors;

        return new ImportFolderListing
        {
            Enabled = true,
            Path = folder,
            ParentPath = folder == root ? null : Path.GetDirectoryName(folder),
            Folders = folders.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            FileCount = files,
            Truncated = truncated
        };
    }
}
