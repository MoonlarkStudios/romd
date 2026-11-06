using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;
namespace Romd.Admin.Application.Ingestion.Jobs.Queries.ListJobs;

public sealed record ListJobsQuery(string? Query = null, string? Outcome = null, string? JobType = null,
    string Archive = "active", DateTimeOffset? From = null, DateTimeOffset? Before = null,
    string? Cursor = null, int Limit = 50, Guid? CorrelationId = null) : IQuery<Page<JobDto>>
{ public const int MaxLimit = 100; }

public sealed class ListJobsQueryHandler(IReferenceCatalogService referenceCatalog, IJobRepository repository, ICurrentUser user)
    : IQueryHandler<ListJobsQuery, Page<JobDto>>
{
    public async Task<ErrorOr<Page<JobDto>>> HandleAsync(ListJobsQuery query, CancellationToken ct = default)
    {
        if (query.Limit is < 1 or > ListJobsQuery.MaxLimit || query.Query?.Length > 200 ||
            !JobFilters.Allows(query.Outcome, JobFilters.Outcomes) || !JobFilters.Allows(query.JobType, JobFilters.Types) ||
            !JobFilters.Archives.Contains(query.Archive) || query.From >= query.Before)
            return Error.Validation("Jobs.InvalidFilter", "Use supported job filters, a query of at most 200 characters, and a limit from 1 to 100.");
        string? search = string.IsNullOrWhiteSpace(query.Query) ? null : query.Query.Trim();
        var from = query.From?.ToUniversalTime();
        var before = query.Before?.ToUniversalTime();
        var visibility = JobVisibility.ScopeFor(user);
        string fingerprint = KeysetCursor.Fingerprint("jobs", "createdAt-desc/id-desc", visibility, query.JobType,
            query.Outcome, query.Archive, query.CorrelationId, search?.ToLowerInvariant(), from, before);
        var cursor = KeysetCursor.Decode<DateIdPosition>(query.Cursor, fingerprint, position => position.IsValid);
        if (cursor.IsError)
            return JobErrors.InvalidCursor();
        if (visibility.Empty)
            return new Page<JobDto> { Items = [], HasNextPage = false };
        var jobs = await repository.ListAsync(new JobListFilter(visibility.OwnerId, search, query.Outcome, query.JobType,
            query.Archive, from, before, cursor.Value?.CreatedAt.ToUniversalTime(), cursor.Value?.Id,
            query.Limit, visibility.IncludeAdminOnly, query.CorrelationId), ct);
        var page = jobs.Take(query.Limit).ToArray();
        var keys = await referenceCatalog.GetSystemKeysAsync(ct);
        return new Page<JobDto>
        {
            Items = page.Select(job => job.ToContract(keys)).ToArray(),
            HasNextPage = jobs.Count > query.Limit,
            NextCursor = jobs.Count > query.Limit ? KeysetCursor.Encode(new DateIdPosition(page[^1].CreatedAt.ToUniversalTime(), page[^1].Id), fingerprint) : null
        };
    }
}
