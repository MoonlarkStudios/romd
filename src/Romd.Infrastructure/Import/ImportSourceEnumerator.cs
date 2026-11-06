using ErrorOr;
using Romd.Admin.Application.Ingestion.Import;

namespace Romd.Infrastructure.Import;

/// <summary>A candidate import file with its path relative to the import source root.</summary>
public sealed record ImportFile(string AbsolutePath, string RelativePath, long SizeBytes);

/// <summary>Importable files plus per-entry skip reasons surfaced as job errors.</summary>
public sealed record ImportEnumeration(
    IReadOnlyList<ImportFile> Files,
    IReadOnlyList<(string Path, string Reason)> Skipped);

/// <summary>
///     Iterative top-down enumeration of importable files. Every directory is validated
///     (containment plus per-component link walk) BEFORE it is descended into, so a symlinked
///     directory — including a loop back into an ancestor — is skipped instead of followed, and
///     every observed entry (file or directory, accepted or rejected) counts against a hard cap so
///     a pathological tree terminates deterministically with Import.TooManyFiles. Per-entry
///     failures are isolated as skip reasons instead of aborting the enumeration.
/// </summary>
public sealed class ImportSourceEnumerator
{
    private readonly IPathValidator _pathValidator;

    public ImportSourceEnumerator(IPathValidator pathValidator) => _pathValidator = pathValidator;

    public ErrorOr<ImportEnumeration> Enumerate(string sourceRoot, int maxObservedEntries, CancellationToken ct)
    {
        var files = new List<ImportFile>();
        var skipped = new List<(string Path, string Reason)>();
        var pending = new Queue<string>();
        pending.Enqueue(sourceRoot);
        int observed = 0;

        while (pending.TryDequeue(out string? directory))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++observed > maxObservedEntries)
                    {
                        return ImportErrors.TooManyFiles(observed, maxObservedEntries);
                    }

                    if (Directory.Exists(entry))
                    {
                        var directoryValidation = _pathValidator.ValidateImportRoot(entry);
                        if (directoryValidation.IsError)
                        {
                            skipped.Add((entry, directoryValidation.FirstError.Description));
                        }
                        else
                        {
                            pending.Enqueue(entry);
                        }

                        continue;
                    }

                    AddFile(files, skipped, sourceRoot, entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add((directory, $"Failed to enumerate directory: {ex.Message}"));
            }
        }

        return new ImportEnumeration(files, skipped);
    }

    private void AddFile(
        List<ImportFile> files,
        List<(string Path, string Reason)> skipped,
        string sourceRoot,
        string entry)
    {
        var validation = _pathValidator.ValidateImportFile(entry);
        if (validation.IsError)
        {
            skipped.Add((entry, validation.FirstError.Description));
            return;
        }

        try
        {
            files.Add(new ImportFile(entry, Path.GetRelativePath(sourceRoot, entry), new FileInfo(entry).Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add((entry, $"Failed to read file metadata: {ex.Message}"));
        }
    }
}
