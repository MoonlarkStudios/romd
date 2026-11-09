using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;
using ArtworkCompletionJob = Romd.Domain.Jobs.ArtworkCompletionJob;

namespace Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobArtworkItems;

public sealed record GetJobArtworkItemsQuery(Guid JobId, string? Status = null, string? Cursor = null, int Limit = 50) : IQuery<Page<ArtworkCompletionItem>>;
public sealed class GetJobArtworkItemsQueryHandler(IJobRepository jobs, ICurrentUser user, IArtworkCompletionService service)
    : IQueryHandler<GetJobArtworkItemsQuery, Page<ArtworkCompletionItem>>
{
    public async Task<ErrorOr<Page<ArtworkCompletionItem>>> HandleAsync(GetJobArtworkItemsQuery query, CancellationToken ct = default)
    {
        if (query.Limit is < 1 or > 100 || !JobFilters.Allows(query.Status, JobFilters.ArtworkStatuses))
            return Error.Validation("Jobs.InvalidFilter", "Use a supported artwork status and limit from 1 to 100.");
        var job = await jobs.GetVisibleAsync(query.JobId, user, ct);
        if (job is null)
            return JobErrors.NotFound(query.JobId);
        if (job is not ArtworkCompletionJob)
            return JobErrors.FeedUnavailable();
        string fingerprint = KeysetCursor.Fingerprint("job-artwork-items", "titleId-asc", JobVisibility.ScopeFor(user), query.JobId, query.Status);
        var cursor = KeysetCursor.Decode<IntegerPosition>(query.Cursor, fingerprint, position => position.IsValid);
        if (cursor.IsError)
            return JobErrors.InvalidCursor();
        var result = await service.ItemsAsync(query.JobId, cursor.Value?.Id ?? 0, query.Status, ct, query.Limit);
        if (result.IsError)
            return result.Errors;
        var page = result.Value;
        return new Page<ArtworkCompletionItem>
        {
            Items = page.Items,
            HasNextPage = page.NextCursor is not null,
            NextCursor = page.NextCursor is { } next ? KeysetCursor.Encode(new IntegerPosition(Sqid.Parse(next, null).Value), fingerprint) : null
        };
    }
}
