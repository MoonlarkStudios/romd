using ErrorOr;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Application.Common.Configuration;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Validates import paths with strict containment: absolute paths only, canonical (lexically
///     normalized) containment under a configured allowed root using a trailing-separator-safe
///     prefix compare, and rejection of symbolic links, junctions, and other reparse points on
///     every component from the allowed root down to the target. Callers re-run per-file
///     validation immediately before opening a file to close the path-swap window; the residual
///     handle-level race (an attacker with local filesystem write access swapping the file between
///     open and read) is documented as accepted.
/// </summary>
public sealed class PathValidator : IPathValidator
{
    private readonly IRomdOptions _options;

    public PathValidator(IRomdOptions options) => _options = options;

    public ErrorOr<string> ValidateImportRoot(string path) =>
        Validate(path, requireDirectory: true);

    public ErrorOr<Success> ValidateImportFile(string path)
    {
        var result = Validate(path, requireDirectory: false);
        return result.IsError ? result.Errors : Result.Success;
    }

    private ErrorOr<string> Validate(string path, bool requireDirectory)
    {
        string[] allowedRoots = _options.AllowedImportPaths;
        if (allowedRoots.Length == 0)
        {
            return ImportErrors.FeatureDisabled();
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return ImportErrors.PathNotAbsolute(path);
        }

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? containingRoot = allowedRoots
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .FirstOrDefault(root => IsContainedIn(fullPath, root));

        if (containingRoot is null)
        {
            return ImportErrors.PathNotAllowed(path);
        }

        foreach (string component in ComponentsFromRootDown(containingRoot, fullPath))
        {
            if (IsLink(component))
            {
                return ImportErrors.LinkRejected(component);
            }
        }

        bool exists = requireDirectory ? Directory.Exists(fullPath) : File.Exists(fullPath);
        return exists ? containingRoot : ImportErrors.PathNotFound(path);
    }

    /// <summary>
    ///     Trailing-separator-safe containment so a sibling such as "/x/allowed-evil" never
    ///     matches the allowed root "/x/allowed". Ordinal comparison is intentionally strict.
    /// </summary>
    private static bool IsContainedIn(string fullPath, string root) =>
        fullPath.Equals(root, StringComparison.Ordinal)
        || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Yields the allowed root and every accumulated component down to the target.</summary>
    private static IEnumerable<string> ComponentsFromRootDown(string root, string fullPath)
    {
        yield return root;

        if (fullPath.Equals(root, StringComparison.Ordinal))
        {
            yield break;
        }

        string current = root;
        foreach (string segment in fullPath[(root.Length + 1)..].Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    /// <summary>
    ///     Detects symlinks, junctions, and other reparse points via
    ///     <see cref="FileSystemInfo.LinkTarget" /> plus the ReparsePoint attribute. Non-existent
    ///     components are not links; existence is checked separately.
    /// </summary>
    private static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        return info.LinkTarget is not null
            || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);
    }
}
