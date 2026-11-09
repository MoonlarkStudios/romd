using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;
using ArtworkCompletionJob = Romd.Domain.Jobs.ArtworkCompletionJob;

namespace Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobArtworkSummary;

public sealed record GetJobArtworkSummaryQuery(Guid JobId) : IQuery<ArtworkCompletionSummary>;
public sealed class GetJobArtworkSummaryQueryHandler(IJobRepository jobs, ICurrentUser user, IArtworkCompletionService service)
    : IQueryHandler<GetJobArtworkSummaryQuery, ArtworkCompletionSummary>
{
    public async Task<ErrorOr<ArtworkCompletionSummary>> HandleAsync(GetJobArtworkSummaryQuery query, CancellationToken ct = default)
    {
        var job = await jobs.GetVisibleAsync(query.JobId, user, ct);
        if (job is null)
            return JobErrors.NotFound(query.JobId);
        if (job is not ArtworkCompletionJob)
            return JobErrors.FeedUnavailable();
        var result = await service.GetAsync(query.JobId, ct);
        if (result.IsError)
            return result.Errors;
        var summary = result.Value;
        return new ArtworkCompletionSummary(summary.Roles, summary.Review, summary.Failed, summary.Unfinished, summary.Working);
    }
}
