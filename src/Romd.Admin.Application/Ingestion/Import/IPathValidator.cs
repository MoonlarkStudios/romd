using ErrorOr;

namespace Romd.Admin.Application.Ingestion.Import;

/// <summary>
///     Validates server-side import paths against the configured allowlist. Every path must be
///     absolute, canonically contained under an allowed root, and free of symbolic links,
///     junctions, or other reparse points on every component from the allowed root down.
/// </summary>
public interface IPathValidator
{
    /// <summary>
    ///     Validates a directory as the root of an import. On success returns the canonical
    ///     (trimmed, fully qualified) configured allowed root that contains the directory.
    /// </summary>
    ErrorOr<string> ValidateImportRoot(string path);

    /// <summary>
    ///     Re-validates a single file. Callers run this immediately before opening the file so a
    ///     path swapped for a link between enumeration and read is rejected.
    /// </summary>
    ErrorOr<Success> ValidateImportFile(string path);
}
