using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Models;
namespace Romd.Admin.Application.Ingestion.Jobs.Queries.GetJob;

public sealed record GetJobQuery(Guid JobId) : IQuery<JobDto>;
public sealed class GetJobQueryHandler(IJobRepository jobs, ICurrentUser user, IReferenceCatalogService referenceCatalog) : IQueryHandler<GetJobQuery, JobDto>
{
    public async Task<ErrorOr<JobDto>> HandleAsync(GetJobQuery query, CancellationToken ct = default) =>
        await jobs.GetVisibleAsync(query.JobId, user, ct) is { } job
            ? job.ToContract(await referenceCatalog.GetSystemKeysAsync(ct)) : JobErrors.NotFound(query.JobId);
}
