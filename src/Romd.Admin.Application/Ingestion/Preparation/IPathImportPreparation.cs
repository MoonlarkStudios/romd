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

public sealed record PreparedPathImport(
    IPreparedJobWorkspace Workspace, ImmutableArray<ImportCopyIssue> CopyIssues);

public interface IPathImportPreparation
{
    ErrorOr<IPathImportPlan> Inspect(string sourcePath, CancellationToken ct = default);

    Task<ErrorOr<PreparedPathImport>> PrepareAsync(
        IPathImportPlan plan, Guid jobId, bool move, IJobWorkspaceLease lease, CancellationToken ct = default);
}
