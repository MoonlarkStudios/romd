using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;
namespace Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobArtworkSummary;

public sealed record GetJobArtworkSummaryQuery(Guid JobId) : IQuery<ArtworkCompletionSummaryDto>;
public sealed class GetJobArtworkSummaryQueryHandler(IJobRepository jobs, ICurrentUser user, IArtworkCompletionService service)
    : IQueryHandler<GetJobArtworkSummaryQuery, ArtworkCompletionSummaryDto>
{
    public async Task<ErrorOr<ArtworkCompletionSummaryDto>> HandleAsync(GetJobArtworkSummaryQuery query, CancellationToken ct = default)
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
        return new ArtworkCompletionSummaryDto(summary.Roles, summary.Review, summary.Failed, summary.Unfinished, summary.Working);
    }
}
