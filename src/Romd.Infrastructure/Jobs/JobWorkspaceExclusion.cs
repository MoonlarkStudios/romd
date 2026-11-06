using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;

namespace Romd.Infrastructure.Jobs;

public sealed class JobWorkspaceExclusion(IRomdOptions options) : IJobWorkspaceExclusion
{
    public async Task<IJobWorkspaceLease> AcquireAsync(Guid jobId, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(options.DataDirectory);
        var handle = await JobWorkspace.AcquireExecutionLockAsync(root, jobId, ct);
        return new WorkspaceLease(root, jobId, handle);
    }

    internal sealed class WorkspaceLease(string root, Guid jobId, FileStream handle) : IJobWorkspaceLease
    {
        private FileStream? _handle = handle;
        private bool _prepared;
        public Guid JobId { get; } = jobId;

        internal void RequireHeld(string dataDirectory, Guid requestedJobId)
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            if (JobId != requestedJobId || root != Path.GetFullPath(dataDirectory))
                throw new ArgumentException("The workspace lease does not own this input.");
        }

        internal void BeginPreparation(string dataDirectory, Guid requestedJobId)
        {
            RequireHeld(dataDirectory, requestedJobId);
            if (_prepared)
                throw new InvalidOperationException("A workspace lease can prepare input only once.");
            _prepared = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _handle, null) is { } owned)
                await owned.DisposeAsync();
        }
    }

    internal static WorkspaceLease RequireLease(IJobWorkspaceLease lease) => lease as WorkspaceLease
        ?? throw new ArgumentException("The workspace lease was not issued by this workspace adapter.", nameof(lease));
}
