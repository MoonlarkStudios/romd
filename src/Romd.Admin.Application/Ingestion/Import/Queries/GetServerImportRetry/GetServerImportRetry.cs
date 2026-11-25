using ErrorOr;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Ingestion.Import.Queries.GetServerImportRetry;

public sealed record GetServerImportRetryQuery(Guid JobId) : IQuery<ServerImportRetryReview>;

public sealed class GetServerImportRetryQueryHandler(
    IServerImportOptions options, IServerImportStore store, ServerImportRetryPlanner planner)
    : IQueryHandler<GetServerImportRetryQuery, ServerImportRetryReview>
{
    public async Task<ErrorOr<ServerImportRetryReview>> HandleAsync(GetServerImportRetryQuery query, CancellationToken ct = default)
    {
        if (options.ServerImportDirectory is not { } directory)
            return ImportErrors.RetryUnavailable();

        var activity = await store.ReadActivityAsync(directory, ct);
        var latest = await store.ReadJobAsync(activity?.JobId, ct);
        var plan = await planner.ReviewAsync(directory, query.JobId, latest, ct);
        return plan.Match<ErrorOr<ServerImportRetryReview>>(value => value.Review, errors => errors);
    }
}
