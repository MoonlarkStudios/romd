using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;
using Romd.Domain.Jobs;
namespace Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobItems;

public sealed record GetJobItemsQuery(Guid JobId, string? Outcome = null, int Limit = 100,
    string? Cursor = null, string? Query = null) : IQuery<Page<JobItemDto>>
{ public const int MaxLimit = 100; }

public sealed class GetJobItemsQueryHandler(IJobItemRepository repository, IReadSnapshotTransactionFactory readSnapshots,
    IJobRepository jobs, ICurrentUser user, IReferenceCatalogService referenceCatalog,
    ILogger<GetJobItemsQueryHandler>? logger = null) : IQueryHandler<GetJobItemsQuery, Page<JobItemDto>>
{
    public async Task<ErrorOr<Page<JobItemDto>>> HandleAsync(GetJobItemsQuery query, CancellationToken ct = default)
    {
        if (query.Limit is < 1 or > MaxLimit || query.Query?.Length > 200 || !JobFilters.Allows(query.Outcome, JobFilters.ItemOutcomes))
            return Error.Validation("Jobs.InvalidFilter", "Use a supported item outcome, query of at most 200 characters, and limit from 1 to 100.");
        var job = await jobs.GetVisibleAsync(query.JobId, user, ct);
        if (job is null)
            return JobErrors.NotFound(query.JobId);
        if (job is not UploadJob)
            return JobErrors.FeedUnavailable();
        string? search = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
        var visibility = JobVisibility.ScopeFor(user);
        string fingerprint = KeysetCursor.Fingerprint("job-items", "createdAt-asc/id-asc", visibility, query.JobId, query.Outcome, search?.ToLowerInvariant());
        var cursor = KeysetCursor.Decode<DateIdPosition>(query.Cursor, fingerprint, position => position.IsValid);
        if (cursor.IsError)
            return JobErrors.InvalidCursor();
        var transaction = await readSnapshots.BeginAsync(ct);
        bool completed = false;
        Exception? primary = null;
        try
        {
            var position = cursor.Value is { } decoded ? new JobItemPosition(decoded.CreatedAt.ToUniversalTime(), decoded.Id) : null;
            var page = await repository.GetByJobAsync(query.JobId, JobItemMapping.ParseOutcome(query.Outcome), query.Limit, ct, position, search);
            var keys = await referenceCatalog.GetSystemKeysAsync(ct);
            await transaction.CompleteAsync(ct);
            completed = true;
            return new Page<JobItemDto>
            {
                Items = page.Items.Select(item => item.ToContract(keys)).ToArray(),
                HasNextPage = page.HasMore,
                NextCursor = page.HasMore && page.NextPosition is { } next ? KeysetCursor.Encode(new DateIdPosition(next.CreatedAt.ToUniversalTime(), next.Id), fingerprint) : null
            };
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            await ReadSnapshotCleanup.DisposeAsync([transaction], completed, primary, logger ?? NullLogger<GetJobItemsQueryHandler>.Instance);
        }
    }
    private const int MaxLimit = GetJobItemsQuery.MaxLimit;
}
