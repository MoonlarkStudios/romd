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
    DateTime LastWriteTimeUtc)
{
    /// <summary>All leaf files from a fully extracted server-folder archive.</summary>
    public IReadOnlyList<string>? ArchiveMembers { get; init; }
}

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

    public bool ServerFolder { get; init; }

    public static string PathFor(string dataDirectory, Guid jobId) =>
        PathForWorkspace(Path.Combine(dataDirectory, "temp", "jobs", jobId.ToString("N")));

    internal static string PathForWorkspace(string workspace) =>
        Path.TrimEndingDirectorySeparator(workspace) + ".import.json";

    /// <summary>Normalizes a relative path to '/' separators for storage and joining.</summary>
    public static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.DirectorySeparatorChar, '/');

    public static async Task WriteAsync(string path, ImportManifest manifest, CancellationToken ct) =>
        await WriteOwnedAsync(File.Create(path), manifest, ct);

    internal async Task RecordArchiveMembersAsync(
        string path, string workspace, IReadOnlyDictionary<string, string> archiveRoots, CancellationToken ct)
    {
        if (archiveRoots.Count == 0)
            return;

        var entries = new List<ImportManifestEntry>(Entries.Count);
        foreach (var entry in Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!archiveRoots.TryGetValue(entry.WorkspaceRelativePath.Replace('/', Path.DirectorySeparatorChar), out string? target))
            {
                entries.Add(entry);
                continue;
            }

            var files = Directory.EnumerateFiles(Path.Combine(workspace, target), "*", SearchOption.AllDirectories)
                .Select(file => NormalizeRelativePath(Path.GetRelativePath(workspace, file))).ToArray();
            entries.Add(entry with { ArchiveMembers = files });
        }

        // The previous manifest remains valid if publication is interrupted. In that case
        // extraction redelivery restores the same archive roots and reconstructs the proof.
        string pending = path + ".pending";
        await WriteAsync(pending, this with { Entries = entries }, ct);
        ct.ThrowIfCancellationRequested();
        File.Move(pending, path, overwrite: true);
    }

    internal static async Task WriteOwnedAsync(Stream stream, ImportManifest manifest, CancellationToken ct)
    {
        Exception? primary = null;
        try
        {
            await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: ct);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch when (primary is not null)
            {
                // Preserve the serialization/write fault and its cancellation token through cleanup.
            }
        }
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
