using Romd.Application.Common.ReferenceCatalog;
using System.Text.Json;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Models;
using Romd.Domain.Identity;

namespace Romd.Admin.Application.Ingestion.Jobs.Queries.GetJobHistory;

public sealed record GetJobHistoryQuery(
    string? Search = null,
    string? Outcome = null,
    string? JobType = null,
    string Archive = "active",
    DateTimeOffset? From = null,
    DateTimeOffset? Before = null,
    string? Cursor = null,
    int Limit = 50) : IQuery<JobHistoryPage>;

public sealed class GetJobHistoryQueryHandler(IReferenceCatalogService referenceCatalog, IJobRepository repository, ICurrentUser currentUser)
    : IQueryHandler<GetJobHistoryQuery, JobHistoryPage>
{
    private static readonly string[] Outcomes = ["queued", "running", "completed", "partial", "failed", "cancelled", "deferred", "unknown"];
    private static readonly string[] JobTypes = ["upload", "replace_dat", "enrichment", "bulk_enrichment", "export", "materialization", "artwork-import"];

    public async Task<ErrorOr<JobHistoryPage>> HandleAsync(GetJobHistoryQuery query, CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        if (query.Limit is < 1 or > 100 || query.Search?.Length > 200 ||
            (query.Outcome is not null && !Outcomes.Contains(query.Outcome)) ||
            (query.JobType is not null && !JobTypes.Contains(query.JobType)) ||
            query.Archive is not ("active" or "archived" or "all") ||
            (query.From.HasValue && query.Before.HasValue && query.From >= query.Before))
            return Error.Validation("Jobs.InvalidFilter", "Use valid history filters, a search of at most 200 characters, and a limit from 1 to 100.");

        CursorPosition? cursor = null;
        if (query.Cursor is not null)
        {
            try
            {
                if (query.Cursor.Length > 512) throw new FormatException();
                cursor = JsonSerializer.Deserialize<CursorPosition>(Convert.FromBase64String(query.Cursor));
                if (cursor is null || cursor.Id == Guid.Empty || cursor.CreatedAt == default) throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return Error.Validation("Jobs.InvalidCursor", "The history cursor is invalid. Start from the first page.");
            }
        }

        // Non-managers can only inspect their own work, including archived history.
        Guid? owner = currentUser.HasRole(RomdRoleType.Manager) ? null : currentUser.UserId;
        if (!currentUser.HasRole(RomdRoleType.Manager) && owner is null)
            return new JobHistoryPage([], null);

        var jobs = await repository.GetHistoryAsync(new JobHistoryFilter(
            owner, query.Search?.Trim(), query.Outcome, query.JobType, query.Archive,
            query.From?.ToUniversalTime(), query.Before?.ToUniversalTime(),
            cursor?.CreatedAt.ToUniversalTime(), cursor?.Id, query.Limit), ct);
        var page = jobs.Take(query.Limit).ToList();
        string? next = jobs.Count > query.Limit
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new CursorPosition(page[^1].CreatedAt, page[^1].Id)))
            : null;
        return new JobHistoryPage(page.Select(job => job.ToContract(systemKeys)).ToList(), next);
    }

    private sealed record CursorPosition(DateTimeOffset CreatedAt, Guid Id);
}
