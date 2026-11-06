using ErrorOr;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Import;

namespace Romd.Infrastructure.Jobs;

public sealed class UploadWorkspacePreparation(IRomdOptions options) : IUploadWorkspacePreparation
{
    public async Task<ErrorOr<IPreparedJobWorkspace>> PrepareAsync(
        JobWorkspaceInput input, Stream source, IJobWorkspaceLease lease, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WorkspaceInputCopies.ValidateInput(input);
        string dataDirectory = options.DataDirectory;
        var owned = JobWorkspaceExclusion.RequireLease(lease);
        owned.BeginPreparation(dataDirectory, input.JobId);
        var copied = await WorkspaceInputCopies.CopyUploadAsync(dataDirectory, input, source, ct);
        return copied.IsError ? copied.Errors : new PreparedJobWorkspace(dataDirectory, input.JobId, owned);
    }
}

public sealed class ReplaceDatWorkspacePreparation(IRomdOptions options) : IReplaceDatWorkspacePreparation
{
    public async Task<IPreparedJobWorkspace> PrepareAsync(
        JobWorkspaceInput input, Stream source, IJobWorkspaceLease lease, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WorkspaceInputCopies.ValidateInput(input);
        string dataDirectory = options.DataDirectory;
        var owned = JobWorkspaceExclusion.RequireLease(lease);
        owned.BeginPreparation(dataDirectory, input.JobId);
        // Complete or partial replacement bytes are retained on faults, matching the legacy copy contract.
        await WorkspaceInputCopies.CopyReplacementAsync(dataDirectory, input, source, ct);
        return new PreparedJobWorkspace(dataDirectory, input.JobId, owned);
    }
}

internal sealed class PreparedJobWorkspace(
    string dataDirectory, Guid jobId, JobWorkspaceExclusion.WorkspaceLease lease) : IPreparedJobWorkspace
{
    private bool _discarded;
    public Guid JobId { get; } = jobId;

    public Task DiscardAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lease.RequireHeld(dataDirectory, JobId);
        if (_discarded)
            return Task.CompletedTask;
        string workspace = WorkspaceInputCopies.DirectoryFor(dataDirectory, JobId);
        if (Directory.Exists(workspace))
            Directory.Delete(workspace, recursive: true);
        string manifest = ImportManifest.PathFor(dataDirectory, JobId);
        if (File.Exists(manifest))
            File.Delete(manifest);
        _discarded = true;
        return Task.CompletedTask;
    }
}
