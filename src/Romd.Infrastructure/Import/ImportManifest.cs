using System.Text.Json;

namespace Romd.Infrastructure.Import;

/// <summary>
///     One staged source file in a move-mode manifest. Paths are stored root-relative (never
///     absolute) with '/' separators: <paramref name="SourceRelativePath" /> is relative to the
///     manifest's allowed root and is recombined with it at delete time;
///     <paramref name="WorkspaceRelativePath" /> joins the entry to the job's per-file
///     <see cref="Romd.Domain.Jobs.JobItem" /> provenance. <paramref name="SizeBytes" /> and
///     <paramref name="LastWriteTimeUtc" /> fingerprint the file as staged so a source that
///     changed after staging is never deleted.
/// </summary>
public sealed record ImportManifestEntry(
    string SourceRelativePath,
    string WorkspaceRelativePath,
    long SizeBytes,
    DateTime LastWriteTimeUtc);

/// <summary>
///     Move-mode source manifest (version 2). Lives outside the job workspace (a
///     "{jobId:N}.import.json" sibling under temp/jobs) so workspace cleanup can never delete it
///     and the classifier never ingests it. <see cref="AllowedRoot" /> is the configured allowed
///     import root that contained the source at staging time; every entry path is stored relative
///     to it. Orphaned manifests are resolved or aged out by the orphaned-job cleanup.
/// </summary>
public sealed record ImportManifest(
    int Version,
    string AllowedRoot,
    string SourceRoot,
    bool Move,
    IReadOnlyList<ImportManifestEntry> Entries)
{
    public const int CurrentVersion = 2;

    public static string PathFor(string dataDirectory, Guid jobId) =>
        Path.Combine(dataDirectory, "temp", "jobs", $"{jobId:N}.import.json");

    /// <summary>Normalizes a relative path to '/' separators for storage and joining.</summary>
    public static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    public static async Task WriteAsync(string path, ImportManifest manifest, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: ct);
    }

    public static async Task<ImportManifest?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ImportManifest>(stream, cancellationToken: ct);
    }
}
