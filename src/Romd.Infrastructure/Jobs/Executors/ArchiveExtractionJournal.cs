using System.Text.Json;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Infrastructure.Import;

namespace Romd.Infrastructure.Jobs.Executors;

/// <summary>
/// Worker-owned recovery state, outside the uploaded namespace. The caller holds the
/// workspace execution lock throughout reading, extraction, checkpointing and cleanup.
/// Atomic replacement protects against worker death; this is not a power-loss guarantee.
/// </summary>
internal sealed record ArchiveExtractionJournal(
    int Version,
    string Workspace,
    ArchiveExtractionLimits Limits,
    long RemainingEntries,
    long RemainingBytes,
    List<ArchiveExtractionWork> Pending,
    ArchiveExtractionWork? Cleanup)
{
    private const int CurrentVersion = 1;
    private const long MaxJournalBytes = 32L * 1024 * 1024;

    internal static string PathFor(string workspace) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + ".extraction.json";

    internal static ArchiveExtractionJournal Create(
        string workspace, ArchiveExtractionLimits limits, ExtractionBudget budget, IEnumerable<string> archives) =>
        new(CurrentVersion, Path.GetFullPath(workspace), limits,
            budget.RemainingEntries, budget.RemainingBytes,
            archives.Select(path => ArchiveExtractionWork.Create(workspace, path, 1)).ToList(), null);

    internal static async Task<ArchiveExtractionJournal?> ReadAsync(
        string workspace, ArchiveExtractionLimits limits, CancellationToken ct)
    {
        string path = PathFor(workspace);
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length > MaxJournalBytes)
            throw new JobPermanentFailureException("The extraction checkpoint exceeds its size limit.");
        await using var stream = File.OpenRead(path);
        ArchiveExtractionJournal state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<ArchiveExtractionJournal>(stream, cancellationToken: ct)
                ?? throw new JsonException("Empty checkpoint.");
        }
        catch (JsonException ex)
        {
            throw new JobPermanentFailureException($"The extraction checkpoint is invalid: {ex.Message}");
        }
        if (state.Version != CurrentVersion || state.Workspace != Path.GetFullPath(workspace)
            || state.Limits != limits || state.RemainingEntries < 0 || state.RemainingBytes < 0
            || state.RemainingEntries > limits.MaxTotalEntries || state.Pending is null
            || state.Pending.Count > limits.MaxTotalEntries)
            throw new JobPermanentFailureException("The extraction checkpoint does not match this workspace or policy.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Pending.Concat(state.Cleanup is { } cleanup ? [cleanup] : []))
        {
            if (item.Depth < 1 || item.Size < 0 || !paths.Add(item.Source))
                throw new JobPermanentFailureException("The extraction checkpoint contains invalid archive entries.");
            Resolve(workspace, item.Source);
            if (item.Target is not null)
            {
                Resolve(workspace, item.Target);
                if (Path.GetDirectoryName(item.Source) != Path.GetDirectoryName(item.Target)
                    || item.Source == item.Target)
                    throw new JobPermanentFailureException("The extraction checkpoint has an invalid output path.");
            }
        }
        if (state.Pending.Skip(1).Any(item => item.Target is not null)
            || state.Cleanup is { Target: null })
            throw new JobPermanentFailureException("The extraction checkpoint has an invalid publication state.");
        return state;
    }

    internal async Task SaveAsync(CancellationToken ct)
    {
        string path = PathFor(Workspace);
        string temporary = path + ".pending";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, this, cancellationToken: ct);
            if (stream.Length > MaxJournalBytes)
                throw new JobPermanentFailureException("The extraction checkpoint exceeds its size limit.");
            stream.Flush(flushToDisk: true);
        }
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
    }

    internal void FinishSourceCleanup()
    {
        if (Cleanup is not { } item)
            return;
        string target = Resolve(Workspace, item.Target!);
        if (!Directory.Exists(target))
            throw new JobPermanentFailureException("A completed extraction output is missing; its source was retained.");
        string source = Resolve(Workspace, item.Source);
        if (File.Exists(source))
        {
            item.ValidateSource(Workspace);
            File.Delete(source);
        }
    }

    internal static string Resolve(string workspace, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new JobPermanentFailureException("The extraction checkpoint has an invalid relative path.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        string absolute = Path.GetFullPath(Path.Combine(root, relative));
        if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.GetRelativePath(root, absolute) != relative)
            throw new JobPermanentFailureException("The extraction checkpoint path escapes its workspace.");
        // Never delete or descend through links, including a link planted in an output tree.
        for (string? current = absolute; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new JobPermanentFailureException("Extraction cannot traverse symbolic links.");
            if (current == root)
                break;
        }
        return absolute;
    }
}

internal sealed record ArchiveExtractionWork(string Source, int Depth, long Size, DateTime LastWriteTimeUtc, string? Target = null)
{
    internal static ArchiveExtractionWork Create(string root, string source, int depth)
    {
        var info = new FileInfo(source);
        return new(Path.GetRelativePath(root, source), depth, info.Length, info.LastWriteTimeUtc);
    }

    internal void ValidateSource(string root)
    {
        var info = new FileInfo(ArchiveExtractionJournal.Resolve(root, Source));
        if (!info.Exists || info.Length != Size || info.LastWriteTimeUtc != LastWriteTimeUtc)
            throw new JobPermanentFailureException("An archive changed or disappeared after extraction was checkpointed.");
    }
}
