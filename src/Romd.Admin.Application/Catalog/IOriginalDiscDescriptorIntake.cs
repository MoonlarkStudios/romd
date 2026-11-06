using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Catalog;

public interface IOriginalDiscDescriptorIntake
{
    /// <summary>Durably records original descriptor and upload outcome in the same fenced transaction.</summary>
    Task<JobItem> IntakeAsync(Stream stream, Guid jobId, string relativePath, CancellationToken ct);
    /// <summary>Records exact successful sibling hashes after all ROM outcomes have committed.</summary>
    Task ReconcileAsync(Guid jobId, CancellationToken ct);
}
