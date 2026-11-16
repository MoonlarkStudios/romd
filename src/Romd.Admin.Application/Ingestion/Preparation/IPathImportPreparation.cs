using System.Collections.Immutable;
using ErrorOr;

namespace Romd.Admin.Application.Ingestion.Preparation;

public sealed record ImportPreparationIssue(string Path, string Message);
public sealed record ImportCopyIssue(string Path, string Message, DateTimeOffset OccurredAt);

/// <summary>Source facts, not ownership of a workspace or its paths.</summary>
public sealed record PathImportFacts(
    string SourceRoot, string SourceFilename, int FileCount, ImmutableArray<ImportPreparationIssue> Skipped);

/// <summary>Opaque enumerated input. Every file is revalidated immediately before opening it.</summary>
public interface IPathImportPlan
{
    PathImportFacts Facts { get; }
}

/// <summary>
///     Admin-side path-import acceptance. Inspection validates the source and its capacity so the
///     request fails fast; the worker re-inspects and copies it, so acceptance never waits for bytes.
/// </summary>
public interface IPathImportPreparation
{
    ErrorOr<IPathImportPlan> Inspect(string sourcePath, CancellationToken ct = default);

    /// <summary>
    ///     Reserves the empty job workspace the worker later copies the source into. The runner
    ///     executes only jobs whose workspace exists, under the same cross-process lock.
    /// </summary>
    Task<IPreparedJobWorkspace> ReserveAsync(Guid jobId, IJobWorkspaceLease lease, CancellationToken ct = default);
}
