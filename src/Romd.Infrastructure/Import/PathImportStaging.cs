using System.Collections.Immutable;
using System.IO.Abstractions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Worker-side copy of a path-import source into its job workspace, run in the job's Copying
///     phase. Every attempt starts from an empty workspace and re-inspects the source under the
///     worker's own allowlist, so a redelivered copy never mixes bytes from different attempts.
///     Sources are only read; move-mode deletion stays with completion cleanup.
/// </summary>
public sealed class PathImportStaging(
    IRomdOptions options, IPathValidator validator, IFileSystem fileSystem,
    TimeProvider clock, ILogger<PathImportStaging> logger, IServerImportStore serverImports)
{
    /// <summary>Issues to record on the job, in source order: skipped entries, then failed copies.</summary>
    public sealed record StagedSource(bool HasFiles, ImmutableArray<JobError> Issues);

    public async Task<ErrorOr<StagedSource>> StageAsync(
        UploadJob job, string workspace, Func<string, CancellationToken, Task> onFile, CancellationToken ct)
    {
        if (job.ImportSourcePath is not { } sourcePath)
            throw new InvalidOperationException("Only path imports have a source to copy.");
        string dataDirectory = options.DataDirectory;
        if (Path.GetFullPath(workspace) != Path.GetFullPath(WorkspaceInputCopies.DirectoryFor(dataDirectory, job.Id)))
            throw new ArgumentException("The workspace does not belong to this job.", nameof(workspace));

        Reset(dataDirectory, job.Id, workspace);
        var selection = await serverImports.ReadSelectionAsync(job.Id, ct);
        var inspected = selection is null
            ? PathImportFiles.Inspect(dataDirectory, sourcePath, validator, fileSystem, ct)
            : PathImportFiles.InspectSelection(dataDirectory, sourcePath, selection, validator, fileSystem);
        if (inspected.IsError)
            return inspected.Errors;
        var plan = inspected.Value;
        var issues = ImmutableArray.CreateBuilder<JobError>();
        foreach (var skipped in plan.Facts.Skipped)
            issues.Add(new JobError(skipped.Path, skipped.Message, clock.GetUtcNow()));
        try
        {
            var copied = await PathImportFiles.PopulateAsync(plan, job.Id, validator, fileSystem, clock,
                issue => issues.Add(new JobError(issue.Path, issue.Message, issue.OccurredAt)), ct,
                (file, token) => onFile(file.RelativePath, token));
            if (copied.Count == 0)
            {
                Reset(dataDirectory, job.Id, workspace);
                return new StagedSource(false, issues.ToImmutable());
            }
            await PathImportFiles.WriteManifestAsync(plan, job.Id, job.ImportMove, selection is not null, copied, ct);
        }
        catch (Exception primary)
        {
            // A partial copy or manifest must never reach extraction; the next attempt starts over
            // from an empty workspace anyway, so a cleanup fault here cannot replace the cause.
            try
            {
                Reset(dataDirectory, job.Id, workspace);
            }
            catch (Exception cleanup)
            {
                logger.LogWarning(cleanup, "Path import {JobId} could not empty its workspace after {Fault}",
                    job.Id, primary.GetType().Name);
            }
            throw;
        }
        logger.LogInformation("Path import {JobId} copied its source into the workspace with {IssueCount} issue(s)",
            job.Id, issues.Count);
        return new StagedSource(true, issues.ToImmutable());
    }

    /// <summary>Empties the workspace, keeping the directory the runner located, and drops any move manifest.</summary>
    private static void Reset(string dataDirectory, Guid jobId, string workspace)
    {
        if (Directory.Exists(workspace))
        {
            foreach (string directory in Directory.EnumerateDirectories(workspace))
                Directory.Delete(directory, recursive: true);
            foreach (string file in Directory.EnumerateFiles(workspace))
                File.Delete(file);
        }
        else
        {
            Directory.CreateDirectory(workspace);
        }
        JobWorkspace.DeleteExtractionCheckpoint(workspace);
        string manifest = ImportManifest.PathFor(dataDirectory, jobId);
        File.Delete(manifest);
        File.Delete(manifest + ".pending");
    }
}
