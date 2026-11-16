using System.IO.Abstractions;
using ErrorOr;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Import;

public sealed class PathImportPreparation(
    IRomdOptions options, IPathValidator validator, IFileSystem fileSystem) : IPathImportPreparation
{
    public ErrorOr<IPathImportPlan> Inspect(string sourcePath, CancellationToken ct = default)
    {
        var inspected = PathImportFiles.Inspect(options.DataDirectory, sourcePath, validator, fileSystem, ct);
        return inspected.IsError ? inspected.Errors : inspected.Value;
    }

    public Task<IPreparedJobWorkspace> ReserveAsync(Guid jobId, IJobWorkspaceLease lease, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string dataDirectory = options.DataDirectory;
        var owned = JobWorkspaceExclusion.RequireLease(lease);
        owned.BeginPreparation(dataDirectory, jobId);
        Directory.CreateDirectory(WorkspaceInputCopies.DirectoryFor(dataDirectory, jobId));
        return Task.FromResult<IPreparedJobWorkspace>(new PreparedJobWorkspace(dataDirectory, jobId, owned));
    }
}
