using System.Collections.Immutable;
using System.IO.Abstractions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Import;

public sealed class PathImportPreparation(
    IRomdOptions options, IPathValidator validator, IFileSystem fileSystem,
    TimeProvider clock, ILogger<PathImportPreparation> logger) : IPathImportPreparation
{
    public ErrorOr<IPathImportPlan> Inspect(string sourcePath, CancellationToken ct = default)
    {
        var inspected = PathImportFiles.Inspect(options.DataDirectory, sourcePath, validator, fileSystem, ct);
        return inspected.IsError ? inspected.Errors : inspected.Value;
    }

    public async Task<ErrorOr<PreparedPathImport>> PrepareAsync(
        IPathImportPlan plan, Guid jobId, bool move, IJobWorkspaceLease lease, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string dataDirectory = options.DataDirectory;
        if (plan is not InspectedPathImport inspected || inspected.DataDirectory != Path.GetFullPath(dataDirectory))
            throw new ArgumentException("The path-import plan was not issued for this workspace adapter.", nameof(plan));
        var owned = JobWorkspaceExclusion.RequireLease(lease);
        owned.BeginPreparation(dataDirectory, jobId);
        var issues = ImmutableArray.CreateBuilder<ImportCopyIssue>();
        try
        {
            var copied = await PathImportFiles.PopulateAsync(inspected, jobId, validator, fileSystem, clock, issues.Add, ct);
            if (copied.Count == 0)
            {
                PathImportFiles.DeleteWorkspaceAndManifest(dataDirectory, jobId, logger);
                return ImportErrors.NoFilesFound(inspected.RequestedPath);
            }
            await PathImportFiles.WriteManifestAsync(inspected, jobId, move, copied, ct);
            return new PreparedPathImport(new PreparedJobWorkspace(dataDirectory, jobId, owned), issues.ToImmutable());
        }
        catch
        {
            PathImportFiles.DeleteWorkspaceAndManifest(dataDirectory, jobId, logger);
            throw;
        }
    }
}
